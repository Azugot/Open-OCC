using Microsoft.EntityFrameworkCore;

namespace Story;

public record CreateCampaign(string Name, bool Synthetic = true, Guid? WorldVersionId = null);
public record TurnRequest(Guid ExpectedCheckpointId, string Action);
public record ForkRequest(Guid CheckpointId, string Name);
public record ReviewDecision(Guid FactId, bool Accept, string Text, string Visibility, string Kind = "fact", double Confidence = 1, string[]? KnownBy = null);
public record ThreadDecision(string Title, string Details, string Kind, string Status, int Importance, bool Accept = true);
public record ApproveImport(Guid ExpectedCheckpointId, StoryState State, ReviewDecision[] Decisions, ThreadDecision[]? Threads = null);
public record CorrectFactRequest(Guid ExpectedCheckpointId, string Text, string Reason, string Visibility = "public", string Kind = "fact", double Confidence = 1, string[]? KnownBy = null);
public record CreateWorldRequest(string Name, string Description = "");
public record CreateWorldVersionRequest(string RulesJson = "{}", string LocationsJson = "[]", string FactionsJson = "[]", string AuthorInstructions = "");
public record CreateCharacterRequest(string Name, string Description = "", string Goals = "", string Status = "active");
public record UpsertRelationshipRequest(Guid FromCharacterId, Guid ToCharacterId, string Label, int Score = 0, string Notes = "");
public record UpsertKnowledgeRequest(Guid CharacterId, Guid? FactId, string Subject, string BeliefType = "fact", double Confidence = 1);
public record ApplyMechanicsRequest(Guid ExpectedCheckpointId, string Kind, string Subject, int Delta, string Reason);

public static class CampaignService
{
    public static async Task<Branch> Create(StoryDb db, CreateCampaign request, CancellationToken ct, EngineSettings? settings = null)
    {
        RequireText(request.Name, 100, "Campaign name");
        var campaign = new Campaign { Name = request.Name.Trim(), WorldVersionId = request.WorldVersionId };
        if (request.WorldVersionId is { } worldVersionId && !await db.WorldVersions.AnyAsync(x => x.Id == worldVersionId, ct)) throw new KeyNotFoundException();
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
            var copy = new Fact { BranchId = branch.Id, EffectiveSequence = fact.EffectiveSequence, Text = fact.Text, Provenance = fact.Provenance, ReviewStatus = fact.ReviewStatus, Visibility = fact.Visibility, Kind = fact.Kind, Confidence = fact.Confidence, KnownByJson = fact.KnownByJson };
            db.Facts.Add(copy);
            var evidence = await db.Evidence.Where(x => x.FactId == fact.Id).ToListAsync(ct);
            foreach (var link in evidence) db.Evidence.Add(new FactEvidence { FactId = copy.Id, SegmentId = link.SegmentId });
        }
        var threads = await db.NarrativeThreads.AsNoTracking().Where(x => x.BranchId == branchId && x.EffectiveSequence <= at.Sequence).ToListAsync(ct);
        foreach (var thread in threads) db.NarrativeThreads.Add(new NarrativeThread { BranchId = branch.Id, Title = thread.Title, Details = thread.Details, Kind = thread.Kind, Status = thread.Status, Importance = thread.Importance, EffectiveSequence = thread.EffectiveSequence });
        var relationships = await db.Relationships.AsNoTracking().Where(x => x.BranchId == branchId && x.EffectiveSequence <= at.Sequence).ToListAsync(ct);
        foreach (var relationship in relationships) db.Relationships.Add(new Relationship { BranchId = branch.Id, FromCharacterId = relationship.FromCharacterId, ToCharacterId = relationship.ToCharacterId, Label = relationship.Label, Score = relationship.Score, Notes = relationship.Notes, EffectiveSequence = relationship.EffectiveSequence });
        var knowledge = await db.Knowledge.AsNoTracking().Where(x => x.BranchId == branchId && x.EffectiveSequence <= at.Sequence).ToListAsync(ct);
        foreach (var record in knowledge) db.Knowledge.Add(new KnowledgeRecord { BranchId = branch.Id, CharacterId = record.CharacterId, Subject = record.Subject, BeliefType = record.BeliefType, Confidence = record.Confidence, EffectiveSequence = record.EffectiveSequence, Source = record.Source });
        var mechanics = await db.Mechanics.AsNoTracking().Where(x => x.BranchId == branchId && x.Sequence <= at.Sequence).ToListAsync(ct);
        foreach (var entry in mechanics) db.Mechanics.Add(new MechanicsLedgerEntry { BranchId = branch.Id, Sequence = entry.Sequence, Kind = entry.Kind, Subject = entry.Subject, Delta = entry.Delta, Reason = entry.Reason, Source = entry.Source, CreatedAt = entry.CreatedAt });
        var events = await db.Events.AsNoTracking().Where(x => x.BranchId == branchId && x.Sequence <= at.Sequence).ToListAsync(ct);
        foreach (var storyEvent in events) db.Events.Add(new StoryEvent { BranchId = branch.Id, Sequence = storyEvent.Sequence, Type = storyEvent.Type, Summary = storyEvent.Summary, CreatedAt = storyEvent.CreatedAt });
        var summary = await db.Summaries.AsNoTracking().SingleOrDefaultAsync(x => x.BranchId == branchId && x.ThroughSequence <= at.Sequence, ct);
        if (summary is not null) db.Summaries.Add(new CampaignSummary { BranchId = branch.Id, ThroughSequence = summary.ThroughSequence, Text = summary.Text, UpdatedAt = summary.UpdatedAt });
        db.Branches.Add(branch);
        await db.SaveChangesAsync(ct);
        return branch;
    }

    public static async Task CommitTurn(StoryDb db, GenerationRun run, string narrative, CancellationToken ct, StoryState? nextState = null, StoryWorld? nextWorld = null)
    {
        var current = await db.Checkpoints.SingleAsync(x => x.Id == run.ExpectedCheckpointId, ct);
        await CommitTurn(db, run, narrative, nextState ?? Json.Read<StoryState>(current.StateJson), new(null, [], []), ct, nextWorld);
    }

    public static async Task CommitTurn(StoryDb db, GenerationRun run, string narrative, StoryState state, StateTransition transition, CancellationToken ct, StoryWorld? nextWorld = null)
    {
        var branch = await db.Branches.SingleAsync(x => x.Id == run.BranchId, ct);
        if (branch.HeadCheckpointId != run.ExpectedCheckpointId) throw new DbUpdateConcurrencyException("Branch changed. Reload before continuing.");
        var current = await db.Checkpoints.SingleAsync(x => x.Id == branch.HeadCheckpointId, ct);
        state.Validate();
        var autonomous = nextWorld is not null;
        if (nextWorld is null)
        {
            nextWorld = StoryWorld.From(current);
            nextWorld.SynchronizeScene(state);
            var observed = nextWorld.Characters.Where(c => nextWorld.Entities.Single(e => e.Id == c.EntityId).LocationId == nextWorld.PlayerLocationId).Select(c => c.EntityId).Append("player").ToArray();
            nextWorld.Events.Add(new("narration-" + run.Id.ToString("N"), nextWorld.Events.Count, 0, "director", nextWorld.PlayerLocationId, narrative[..Math.Min(narrative.Length, 4000)], observed, [nextWorld.PlayerLocationId], state.Time, "narration"));
        }
        var checkpoint = new Checkpoint { BranchId = branch.Id, Sequence = current.Sequence + 2, Label = autonomous ? "Story turn" : run.Provider == "fixture" ? "Simulated turn" : "Narrated turn", StateJson = Json.Write(state), WorldJson = Json.Write(nextWorld) };
        var assistantMessage = new Message { BranchId = branch.Id, Sequence = current.Sequence + 2, Content = narrative, Provider = run.Provider, Model = run.Model };
        db.Messages.AddRange(
            new Message { BranchId = branch.Id, Sequence = current.Sequence + 1, Role = "user", Content = run.Action, Provider = run.Provider, Model = run.Model },
            assistantMessage);
        db.Checkpoints.Add(checkpoint);
        branch.HeadCheckpointId = checkpoint.Id;
        branch.Revision++;
        foreach (var fact in transition.Facts)
            db.Facts.Add(new Fact { BranchId = branch.Id, EffectiveSequence = checkpoint.Sequence, Text = fact.Text, Kind = fact.Kind, Visibility = fact.Visibility, KnownByJson = Json.Write(fact.KnownBy), Confidence = fact.Confidence, Provenance = $"generated:{run.Provider}", ReviewStatus = "accepted" });
        foreach (var update in transition.Threads)
        {
            var thread = await db.NarrativeThreads.FirstOrDefaultAsync(x => x.BranchId == branch.Id && x.Title == update.Title, ct);
            if (thread is null) db.NarrativeThreads.Add(new NarrativeThread { BranchId = branch.Id, Title = update.Title, Details = update.Details, Kind = update.Kind, Status = update.Status, Importance = update.Importance, EffectiveSequence = checkpoint.Sequence });
            else { thread.Details = update.Details; thread.Kind = update.Kind; thread.Status = update.Status; thread.Importance = update.Importance; thread.EffectiveSequence = checkpoint.Sequence; }
        }
        db.Events.Add(new StoryEvent { BranchId = branch.Id, Sequence = checkpoint.Sequence, Type = "turn", Summary = narrative.Length > 500 ? narrative[..500] : narrative, MessageId = assistantMessage.Id });
        var summary = await db.Summaries.SingleOrDefaultAsync(x => x.BranchId == branch.Id, ct);
        var summaryText = $"At sequence {checkpoint.Sequence}, the player acted: {run.Action}. The scene continued: {(narrative.Length > 700 ? narrative[..700] : narrative)}";
        if (summary is null) db.Summaries.Add(new CampaignSummary { BranchId = branch.Id, ThroughSequence = checkpoint.Sequence, Text = summaryText });
        else { summary.ThroughSequence = checkpoint.Sequence; summary.Text = summaryText; summary.UpdatedAt = DateTime.UtcNow; }
        run.StateProposalJson = Json.Write(transition);
        run.Status = "completed";
        await db.SaveChangesAsync(ct);
    }

    public static async Task CorrectFact(StoryDb db, Guid branchId, Guid factId, CorrectFactRequest request, CancellationToken ct)
    {
        RequireText(request.Text, 1000, "Corrected fact");
        RequireText(request.Reason, 1000, "Correction reason");
        var branch = await db.Branches.SingleOrDefaultAsync(x => x.Id == branchId, ct) ?? throw new KeyNotFoundException();
        if (branch.HeadCheckpointId != request.ExpectedCheckpointId) throw new DbUpdateConcurrencyException("Branch changed. Reload before correcting.");
        var fact = await db.Facts.SingleOrDefaultAsync(x => x.Id == factId && x.BranchId == branchId && x.ReviewStatus == "accepted", ct) ?? throw new KeyNotFoundException();
        if (request.Visibility is not ("public" or "narrator") || request.Kind is not ("fact" or "rumor" or "belief" or "secret" or "correction") || request.Confidence is < 0 or > 1)
            throw new InvalidOperationException("Correction visibility, kind, or confidence is invalid.");
        var current = await db.Checkpoints.SingleAsync(x => x.Id == branch.HeadCheckpointId, ct);
        db.FactCorrections.Add(new FactCorrection { BranchId = branchId, FactId = fact.Id, PreviousText = fact.Text, NewText = request.Text.Trim(), Reason = request.Reason.Trim() });
        fact.Text = request.Text.Trim(); fact.Kind = request.Kind; fact.Visibility = request.Visibility; fact.Confidence = request.Confidence; fact.KnownByJson = Json.Write(request.KnownBy ?? []); fact.Provenance = "user-confirmed"; fact.EffectiveSequence = current.Sequence + 1;
        var checkpoint = new Checkpoint { BranchId = branchId, Sequence = current.Sequence + 1, Label = "Canon correction", StateJson = current.StateJson, WorldJson = current.WorldJson };
        db.Checkpoints.Add(checkpoint);
        db.Messages.Add(new Message { BranchId = branchId, Sequence = checkpoint.Sequence, Role = "system", Content = $"Canon corrected: {request.Reason.Trim()}", Provider = "manual-review", Model = "deterministic-v1" });
        branch.HeadCheckpointId = checkpoint.Id; branch.Revision++;
        await db.SaveChangesAsync(ct);
    }

    public static async Task Approve(StoryDb db, Guid id, ApproveImport request, CancellationToken ct)
    {
        if (request.State is null) throw new InvalidOperationException("Provide a complete resume state.");
        request.State.Validate();
        var job = await db.ImportJobs.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException();
        if (job.Method == Reconstruction.Method) throw new InvalidOperationException("Use the reviewed draft approval endpoint for agent imports.");
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
            if (decision.Kind is not ("fact" or "rumor" or "belief" or "secret" or "correction") || decision.Confidence is < 0 or > 1 || (decision.KnownBy?.Length ?? 0) > 30 || (decision.KnownBy?.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 100) ?? false))
                throw new InvalidOperationException("Fact kind, confidence, or knowledge scope is invalid.");
            var fact = candidates.Single(x => x.Id == decision.FactId);
            fact.Text = decision.Text;
            fact.Visibility = decision.Visibility;
            fact.Kind = decision.Kind;
            fact.Confidence = decision.Confidence;
            fact.KnownByJson = Json.Write(decision.KnownBy ?? []);
            fact.ReviewStatus = decision.Accept ? "accepted" : "rejected";
            fact.Provenance = decision.Accept ? "user-confirmed" : "imported";
            fact.EffectiveSequence = current.Sequence + 1;
        }
        foreach (var decision in request.Threads ?? [])
        {
            if (!decision.Accept) continue;
            CampaignService.RequireText(decision.Title, 200, "Thread title");
            CampaignService.RequireText(decision.Details, 1000, "Thread details");
            if (decision.Kind is not ("goal" or "promise" or "deadline" or "mystery" or "conflict" or "thread") || decision.Status is not ("active" or "resolved") || decision.Importance is < 1 or > 5)
                throw new InvalidOperationException("A reviewed story thread is invalid.");
            db.NarrativeThreads.Add(new NarrativeThread { BranchId = branch.Id, Title = decision.Title, Details = decision.Details, Kind = decision.Kind, Status = decision.Status, Importance = decision.Importance, EffectiveSequence = current.Sequence + 1 });
        }
        // Imports establish a new scene rather than carrying unrelated synthetic secrets.
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
