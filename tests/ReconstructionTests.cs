using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Story;
using Xunit;

namespace StoryTests;

public class ReconstructionTests : IAsyncLifetime
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private StoryDb Db() => new(new DbContextOptionsBuilder<StoryDb>().UseSqlite(connection).Options);
    public async Task InitializeAsync() { await connection.OpenAsync(); await using var db = Db(); await db.Database.EnsureCreatedAsync(); }
    public Task DisposeAsync() => connection.DisposeAsync().AsTask();
    private async Task<ImportJob> Create(StoryDb db, byte[]? bytes = null, string fileName = "story.txt")
    {
        var branch = await CampaignService.Create(db, new("Imported test", false), default);
        var source = new ImportedSource { CampaignId = branch.CampaignId, FileName = fileName, Bytes = bytes ?? Encoding.UTF8.GetBytes("Mara waits at the Harbor. Player: Pack two rations. The crossing begins at noon.") };
        var job = new ImportJob { SourceId = source.Id, BranchId = branch.Id, ExpectedCheckpointId = branch.HeadCheckpointId, Method = Reconstruction.Method, Provider = "test", Model = "scripted" };
        db.AddRange(source, job); await db.SaveChangesAsync(); return job;
    }
    private sealed class Scripted(Func<ProviderPrompt, AgentResult> respond) : IStoryProvider
    {
        public ProviderCapabilities Capabilities => new(true, false, true, false, false, "Scripted test model");
        public async IAsyncEnumerable<ProviderEvent> Stream(ProviderPrompt request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellation) { await Task.CompletedTask; yield break; }
        public Task<ProviderCompletion> Complete(ProviderPrompt request, JsonElement? schema, CancellationToken cancellation) => Task.FromResult(new ProviderCompletion(Json.Write(respond(request)), 10, 20));
    }
    private sealed class Invalid : IStoryProvider
    {
        public int Calls;
        public ProviderCapabilities Capabilities => new(true, false, false, false, false, "Invalid local model");
        public async IAsyncEnumerable<ProviderEvent> Stream(ProviderPrompt request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellation) { await Task.CompletedTask; yield break; }
        public Task<ProviderCompletion> Complete(ProviderPrompt request, JsonElement? schema, CancellationToken cancellation) { Calls++; return Task.FromResult(new ProviderCompletion("```json\n{incomplete", 3, 5)); }
    }
    private sealed class TimedOut : IStoryProvider
    {
        public int Calls;
        public ProviderCapabilities Capabilities => new(true, false, false, false, false, "Timed out test model");
        public async IAsyncEnumerable<ProviderEvent> Stream(ProviderPrompt request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellation) { await Task.CompletedTask; yield break; }
        public Task<ProviderCompletion> Complete(ProviderPrompt request, JsonElement? schema, CancellationToken cancellation)
        { Calls++; return Task.FromException<ProviderCompletion>(new OperationCanceledException("HTTP timeout")); }
    }
    [Fact]
    public async Task ProviderTimeoutStopsBeforeRepairInsteadOfSilentlyRepeatingCalls()
    {
        await using var db = Db(); var job = await Create(db); await Reconstruction.Step(db, job, null, default);
        var model = new TimedOut();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Reconstruction.Step(db, job, model, default));
        Assert.Contains("timed out", error.Message); Assert.Equal(1, model.Calls); Assert.Equal(1, job.Calls);
        Assert.Equal(0, job.Processed); Assert.Empty(await db.ImportResults.Where(x => x.JobId == job.Id).ToListAsync());
    }
    private static int[] Evidence(ProviderPrompt prompt)
    {
        var text = prompt.Input.Split("EVIDENCE:\n")[1].Split("\nRELATED LEDGER:")[0];
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.EnumerateArray().Select(x => x.GetProperty("ordinal").GetInt32()).ToArray();
    }
    private static ImportClaim Claim(string key, string category, string text, int[] evidence, string subject = "", string target = "", string value = "", int amount = 0, string kind = "fact", string visibility = "public", string[]? knownBy = null)
        => new(key, category, text, subject, target, value, amount, kind, visibility, knownBy ?? [], .9, evidence, "current", []);
    private static AgentResult Respond(ProviderPrompt prompt)
    {
        var evidence = Evidence(prompt); var cite = evidence.Take(1).ToArray();
        var state = new StoryState("Harbor", "Day 7 · noon", "Cross the bay", "Rested", new() { ["Navigation"] = "F · 2/5" }, new() { ["Rations"] = 2 }, ["Mara", "Lyra"]);
        var scene = new ResumeProposal(state, cite, "Pack two rations", true, []);
        if (prompt.Input.StartsWith("STAGE: audit")) return new([], [], null, "Evidence and ending verified.", []);
        if (prompt.Input.StartsWith("STAGE: reconcile")) return new([], [], null, "Ledger reconciled.", []);
        if (prompt.Input.StartsWith("STAGE: resume")) return new([], [], scene, "Mara and Lyra are ready to cross the bay at noon.", []);
        var claims = new[] {
            Claim("character:mara", "character", "Mara is a navigator.", cite, "Mara"),
            Claim("character:lyra", "character", "Lyra is Mara's companion.", cite, "Lyra"),
            Claim("inventory:rations", "inventory", "The current supplies include two rations.", cite, "Rations", amount: 2),
            Claim("skill:navigation", "skill", "Navigation has two pips.", cite, "Navigation", value: "F · 2/5"),
            Claim("relationship:mara-lyra", "relationship", "Mara trusts Lyra.", cite, "Mara", "Lyra", "friend", 20),
            Claim("fact:key", "fact", "The silver key opens the archive.", cite, kind: "secret", visibility: "narrator", knownBy: ["Mara"]),
            Claim("knowledge:mara-key", "knowledge", "Mara knows what the silver key opens.", cite, "Mara", "fact:key", kind: "secret", visibility: "narrator", knownBy: ["Mara"]),
            Claim("world:tides", "world", "The bay can be crossed at low tide.", cite, "rule", value: "Tides"),
            Claim("thread:crossing", "thread", "Cross the bay before dusk.", cite, "Cross the bay", "active", "deadline", 5),
            Claim("event:preparation", "event", "Mara packed supplies for the crossing.", cite) };
        return new(claims, [], scene, "Mara prepares for the crossing; the ledger retains her companions, abilities and secrets.", []);
    }
    private async Task Complete(StoryDb db, ImportJob job, IStoryProvider? model = null)
    {
        var count = 0;
        while (job.Status != "review")
        {
            Assert.True(count++ < 100, $"Stage did not finish: {job.Stage} {job.StageCursor}");
            await Reconstruction.Step(db, job, model ?? new Scripted(Respond), default);
        }
    }
    [Fact]
    public async Task GrowingSceneAndDetailedClaimsAdaptWindowsToTheCompleteInputBudget()
    {
        await using var db = Db();
        var job = await Create(db, Encoding.UTF8.GetBytes(string.Join("\n\n", Enumerable.Range(0, 160).Select(i => $"Day {i}: " + new string('x', 150)))));
        var prompts = new List<ProviderPrompt>();
        var model = new Scripted(p => {
            prompts.Add(p);
            Assert.True(p.Input.Length + p.Instructions.Length + Reconstruction.Schema.GetRawText().Length <= job.InputCharacterLimit);
            if (p.Input.StartsWith("STAGE: reconcile") || p.Input.StartsWith("STAGE: audit")) return new([], [], null, "", []);
            var scene = new ResumeProposal(StoryState.Synthetic, Evidence(p).TakeLast(1).ToArray(), new string('a', 4000), false, []);
            var claims = p.Input.StartsWith("STAGE: resume") ? [] : Enumerable.Range(0, 4).Select(i =>
                Claim($"fact:detail-{i}", "fact", new string('t', 900), Evidence(p).TakeLast(1).ToArray(), value: new string('v', 900))).ToArray();
            return new(claims, [], scene, new string('s', 4000), []);
        });
        await Complete(db, job, model);
        Assert.Equal(job.Total, job.Processed); Assert.Equal("review", job.Status);
        Assert.True(prompts.Count(p => p.Input.StartsWith("STAGE: extract")) > 2);
    }
    [Fact]
    public void ReviewPagesCoverEveryCitationAcrossLargeClaimsWithinBudget()
    {
        var rows = Enumerable.Range(0, 25).Select(i => new ImportSegment { Ordinal = i, Text = new string('x', 900) }).ToArray();
        var proposal = new ImportProposal { ContentJson = Json.Write(Claim("fact:history", "fact", "A recurring fact.", rows.Select(x => x.Ordinal).ToArray())) };
        var scene = new ResumeProposal(StoryState.Synthetic, [0, 24], "Wait", false, []);
        var pages = Reconstruction.ReviewWindows([proposal], rows, scene, 3500);
        Assert.True(pages.Length > 2);
        Assert.Equal(rows.Select(x => x.Ordinal), pages.Where(x => !x.ReviewScene).SelectMany(x => x.EvidenceOrdinals).Distinct().Order());
        Assert.Equal(new[] { 0, 24 }, pages.Where(x => x.ReviewScene).SelectMany(x => x.EvidenceOrdinals).Order());
        Assert.All(pages, p => Assert.True(ImportContext.Evidence(rows.Where(x => p.EvidenceOrdinals.Contains(x.Ordinal))).Length <= 3500));
    }
    [Fact]
    public async Task QuoteHeavyLongTurnsFitEveryStoredCitationAndInputWindow()
    {
        await using var db = Db(); var text = string.Concat(Enumerable.Repeat("\"quote\" \\ path\t\n", 1400));
        var job = await Create(db, Encoding.UTF8.GetBytes(Json.Write(new { messages = new[] { new { role = "assistant", content = text } } })), "quoted.json");
        await Reconstruction.Step(db, job, null, default);
        var passages = await db.Segments.OrderBy(x => x.Ordinal).ToArrayAsync();
        Assert.Equal(text, string.Concat(passages.Select(x => x.Text)));
        var model = new Scripted(p => {
            Assert.True(p.Instructions.Length + p.Input.Length + Reconstruction.Schema.GetRawText().Length <= job.InputCharacterLimit);
            return new([], [], null, "A transcript turn.", []);
        });
        while (job.Stage == "extract") await Reconstruction.Step(db, job, model, default);
        Assert.Equal(job.Total, job.Processed);
    }
    [Fact]
    public async Task ReadingWindowsUseLargerConnectedSectionsAndOverlapWithoutLosingSource()
    {
        var paragraphs = Enumerable.Range(0, 120).Select(i => $"Passage {i}: Mara answers Lyra in the harbor. " + new string('x', 115)).ToArray();
        await using var db = Db(); var job = await Create(db, Encoding.UTF8.GetBytes(string.Join("\n\n", paragraphs)));
        await Reconstruction.Step(db, job, null, default);
        var sections = await db.ImportSections.Where(x => x.JobId == job.Id).OrderBy(x => x.Ordinal).ToArrayAsync();
        var passages = await db.Segments.Where(x => x.JobId == job.Id).OrderBy(x => x.Ordinal).ToArrayAsync();
        Assert.Equal(paragraphs, passages.Select(x => x.Text));
        Assert.True(passages.Where(x => x.Ordinal < sections[0].End).Sum(x => x.Text.Length) > 6000);
        var prompts = new List<ProviderPrompt>();
        var model = new Scripted(p => { prompts.Add(p); return new([], [], null, "Mara speaks with Lyra.", []); });
        await Reconstruction.Step(db, job, model, default);
        var covered = job.Processed;
        await Reconstruction.Step(db, job, model, default);
        Assert.Contains(Evidence(prompts[1]), x => x < covered);
        Assert.Contains(Evidence(prompts[1]), x => x >= covered);
        Assert.Contains("PRIOR SUMMARY: Mara speaks with Lyra.", prompts[1].Input);
        Assert.All(prompts, p => Assert.True(p.Input.Length + p.Instructions.Length + Reconstruction.Schema.GetRawText().Length <= job.InputCharacterLimit));
    }
    [Fact]
    public void LongTurnsSplitAtNaturalBoundariesAndKeepEveryCharacter()
    {
        var original = string.Join("\n\n", Enumerable.Range(0, 1000).Select(i => $"Paragraph {i}: Mara says hello to Lyra. The reply follows. 😀"));
        Assert.Single(ImportParser.Parse("long.json", Encoding.UTF8.GetBytes(Json.Write(new { messages = new[] { new { role = "assistant", content = original } } }))));
        var parts = ImportContext.Passages([new("assistant", original)], 8000).ToArray();
        Assert.True(parts.Length > 1); Assert.Equal(original, string.Concat(parts.Select(x => x.Text)));
        Assert.All(parts.SkipLast(1), p => Assert.EndsWith("\n\n", p.Text));
        Assert.All(parts, p => Assert.Equal("assistant", p.Speaker));
    }
    [Fact]
    public async Task RepairReceivesSpecificValidatorErrorAndDoesNotCommitInvalidClaims()
    {
        await using var db = Db(); var job = await Create(db); await Reconstruction.Step(db, job, null, default);
        var prompts = new List<ProviderPrompt>();
        var model = new Scripted(p => {
            prompts.Add(p);
            return prompts.Count == 1 ? new([Claim("place:harbor", "place", "Harbor", Evidence(p))], [], null, "", []) : Respond(p);
        });
        await Reconstruction.Step(db, job, model, default);
        Assert.Equal(2, job.Calls); Assert.Contains("Claim category must be one of", prompts[1].Input);
        Assert.DoesNotContain(await db.ImportProposals.ToListAsync(), x => x.Category == "place");
        Assert.True(job.Processed > 0);
    }
    [Fact]
    public void LiteralNewlinesInJsonStringsAreEscapedWithoutChangingTheText()
    {
        var malformed = "```json\n{\"summary\":\"first line\nsecond line\tend\",\"path\":\"C:\\\\tmp\",\"quote\":\"say \\\"hi\\\"\"}\n```";
        using var doc = JsonDocument.Parse(Reconstruction.UnwrapJson(malformed));
        Assert.Equal("first line\nsecond line\tend", doc.RootElement.GetProperty("summary").GetString());
        Assert.Equal("say \"hi\"", doc.RootElement.GetProperty("quote").GetString());
        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(Reconstruction.UnwrapJson("{\"summary\":\"truncated")));
    }
    [Fact]
    public async Task LongDocxConsolidatesDraftsAndApprovesEntitiesEndingAndOriginalHistory()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            using var writer = new StreamWriter(zip.CreateEntry("word/document.xml").Open());
            writer.Write("<document xmlns='http://schemas.openxmlformats.org/wordprocessingml/2006/main'><body><p><pPr><pStyle val='Heading1'/></pPr><t>Character Preview</t></p><p><t>⚔️</t></p>");
            for (var i = 0; i < 450; i++) writer.Write($"<p><t>Day {i}: Mara the navigator and Lyra discuss the silver key, crossing at low tide, and supplies. Navigation is F · 2/5. Correction: two rations remain.</t></p>");
            writer.Write("<tbl><tr><tc><p><t>Rations: 2</t></p></tc></tr></tbl><p><t>Player: Pack two rations. At the Harbor on Day 7 at noon, Mara and Lyra prepare to cross the bay.</t></p></body></document>");
        }
        await using var db = Db(); var job = await Create(db, stream.ToArray(), "large.docx");
        await Complete(db, job);
        Assert.True(job.Total > 450); Assert.Equal(job.Total, job.Processed); Assert.True(job.ReviewCompleted);
        Assert.Empty(await db.Facts.ToListAsync());
        Assert.Equal(10, await db.ImportProposals.CountAsync(x => x.JobId == job.Id && x.Current));
        Assert.Contains(await db.Segments.ToListAsync(), x => x.Artifact);
        Assert.Contains(await db.Segments.ToListAsync(), x => x.Section == "table");
        var resume = Json.Read<ResumeProposal>(job.ResumeJson!); Assert.Equal("Harbor", resume.State!.Location); Assert.True(resume.ReplyPending);
        await ImportReview.Approve(db, job.Id, new(job.ExpectedCheckpointId, job.ProposalRevision), default);
        Assert.Equal("approved", job.Status); Assert.Equal(2, await db.Characters.CountAsync()); Assert.Single(await db.Relationships.ToListAsync()); Assert.Single(await db.Knowledge.ToListAsync());
        Assert.True(await db.Messages.CountAsync() > 450); Assert.Contains(await db.Facts.ToListAsync(), x => x.Kind == "secret" && x.Visibility == "narrator");
        Assert.Single(await db.WorldVersions.ToListAsync());
        var branch = await db.Branches.SingleAsync(); var state = Json.Read<StoryState>((await db.Checkpoints.SingleAsync(x => x.Id == branch.HeadCheckpointId)).StateJson);
        Assert.Equal(2, state.Inventory["Rations"]);
        var exported = await PortabilityService.Export(db, branch.CampaignId, default);
        var restored = await PortabilityService.Import(db, Json.Read<PortableExport>(Json.Write(exported)), "Restored", default);
        Assert.Equal(10, await db.ImportProposals.CountAsync(x => x.Current && x.JobId != job.Id));
        Assert.Equal(2, await db.Characters.CountAsync(x => x.CampaignId == restored.CampaignId));
        Assert.Equal(state, Json.Read<StoryState>((await db.Checkpoints.SingleAsync(x => x.Id == restored.HeadCheckpointId)).StateJson), new StateComparer());
    }
    private sealed class StateComparer : IEqualityComparer<StoryState>
    { public bool Equals(StoryState? a, StoryState? b) => Json.Write(a) == Json.Write(b); public int GetHashCode(StoryState obj) => Json.Write(obj).GetHashCode(); }
    [Fact]
    public async Task InvalidModelPreservesEvidenceAndDoesNotCreateFallbackFactsOrAdvanceCoverage()
    {
        await using var db = Db(); var job = await Create(db); await Reconstruction.Step(db, job, null, default);
        var invalid = new Invalid();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Reconstruction.Step(db, job, invalid, default));
        Assert.Equal(2, invalid.Calls); Assert.Equal(0, job.Processed); Assert.Equal("extract", job.Stage);
        Assert.Empty(await db.Facts.ToListAsync()); Assert.Empty(await db.ImportProposals.ToListAsync()); Assert.Single(await db.Segments.ToListAsync());
        await Complete(db, job); Assert.Equal("review", job.Status);
    }
    [Fact]
    public async Task RestartResumesOnlyIncompleteStagesAndPortablePlansRemapIds()
    {
        Guid id;
        await using (var db = Db())
        {
            var job = await Create(db); id = job.Id;
            await Reconstruction.Step(db, job, null, default); await Reconstruction.Step(db, job, new Scripted(Respond), default);
        }
        await using (var db = Db())
        {
            var job = await db.ImportJobs.SingleAsync(x => x.Id == id);
            await Reconstruction.Step(db, job, new Scripted(Respond), default); Assert.Equal("reconcile", job.Stage);
            await Reconstruction.Step(db, job, new Scripted(Respond), default); // Persist evidence windows as well as the legacy ID plan.
            var branch = await db.Branches.SingleAsync(); var portable = await PortabilityService.Export(db, branch.CampaignId, default);
            var restored = await PortabilityService.Import(db, portable, "Copy", default);
            var copied = await db.ImportJobs.SingleAsync(x => x.BranchId == restored.Id); copied.Status = "queued";
            var windows = Json.Read<ReviewWindow[]>((await db.ImportResults.SingleAsync(x => x.JobId == copied.Id && x.Stage == "reconcile-windows")).ResultJson);
            Assert.All(windows.SelectMany(x => x.ProposalIds), proposalId => Assert.Contains(db.ImportProposals.Local, x => x.Id == proposalId && x.JobId == copied.Id));
            await Complete(db, copied); Assert.Single(await db.ImportResults.Where(x => x.JobId == copied.Id && x.Stage == "extract").ToListAsync());
            await Complete(db, job); Assert.Single(await db.ImportResults.Where(x => x.JobId == job.Id && x.Stage == "extract").ToListAsync());
        }
    }
    [Fact]
    public async Task StaleRevisionAndAdvancedBranchRejectApproval()
    {
        await using var db = Db(); var job = await Create(db); await Complete(db, job);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => ImportReview.Approve(db, job.Id, new(job.ExpectedCheckpointId, job.ProposalRevision - 1), default));
        var run = new GenerationRun { BranchId = job.BranchId, ExpectedCheckpointId = job.ExpectedCheckpointId, Action = "Wait" };
        db.GenerationRuns.Add(run); await db.SaveChangesAsync(); await CampaignService.CommitTurn(db, run, "Waited.", default);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => ImportReview.Approve(db, job.Id, new(job.ExpectedCheckpointId, job.ProposalRevision), default));
        Assert.Empty(await db.Facts.ToListAsync());
    }
    [Fact]
    public async Task MissingEndingRaisesIssueAndCannotApproveEmptyState()
    {
        await using var db = Db(); var job = await Create(db);
        var model = new Scripted(p => new([], [], p.Input.StartsWith("STAGE: resume") ? new(StoryState.Empty, Evidence(p).Take(1).ToArray(), "", false, ["final location"]) : null, "Ending uncertain.", []));
        await Complete(db, job, model);
        Assert.Contains(await db.ImportIssues.ToListAsync(), x => x.Code == "resume:scene");
        await Assert.ThrowsAsync<InvalidOperationException>(() => ImportReview.Approve(db, job.Id, new(job.ExpectedCheckpointId, job.ProposalRevision), default));
        foreach (var issue in await db.ImportIssues.ToListAsync()) issue.Resolution = "Acknowledged"; await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => ImportReview.Approve(db, job.Id, new(job.ExpectedCheckpointId, job.ProposalRevision), default));
        Assert.Single(await db.Checkpoints.ToListAsync());
    }
    [Fact]
    public async Task BudgetAndFixturePreserveSourceWithoutPretendingToReconstruct()
    {
        await using var db = Db(); var job = await Create(db); job.MaxCalls = 1;
        await Reconstruction.Step(db, job, new FixtureProvider(), default);
        await Reconstruction.Step(db, job, new FixtureProvider(), default); Assert.Equal("paused", job.Status); Assert.Empty(await db.ImportProposals.ToListAsync());
        job.Status = "queued"; await Reconstruction.Step(db, job, new Scripted(Respond), default);
        await Reconstruction.Step(db, job, new Scripted(Respond), default);
        await Reconstruction.Step(db, job, new Scripted(Respond), default); // Persist review windows without using a model call.
        await Assert.ThrowsAsync<InvalidOperationException>(() => Reconstruction.Step(db, job, new Scripted(Respond), default)); Assert.Equal(1, job.Calls);
    }
    [Fact]
    public async Task CancellationOrCompetingLeasePreventsInflightResultsFromCommitting()
    {
        Guid id;
        await using (var setup = Db()) { var job = await Create(setup); id = job.Id; await Reconstruction.Step(setup, job, null, default); }
        await using var worker = Db(); var stale = await worker.ImportJobs.SingleAsync(x => x.Id == id);
        await using (var cancel = Db()) { var job = await cancel.ImportJobs.SingleAsync(x => x.Id == id); job.Status = "cancelled"; job.LeaseOwner = null; await cancel.SaveChangesAsync(); }
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => Reconstruction.Step(worker, stale, new Scripted(Respond), default));
        await using var verify = Db(); Assert.Empty(await verify.ImportProposals.ToListAsync());
        var cancelled = await verify.ImportJobs.SingleAsync(); cancelled.Status = "queued"; cancelled.LeaseOwner = "first"; await verify.SaveChangesAsync();
        await using var a = Db(); await using var b = Db(); var ja = await a.ImportJobs.SingleAsync(); var jb = await b.ImportJobs.SingleAsync();
        ja.LeaseOwner = "second"; await a.SaveChangesAsync(); jb.LeaseOwner = "third";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => b.SaveChangesAsync());
    }
    [Fact]
    public async Task ReviewExceptionsAndEditedInventoryMustAgreeWithFinalState()
    {
        await using var db = Db(); var job = await Create(db); await Complete(db, job);
        db.ImportIssues.Add(new ImportIssue { JobId = job.Id, Code = "branch", Text = "Which timeline?", Blocking = true }); await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => ImportReview.Approve(db, job.Id, new(job.ExpectedCheckpointId, job.ProposalRevision), default));
        (await db.ImportIssues.SingleAsync()).Resolution = "Use the final timeline";
        var rations = await db.ImportProposals.SingleAsync(x => x.Key == "inventory:rations" && x.Current); var claim = Json.Read<ImportClaim>(rations.ContentJson); rations.ContentJson = Json.Write(claim with { Amount = 7 }); await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => ImportReview.Approve(db, job.Id, new(job.ExpectedCheckpointId, job.ProposalRevision), default));
        Assert.Empty(await db.Facts.ToListAsync());
    }
}
