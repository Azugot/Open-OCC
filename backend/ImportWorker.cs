using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;

namespace Story;

public record SourceEntry(string Speaker, string Text, string Section = "", string? Timestamp = null, bool Artifact = false);
public static class ImportParser
{
    public const int MaxBytes = 2 * 1024 * 1024;
    public static List<SourceEntry> Parse(string name, byte[] bytes)
    {
        if (bytes.Length is 0 or > MaxBytes) throw new InvalidOperationException("Import must be between 1 byte and 2 MiB.");
        var extension = Path.GetExtension(name).ToLowerInvariant();
        if (extension == ".docx") return Validate(ParseDocx(bytes));
        var text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
        List<SourceEntry> entries;
        if (extension == ".json")
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 });
            if (!doc.RootElement.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("JSON must have a messages array of { role, content } objects.");
            entries = messages.EnumerateArray().Select(m => new SourceEntry(
                m.GetProperty("role").GetString() ?? "unknown", m.GetProperty("content").GetString() ?? "", "message",
                m.TryGetProperty("timestamp", out var stamp) && stamp.ValueKind == JsonValueKind.String ? stamp.GetString() : null)).ToList();
        }
        else if (extension is ".txt" or ".md" or ".markdown") entries = SplitPlainText(text);
        else if (extension is ".html" or ".htm") entries = SplitPlainText(HtmlToText(text));
        else throw new InvalidOperationException("Supported uploads: UTF-8 .txt/.md, .json, .html, or .docx.");
        return Validate(entries);
    }

    private static List<SourceEntry> SplitPlainText(string text) => text.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
        .Select(t => new SourceEntry("unknown", t.Trim(), t.TrimStart().StartsWith('#') ? "heading" : "")).Where(x => x.Text.Length > 0).ToList();

    private static List<SourceEntry> ParseDocx(byte[] bytes)
    {
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var document = archive.GetEntry("word/document.xml") ?? throw new InvalidOperationException("DOCX does not contain word/document.xml.");
        if (document.Length > 16 * 1024 * 1024) throw new InvalidOperationException("DOCX document text exceeds 16 MiB.");
        using var stream = document.Open();
        var xml = XDocument.Load(stream);
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        return xml.Descendants(w + "p").Select(p => new SourceEntry("unknown",
            string.Concat(p.Descendants().Select(t => t.Name == w + "t" ? t.Value : t.Name == w + "tab" ? "\t" : t.Name == w + "br" ? "\n" : "")).Trim(),
            p.Descendants(w + "pStyle").Any(s => ((string?)s.Attribute(w + "val"))?.Contains("Heading", StringComparison.OrdinalIgnoreCase) == true) ? "heading" : p.Ancestors(w + "tbl").Any() ? "table" : ""))
            .Where(x => x.Text.Length > 0).ToList();
    }

    private static string HtmlToText(string html)
    {
        var withoutScripts = Regex.Replace(html, "<(script|style|noscript)\\b[^>]*>.*?</\\1>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        withoutScripts = Regex.Replace(withoutScripts, "<h[1-6]\\b[^>]*>(.*?)</h[1-6]>", "\n\n# $1\n\n", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var blocks = Regex.Replace(withoutScripts, "</(p|div|li|h[1-6]|tr|br)>|<br\\s*/?>", "\n\n", RegexOptions.IgnoreCase);
        var plain = Regex.Replace(blocks, "<[^>]+>", " ");
        return System.Net.WebUtility.HtmlDecode(plain).Replace("\r\n", "\n");
    }

    private static List<SourceEntry> Validate(List<SourceEntry> entries)
    {
        if (entries.Count is 0 or > 4000 || entries.Sum(x => (long)x.Text.Length) > MaxBytes || entries.Any(x => string.IsNullOrWhiteSpace(x.Text) || x.Speaker.Length > 100))
            throw new InvalidOperationException("Import requires 1–4000 nonempty passages, at most 2 MiB of extracted text and 100 characters per speaker. Split large transcripts into smaller files.");
        return entries.Select(x => {
            var label = Regex.Match(x.Text, @"^(user|assistant|narrator|player)\s*:", RegexOptions.IgnoreCase);
            return x with { Speaker = label.Success ? label.Groups[1].Value.ToLowerInvariant() : x.Speaker,
                Artifact = !x.Text.Any(char.IsLetterOrDigit), Timestamp = x.Timestamp ?? (Regex.Match(x.Text, @"\b\d{1,2}:\d{2}\b") is { Success: true } time ? time.Value : null) };
        }).ToList();
    }
}

// One application instance owns the worker. Every batch and its progress are committed together.
public sealed class ImportWorker(IServiceScopeFactory scopes, ILogger<ImportWorker> logger) : BackgroundService
{
    private static readonly string WorkerId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            Guid? activeJobId = null;
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<StoryDb>();
                var now = DateTime.UtcNow;
                var job = await db.ImportJobs.OrderBy(x => x.CreatedAt).FirstOrDefaultAsync(x => (x.Status == "queued" || x.Status == "processing") && (x.LeaseUntil == null || x.LeaseUntil < now), stoppingToken);
                if (job is not null)
                {
                    job.LeaseOwner = WorkerId;
                    job.LeaseUntil = now.AddMinutes(2);
                    job.AttemptCount++;
                    await db.SaveChangesAsync(stoppingToken);
                    activeJobId = job.Id;
                    var routing = scope.ServiceProvider.GetRequiredService<ProviderRouting>();
                    using var work = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    using var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    var heartbeat = RenewLease(scopes, job.Id, work, heartbeatStop.Token);
                    try
                    {
                        if (job.Method == Reconstruction.Method)
                        {
                            var routed = job.Stage == "normalize" || job.Provider == "fixture" ? null : await routing.ResolveCaptured(db, job.Provider, job.Model, work.Token);
                            await Reconstruction.Step(db, job, routed?.Provider, work.Token);
                        }
                        else await ProcessBatch(db, job, routing, work.Token);
                    }
                    finally { heartbeatStop.Cancel(); await heartbeat; }
                    job.LeaseOwner = null;
                    job.LeaseUntil = null;
                    await db.SaveChangesAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (OperationCanceledException) { /* Cancellation/lease loss prevents a stage commit. */ }
            catch (DbUpdateConcurrencyException) { /* Cancellation or another reviewed status won the race. */ }
            catch (Exception e)
            {
                logger.LogError(e, "Import worker failed ({Type}).", e.GetType().Name);
                if (activeJobId is { } id)
                {
                    using var failureScope = scopes.CreateScope();
                    var failureDb = failureScope.ServiceProvider.GetRequiredService<StoryDb>();
                    var failed = await failureDb.ImportJobs.SingleOrDefaultAsync(x => x.Id == id, CancellationToken.None);
                    if (failed is not null && failed.LeaseOwner == WorkerId && failed.Status is "queued" or "processing")
                    {
                        failed.Status = failed.Method == Reconstruction.Method ? "paused" : "failed";
                        failed.LeaseOwner = null;
                        failed.LeaseUntil = null;
                        failed.Error = e is InvalidOperationException ? e.Message.StartsWith("Provider returned ") ? "The provider rejected this request. Check the model and connection, then retry the saved stage." : e.Message : "Reconstruction stopped before the stage committed. Check the source and provider, then retry the saved stage.";
                        await failureDb.SaveChangesAsync(CancellationToken.None);
                    }
                }
            }
            try { await Task.Delay(500, stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }
    private static async Task RenewLease(IServiceScopeFactory scopes, Guid jobId, CancellationTokenSource work, CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(20), stop);
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<StoryDb>();
                var changed = await db.ImportJobs.Where(x => x.Id == jobId && x.LeaseOwner == WorkerId && (x.Status == "queued" || x.Status == "processing"))
                    .ExecuteUpdateAsync(set => set.SetProperty(x => x.LeaseUntil, DateTime.UtcNow.AddMinutes(2)), stop);
                if (changed == 0) { work.Cancel(); return; }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch { work.Cancel(); } // A worker unable to renew must not commit a late response.
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

    public static async Task ProcessBatch(StoryDb db, ImportJob job, ProviderRouting routing, CancellationToken ct)
    {
        if (job.Provider == "fixture") { await ProcessBatch(db, job, ct); return; }
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
        var routed = await routing.ResolveCaptured(db, job.Provider, job.Model, ct);
        var start = job.Processed;
        var batchRows = new List<SourceEntry>();
        var batchChars = 0;
        foreach (var entry in entries.Skip(start).Take(20))
        {
            if (batchRows.Count > 0 && batchChars + entry.Text.Length > 60_000) break;
            batchRows.Add(entry); batchChars += entry.Text.Length;
        }
        var batch = batchRows.ToArray();
        var ledger = await db.Facts.Where(x => x.JobId == job.Id).OrderBy(x => x.Text).Select(x => new { x.Text, x.Kind, x.Visibility, x.Confidence }).Take(100).ToListAsync(ct);
        var prompt = new ProviderPrompt(
            "You are performing evidence-grounded story reconstruction. Return JSON only. Extract durable facts, uncertain beliefs or rumors, secrets and who actually knows them, unresolved promises/deadlines/goals, and the best complete resume state at the end of this batch. Every fact needs one or more exact global evidence ordinals from the supplied passages. Do not treat narrative speculation as truth. Keep prior ledger facts unless contradicted; emit a correction when the source resolves a contradiction.",
            "PRIOR SUMMARY:\n" + (job.Summary ?? "None") + "\n\nPRIOR LEDGER:\n" + Json.Write(ledger) + "\n\nPASSAGES:\n" + string.Join("\n\n", batch.Select((e, i) => $"[{start + i}] {e.Speaker}: {e.Text}")), 3000);
        ReconstructionResult result;
        ProviderCompletion completion;
        try
        {
            (result, completion) = await ContinuityService.CompleteValidatedWithUsage<ReconstructionResult>(routed.Provider, prompt, ContinuityService.ReconstructionSchema,
                value => ValidateBatch(ContinuityService.ValidateReconstruction(value, entries.Count), start, start + batch.Length), ct);
        }
        catch (Exception e) when (e is JsonException || (e is InvalidOperationException && !e.Message.StartsWith("Provider returned ", StringComparison.OrdinalIgnoreCase)))
        {
            // Keep the import resumable when a small local model cannot satisfy the JSON contract.
            // These are deliberately low-confidence, evidence-linked review candidates—not canon.
            result = new(batch.Select((entry, i) => new ReconstructionFact(entry.Text, "fact", "narrator", [], .25, [start + i])).ToArray(), null, [], "Automatic extraction was unavailable; review the verbatim passages below.");
            completion = new("");
        }
        var segmentByOrdinal = new Dictionary<int, ImportSegment>();
        foreach (var pair in batch.Select((entry, i) => (entry, ordinal: start + i)))
        {
            var segment = new ImportSegment { JobId = job.Id, Ordinal = pair.ordinal, Speaker = pair.entry.Speaker, Text = pair.entry.Text };
            segmentByOrdinal[pair.ordinal] = segment;
            db.Segments.Add(segment);
        }
        foreach (var candidate in result.Facts)
        {
            var fact = new Fact { BranchId = job.BranchId, JobId = job.Id, Text = candidate.Text, Kind = candidate.Kind, Visibility = candidate.Visibility, KnownByJson = Json.Write(candidate.KnownBy), Confidence = candidate.Confidence };
            db.Facts.Add(fact);
            foreach (var ordinal in candidate.EvidenceOrdinals.Distinct()) db.Evidence.Add(new FactEvidence { FactId = fact.Id, SegmentId = segmentByOrdinal[ordinal].Id });
        }
        var oldThreads = Json.Read<TransitionThread[]>(job.ProposedThreadsJson);
        job.ProposedThreadsJson = Json.Write(oldThreads.Concat(result.Threads).GroupBy(x => x.Title, StringComparer.OrdinalIgnoreCase).Select(x => x.Last()).Take(50).ToArray());
        if (result.ResumeState is not null) job.ProposedStateJson = Json.Write(result.ResumeState);
        job.Summary = result.Summary;
        job.InputTokens = (job.InputTokens ?? 0) + (completion.InputTokens ?? 0);
        job.OutputTokens = (job.OutputTokens ?? 0) + (completion.OutputTokens ?? 0);
        job.Processed += batch.Length;
        if (job.Processed == job.Total) job.Status = "review";
        await db.SaveChangesAsync(ct);
    }

    private static ReconstructionResult ValidateBatch(ReconstructionResult result, int start, int end)
    {
        if (result.Facts.Any(x => x.EvidenceOrdinals.Any(i => i < start || i >= end)))
            throw new InvalidOperationException("Reconstruction cited a passage outside the supplied batch.");
        return result;
    }
}
