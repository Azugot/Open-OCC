using Microsoft.EntityFrameworkCore;

namespace Story;

public record SettingsUpdate(Guid ExpectedCheckpointId, EngineSettings Settings);
public record MemoryUpdate(Guid ExpectedCheckpointId, string Id, string Text, bool Pinned, bool Remove);

public static class EngineApi
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/branches/{id:guid}/engine", async (Guid id, Guid? checkpointId, StoryDb db, CancellationToken ct) =>
        {
            var branch = await db.Branches.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException();
            var checkpoint = await db.Checkpoints.SingleOrDefaultAsync(x => x.Id == (checkpointId ?? branch.HeadCheckpointId) && x.BranchId == id, ct) ?? throw new KeyNotFoundException();
            var runs = checkpoint.Id == branch.HeadCheckpointId
                ? await db.GenerationRuns.Where(x => x.BranchId == id && x.ExpectedCheckpointId == checkpoint.Id).OrderByDescending(x => x.CreatedAt).Take(10).ToListAsync(ct)
                : [];
            return Results.Ok(new { checkpointId = checkpoint.Id, world = StoryWorld.From(checkpoint), runs = runs.Select(x => new
                { x.Id, x.Action, x.Status, x.Error, draft = x.DraftJson == "{}" ? null : Json.Read<TurnDraft>(x.DraftJson) }) });
        });
        app.MapPost("/api/branches/{id:guid}/engine/settings", async (Guid id, SettingsUpdate request, StoryDb db, CancellationToken ct) =>
        {
            if (request.Settings is null) throw new InvalidOperationException("Settings are required.");
            request.Settings.Validate();
            foreach (var profileId in new[] { request.Settings.CharacterProfile, request.Settings.DirectorProfile })
                if (profileId != "fixture" && !await db.Providers.AnyAsync(x => x.Id == profileId && x.Enabled, ct)) throw new InvalidOperationException("Select an enabled provider profile.");
            await Edit(db, id, request.ExpectedCheckpointId, "Engine settings updated", w => w.Settings = request.Settings, ct);
            return Results.NoContent();
        });
        app.MapPost("/api/branches/{id:guid}/memories", async (Guid id, MemoryUpdate request, StoryDb db, CancellationToken ct) =>
        {
            await Edit(db, id, request.ExpectedCheckpointId, "Author memory correction", w =>
            {
                var index = w.Memories.FindIndex(x => x.Id == request.Id);
                if (index < 0) throw new KeyNotFoundException();
                var memory = w.Memories[index];
                if (request.Remove) w.Memories.RemoveAt(index);
                else
                {
                    CampaignService.RequireText(request.Text, 1000, "Memory text");
                    if (request.Pinned && !memory.Pinned && w.Memories.Count(x => x.OwnerId == memory.OwnerId && x.Pinned) >= 20)
                        throw new InvalidOperationException("Each owner can pin up to 20 memories.");
                    w.Memories[index] = memory with { Text = request.Text.Trim(), Pinned = request.Pinned };
                }
                // Keep the concise belief summary consistent with author corrections/removal.
                var characterIndex = w.Characters.FindIndex(x => x.EntityId == memory.OwnerId);
                if (characterIndex >= 0 && memory.Kind == "belief")
                {
                    var character = w.Characters[characterIndex];
                    w.Characters[characterIndex] = character with { Belief = request.Remove
                        ? (w.Memories.LastOrDefault(x => x.OwnerId == memory.OwnerId && x.Kind == "belief")?.Text ?? "No current belief established")
                        : request.Text.Trim() };
                }
            }, ct);
            return Results.NoContent();
        });
        app.MapPost("/api/runs/{id:guid}/retry", async (Guid id, StoryDb db, IConfiguration config, CancellationToken ct) =>
        {
            var run = await db.GenerationRuns.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException();
            if (run.Status != "paused") throw new InvalidOperationException("Only paused drafts can resume.");
            await StoryEngine.Execute(db, run, config, ct); return Results.Ok(new { run.Id, run.Status });
        });
        app.MapPost("/api/runs/{id:guid}/cancel", async (Guid id, StoryDb db, CancellationToken ct) =>
        {
            var run = await db.GenerationRuns.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException();
            if (run.Status is not ("running" or "paused")) throw new InvalidOperationException("This draft cannot be cancelled.");
            run.Status = "cancelled"; run.Error = "Cancelled. No partial turn was committed.";
            await db.SaveChangesAsync(ct); StoryEngine.Cancel(run.Id); return Results.NoContent();
        });
    }

    public static async Task Edit(StoryDb db, Guid branchId, Guid expected, string label, Action<StoryWorld> change, CancellationToken ct)
    {
        var branch = await db.Branches.SingleOrDefaultAsync(x => x.Id == branchId, ct) ?? throw new KeyNotFoundException();
        if (branch.HeadCheckpointId != expected) throw new DbUpdateConcurrencyException();
        if (await db.GenerationRuns.AnyAsync(x => x.BranchId == branchId && (x.Status == "running" || x.Status == "paused"), ct))
            throw new InvalidOperationException("Cancel or finish the active draft before editing this branch.");
        var head = await db.Checkpoints.SingleAsync(x => x.Id == expected, ct);
        var world = StoryWorld.From(head); change(world);
        var checkpoint = new Checkpoint { BranchId = branchId, Sequence = head.Sequence + 1, Label = label, StateJson = head.StateJson, WorldJson = Json.Write(world) };
        db.Checkpoints.Add(checkpoint);
        // No private memory contents are written into the public message history.
        db.Messages.Add(new Message { BranchId = branchId, Sequence = checkpoint.Sequence, Role = "system", Content = label, Provider = "author", Model = "manual" });
        branch.HeadCheckpointId = checkpoint.Id; branch.Revision++;
        await db.SaveChangesAsync(ct);
    }
}
