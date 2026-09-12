using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Story;

public record SourceEntry(string Speaker, string Text);
public static class ImportParser
{
    public const int MaxBytes = 2 * 1024 * 1024;
    public static List<SourceEntry> Parse(string name, byte[] bytes)
    {
        var text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
        List<SourceEntry> entries;
        if (Path.GetExtension(name).Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 });
            if (!doc.RootElement.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("JSON must have a messages array of { role, content } objects.");
            entries = messages.EnumerateArray().Select(m => new SourceEntry(
                m.GetProperty("role").GetString() ?? "unknown", m.GetProperty("content").GetString() ?? "")).ToList();
        }
        else entries = text.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(t => new SourceEntry("unknown", t.Trim())).ToList();
        if (entries.Count is 0 or > 2000 || entries.Any(x => string.IsNullOrWhiteSpace(x.Text) || x.Text.Length > 16000 || x.Speaker.Length > 100))
            throw new InvalidOperationException("Import requires 1–2000 nonempty segments, at most 16000 characters each and 100 characters per speaker. Split large transcripts into smaller files.");
        return entries;
    }
}

// One application instance owns the worker. Every batch and its progress are committed together.
public sealed class ImportWorker(IServiceScopeFactory scopes, ILogger<ImportWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<StoryDb>();
                var job = await db.ImportJobs.OrderBy(x => x.CreatedAt).FirstOrDefaultAsync(x => x.Status == "queued" || x.Status == "processing", stoppingToken);
                if (job is not null) await ProcessBatch(db, job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (DbUpdateConcurrencyException) { /* Cancellation or another reviewed status won the race. */ }
            catch (Exception e) { logger.LogError("Import worker failed ({Type}); retrying on next tick.", e.GetType().Name); }
            try { await Task.Delay(500, stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }
    public static async Task ProcessBatch(StoryDb db, ImportJob job, CancellationToken ct)
    {
        var source = await db.Sources.SingleAsync(x => x.Id == job.SourceId, ct);
        List<SourceEntry> entries;
        try { entries = ImportParser.Parse(source.FileName, source.Bytes); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or DecoderFallbackException or KeyNotFoundException)
        {
            job.Status = "failed";
            job.Error = "Cannot parse this source as the documented UTF-8 text/JSON format. Check segment limits and JSON fields, then upload a corrected copy.";
            await db.SaveChangesAsync(ct);
            return;
        }
        job.Total = entries.Count;
        job.Status = "processing";
        foreach (var entry in entries.Skip(job.Processed).Take(25))
        {
            var segment = new ImportSegment { JobId = job.Id, Ordinal = job.Processed++, Speaker = entry.Speaker, Text = entry.Text };
            // These are verbatim review candidates, not claims of semantic reconstruction.
            var fact = new Fact { BranchId = job.BranchId, JobId = job.Id, Text = entry.Text, Visibility = "narrator" };
            db.Segments.Add(segment);
            db.Facts.Add(fact);
            db.Evidence.Add(new FactEvidence { FactId = fact.Id, SegmentId = segment.Id });
        }
        if (job.Processed == job.Total) job.Status = "review";
        await db.SaveChangesAsync(ct);
    }
}
