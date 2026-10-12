using Microsoft.EntityFrameworkCore;

namespace Story;

// Timing is operational metadata, stored alongside stage results so it survives reloads
// without changing campaign state or the portable format.
public record ImportRequestTiming(string Stage, bool Repairing, DateTime StartedAt, DateTime? CompletedAt = null, double? Seconds = null);
public static class ImportProgress
{
    public const string TimingStage = "request-timing";
    public static async Task<object> Read(StoryDb db, ImportJob job, CancellationToken ct)
    {
        var results = await db.ImportResults.AsNoTracking().Where(x => x.JobId == job.Id &&
            (x.Stage == TimingStage || x.Stage == "reconcile-windows" || x.Stage == "audit-windows"))
            .Select(x => new { x.Stage, x.Ordinal, x.ResultJson, x.Provider, x.Model }).ToListAsync(ct);
        int? Windows(string stage) => results.FirstOrDefault(x => x.Stage == stage + "-windows") is { } plan
            ? Json.Read<ReviewWindow[]>(plan.ResultJson).Length : null;
        var timing = results.Where(x => x.Stage == TimingStage && x.Provider == job.Provider && x.Model == job.Model)
            .OrderByDescending(x => x.Ordinal).Select(x => Json.Read<ImportRequestTiming>(x.ResultJson)).ToArray();
        var active = job.Status == "processing" && timing.FirstOrDefault() is { CompletedAt: null } latest ? latest : null;
        // An interrupted request from an earlier attempt is never described as active.
        if (active is not null && results.Where(x => x.Stage == TimingStage).Max(x => x.Ordinal) != job.Calls) active = null;
        var stage = job.Stage;
        var done = job.Status is "review" or "approved" || stage == "complete";
        var total = stage == "extract" ? job.Total : stage is "reconcile" or "audit" ? Windows(stage) : stage is "resume" or "repair" ? 1 : (int?)null;
        var completed = stage == "extract" ? job.Processed : stage is "reconcile" or "audit" ? job.StageCursor : 0;
        var fraction = total is > 0 ? Math.Clamp((double)completed / total.Value, 0, 1) : 0;
        var (start, span) = stage switch { "extract" => (0, 45), "reconcile" => (45, 20), "resume" => (65, 10), "audit" => (75, 20), "repair" => (95, 4), "complete" => (100, 0), _ => (0, 0) };
        var percent = done ? 100 : Math.Min(99, (int)(start + span * fraction));
        var samples = timing.Where(x => x.CompletedAt is not null && x.Seconds is > 0).Take(10).Select(x => x.Seconds!.Value).ToArray();
        var now = DateTime.UtcNow;
        var elapsed = active is null ? (double?)null : Math.Max(0, (now - active.StartedAt).TotalSeconds);
        var takingLonger = samples.Length >= 2 && elapsed > samples.Average() * 3;
        double? low = null, high = null;
        var currentKnown = total is not null && (stage != "extract" || job.Processed > 0);
        if (job.Status == "processing" && samples.Length >= 2 && currentKnown)
        {
            var average = samples.Average();
            var proposalCount = await db.ImportProposals.CountAsync(x => x.JobId == job.Id && x.Current, ct);
            var reviewEstimate = Windows("audit") ?? (Windows("reconcile") is { } reconciled ? reconciled + 1 : Math.Max(1, proposalCount));
            var extractCalls = timing.Count(x => x.Stage == "extract" && !x.Repairing && x.CompletedAt is not null);
            var remainingExtract = Math.Ceiling((job.Total - job.Processed) / Math.Max(1d, (double)job.Processed / Math.Max(1, extractCalls)));
            var remaining = stage switch {
                "extract" => remainingExtract + 2 * reviewEstimate + 2,
                "reconcile" => Math.Max(0, total!.Value - completed) + reviewEstimate + 2,
                "resume" => reviewEstimate + 2,
                "audit" => Math.Max(0, total!.Value - completed) + 1,
                "repair" => 1,
                _ => 0d };
            // Long or repaired responses can exceed the average. Stop showing a stale
            // completion promise, while continuing to display the elapsed request time.
            if (!takingLonger)
            {
                low = Math.Max(10, average * remaining * .65 - (elapsed ?? 0));
                high = Math.Max(low.Value + 20, average * remaining * 1.6 - (elapsed ?? 0));
            }
        }
        return new { percent, stage, completed, total, requestStartedAt = active?.StartedAt,
            activity = job.Status == "queued" ? "queued" : active is not null ? active.Repairing ? "repairing-response" : "generating" : "organizing",
            estimatedRemainingMinSeconds = low, estimatedRemainingMaxSeconds = high, takingLonger, asOf = now };
    }
}
