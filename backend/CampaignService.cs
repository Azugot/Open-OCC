using Microsoft.EntityFrameworkCore;

namespace Story;

public record CreateCampaign(string Name, bool Synthetic = true);
public record TurnRequest(Guid ExpectedCheckpointId, string Action);
public record ForkRequest(Guid CheckpointId, string Name);
public record ReviewDecision(Guid FactId, bool Accept, string Text, string Visibility);
public record ApproveImport(Guid ExpectedCheckpointId, StoryState State, ReviewDecision[] Decisions);

public static class CampaignService
{
    public static async Task<Branch> Create(StoryDb db, CreateCampaign request, CancellationToken ct, EngineSettings? settings = null)
    {
        RequireText(request.Name, 100, "Campaign name");
        var campaign = new Campaign { Name = request.Name.Trim() };
        var branch = new Branch { CampaignId = campaign.Id };
        var checkpoint = new Checkpoint { BranchId = branch.Id, Label = "Starting point", StateJson = Json.Write(request.Synthetic ? StoryState.Synthetic : StoryState.Empty) };
        var world = request.Synthetic ? StoryWorld.Crownspire() : StoryWorld.FromState(StoryState.Empty);
        world.Settings = settings ?? new(); world.Settings.Validate();
        checkpoint.WorldJson = Json.Write(world);
        branch.HeadCheckpointId = checkpoint.Id;
        db.AddRange(campaign, branch, checkpoint);
        db.Messages.Add(new Message { BranchId = branch.Id, Sequence = 0, Role = "assistant", Content = request.Synthetic
            ? "CROWNSPIRE ACADEMY · SYNTHETIC CAMPAIGN\n\nMorning light spills through the registration hall. A clerk arranges department forms while Lyra Fen waits beside the notice board, a practice sword at her side. Your first term is about to begin.\n\nWhat do you do?"
            : "Your campaign is ready. Import a transcript, review its evidence, and approve the scene you want to resume." });
        await db.SaveChangesAsync(ct);
        return branch;
    }

    public static async Task<Branch> Fork(StoryDb db, Guid branchId, ForkRequest request, CancellationToken ct)
    {
        RequireText(request.Name, 100, "Branch name");
        var original = await db.Branches.SingleOrDefaultAsync(x => x.Id == branchId, ct) ?? throw new KeyNotFoundException();
        var at = await db.Checkpoints.SingleOrDefaultAsync(x => x.Id == request.CheckpointId && x.BranchId == branchId, ct) ?? throw new KeyNotFoundException();
        var branch = new Branch { CampaignId = original.CampaignId, Name = request.Name.Trim(), ParentBranchId = branchId, ForkCheckpointId = at.Id };
        var snapshots = await db.Checkpoints.AsNoTracking().Where(x => x.BranchId == branchId && x.Sequence <= at.Sequence).ToListAsync(ct);
        foreach (var snapshot in snapshots)
        {
            var copy = new Checkpoint { BranchId = branch.Id, Sequence = snapshot.Sequence, Label = snapshot.Label, StateJson = snapshot.StateJson, WorldJson = snapshot.WorldJson, CreatedAt = snapshot.CreatedAt };
            if (snapshot.Id == at.Id) branch.HeadCheckpointId = copy.Id;
            db.Checkpoints.Add(copy);
        }
        var messages = await db.Messages.AsNoTracking().Where(x => x.BranchId == branchId && x.Sequence <= at.Sequence).ToListAsync(ct);
        foreach (var m in messages) db.Messages.Add(new Message { BranchId = branch.Id, Sequence = m.Sequence, Role = m.Role, Content = m.Content, Provider = m.Provider, Model = m.Model, CreatedAt = m.CreatedAt });
        var facts = await db.Facts.AsNoTracking().Where(x => x.BranchId == branchId && x.ReviewStatus == "accepted" && x.EffectiveSequence <= at.Sequence).ToListAsync(ct);
        foreach (var fact in facts)
        {
            var copy = new Fact { BranchId = branch.Id, EffectiveSequence = fact.EffectiveSequence, Text = fact.Text, Provenance = fact.Provenance, ReviewStatus = fact.ReviewStatus, Visibility = fact.Visibility };
            db.Facts.Add(copy);
            var evidence = await db.Evidence.Where(x => x.FactId == fact.Id).ToListAsync(ct);
            foreach (var link in evidence) db.Evidence.Add(new FactEvidence { FactId = copy.Id, SegmentId = link.SegmentId });
        }
        db.Branches.Add(branch);
        await db.SaveChangesAsync(ct);
        return branch;
    }

    public static async Task CommitTurn(StoryDb db, GenerationRun run, string narrative, CancellationToken ct, StoryState? nextState = null, StoryWorld? nextWorld = null)
    {
        var branch = await db.Branches.SingleAsync(x => x.Id == run.BranchId, ct);
        if (branch.HeadCheckpointId != run.ExpectedCheckpointId) throw new DbUpdateConcurrencyException("Branch changed. Reload before continuing.");
        var current = await db.Checkpoints.SingleAsync(x => x.Id == branch.HeadCheckpointId, ct);
        var state = nextState ?? Json.Read<StoryState>(current.StateJson);
        state.Validate();
        var checkpoint = new Checkpoint { BranchId = branch.Id, Sequence = current.Sequence + 2, Label = nextWorld is null ? "Simulated turn" : "Story turn", StateJson = Json.Write(state), WorldJson = nextWorld is null ? current.WorldJson : Json.Write(nextWorld) };
        db.Messages.AddRange(
            new Message { BranchId = branch.Id, Sequence = current.Sequence + 1, Role = "user", Content = run.Action, Provider = run.Provider, Model = run.Model },
            new Message { BranchId = branch.Id, Sequence = current.Sequence + 2, Content = narrative, Provider = run.Provider, Model = run.Model });
        db.Checkpoints.Add(checkpoint);
        branch.HeadCheckpointId = checkpoint.Id;
        branch.Revision++;
        run.Status = "completed";
        await db.SaveChangesAsync(ct);
    }

    public static async Task Approve(StoryDb db, Guid id, ApproveImport request, CancellationToken ct)
    {
        if (request.State is null) throw new InvalidOperationException("Provide a complete resume state.");
        request.State.Validate();
        var job = await db.ImportJobs.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException();
        if (job.Status != "review") throw new InvalidOperationException("Only a completed, unapproved review can be approved.");
        var branch = await db.Branches.SingleAsync(x => x.Id == job.BranchId, ct);
        if (branch.HeadCheckpointId != request.ExpectedCheckpointId || branch.HeadCheckpointId != job.ExpectedCheckpointId)
            throw new InvalidOperationException("This branch advanced after the import started. Fork the original checkpoint and import there to avoid overwriting newer state.");
        var candidates = await db.Facts.Where(x => x.JobId == id).ToListAsync(ct);
        if (request.Decisions is null || request.Decisions.Length != candidates.Count || request.Decisions.Select(x => x.FactId).Distinct().Count() != candidates.Count || request.Decisions.Any(x => candidates.All(f => f.Id != x.FactId)))
            throw new InvalidOperationException("Review every candidate exactly once before approval.");
        var current = await db.Checkpoints.SingleAsync(x => x.Id == branch.HeadCheckpointId, ct);
        foreach (var decision in request.Decisions)
        {
            RequireText(decision.Text, 16000, "Fact text");
            if (decision.Visibility is not ("public" or "narrator")) throw new InvalidOperationException("Visibility must be public or narrator.");
            var fact = candidates.Single(x => x.Id == decision.FactId);
            fact.Text = decision.Text;
            fact.Visibility = decision.Visibility;
            fact.ReviewStatus = decision.Accept ? "accepted" : "rejected";
            fact.Provenance = decision.Accept ? "user-confirmed" : "imported";
            fact.EffectiveSequence = current.Sequence + 1;
        }
        // Imports establish a new scene. Rebuild neutral participants rather than carrying unrelated synthetic secrets.
        var importedWorld = StoryWorld.FromState(request.State);
        importedWorld.Settings = StoryWorld.From(current).Settings;
        var checkpoint = new Checkpoint { BranchId = branch.Id, Sequence = current.Sequence + 1, Label = "Approved import", StateJson = Json.Write(request.State), WorldJson = Json.Write(importedWorld) };
        db.Checkpoints.Add(checkpoint);
        db.Messages.Add(new Message { BranchId = branch.Id, Sequence = checkpoint.Sequence, Role = "system", Provider = "manual-review", Model = "deterministic-v1", Content = $"Import approved by you. Resume at {request.State.Location} · {request.State.Time}. {request.State.Objective}" });
        branch.HeadCheckpointId = checkpoint.Id;
        branch.Revision++;
        job.Status = "approved";
        await db.SaveChangesAsync(ct);
    }
    public static void RequireText(string? text, int max, string name)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > max) throw new InvalidOperationException($"{name} must contain 1–{max} characters.");
    }
}
