using Microsoft.EntityFrameworkCore;

namespace Story;

public record GenerationActivity(string Mode, string Phase, DateTime StartedAt, DateTime PhaseStartedAt, DateTime UpdatedAt);
public record GenerationProgressView(Guid RunId, string Status, string Mode, string Phase,
    DateTime StartedAt, DateTime PhaseStartedAt, DateTime UpdatedAt, DateTime AsOf);

public static class GenerationProgress
{
    private static readonly HashSet<string> Phases = ["processing", "preparing", "resuming", "director-planning", "cast-deciding", "director-assessing", "director-resolving", "narrating", "checking-continuity", "saving"];
    public static GenerationActivity Activity(GenerationRun run)
    {
        try
        {
            if (run.ProgressJson != "{}" && Json.Read<GenerationActivity>(run.ProgressJson) is { } saved &&
                saved.Mode is "autonomous" or "narrator" && Phases.Contains(saved.Phase) &&
                saved.StartedAt > DateTime.UnixEpoch && saved.PhaseStartedAt >= saved.StartedAt && saved.UpdatedAt >= saved.PhaseStartedAt) return saved;
        }
        catch (System.Text.Json.JsonException) { /* Older runs have no operational progress. */ }
        return new(run.ContextJson == "{}" ? "autonomous" : "narrator", "processing", run.CreatedAt, run.CreatedAt, run.CreatedAt);
    }
    public static void Set(GenerationRun run, string mode, string phase, bool restart = false)
    {
        if (mode is not ("autonomous" or "narrator") || !Phases.Contains(phase)) throw new InvalidOperationException("Invalid generation activity.");
        var now = DateTime.UtcNow; var prior = Activity(run);
        run.ProgressJson = Json.Write(new GenerationActivity(mode, phase, restart ? now : prior.StartedAt,
            restart || prior.Phase != phase ? now : prior.PhaseStartedAt, now));
    }
    public static void Stop(GenerationRun run)
    {
        var activity = Activity(run);
        run.ProgressJson = Json.Write(activity with { UpdatedAt = DateTime.UtcNow });
    }
    public static GenerationProgressView View(GenerationRun run)
    {
        var activity = Activity(run);
        // No draft keys, actor identities, private thoughts, or off-screen counters leave this endpoint.
        return new(run.Id, run.Status, activity.Mode, activity.Phase, activity.StartedAt, activity.PhaseStartedAt, activity.UpdatedAt, DateTime.UtcNow);
    }
    public static async Task<GenerationProgressView?> Read(StoryDb db, Guid branchId, Guid? runId, Guid? expectedCheckpointId, CancellationToken ct)
    {
        var branch = await db.Branches.AsNoTracking().SingleOrDefaultAsync(b => b.Id == branchId, ct) ?? throw new KeyNotFoundException();
        if (expectedCheckpointId is { } expected && !await db.Checkpoints.AnyAsync(c => c.Id == expected && c.BranchId == branchId, ct)) throw new KeyNotFoundException();
        var query = db.GenerationRuns.AsNoTracking().Where(r => r.BranchId == branchId);
        if (runId is { } id) query = query.Where(r => r.Id == id);
        if (expectedCheckpointId is { } cp) query = query.Where(r => r.ExpectedCheckpointId == cp);
        else if (runId is null) query = query.Where(r => r.ExpectedCheckpointId == branch.HeadCheckpointId && (r.Status == "running" || r.Status == "paused"));
        var run = await query.OrderByDescending(r => r.CreatedAt).FirstOrDefaultAsync(ct);
        if (run is null && runId is not null) throw new KeyNotFoundException();
        return run is null ? null : View(run);
    }
    public static void Map(WebApplication app) => app.MapGet("/api/branches/{id:guid}/generation-progress",
        async (Guid id, Guid? runId, Guid? expectedCheckpointId, StoryDb db, CancellationToken ct) => Results.Ok(await Read(db, id, runId, expectedCheckpointId, ct)));
}
