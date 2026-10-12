using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Story;
using Xunit;

namespace StoryTests;

public class ImportReliabilityTests : IAsyncLifetime
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private StoryDb Db() => new(new DbContextOptionsBuilder<StoryDb>().UseSqlite(connection).Options);
    public async Task InitializeAsync() { await connection.OpenAsync(); await using var db = Db(); await db.Database.EnsureCreatedAsync(); }
    public Task DisposeAsync() => connection.DisposeAsync().AsTask();
    private static ProviderRouting Routing() => new(new ProviderFactory(new Clients(), new ConfigurationBuilder().Build()), new ConfigurationBuilder().Build());
    private sealed class Clients : IHttpClientFactory { public HttpClient CreateClient(string name) => new(); }

    [Theory]
    [InlineData("story.json", "{broken")]
    [InlineData("story.json", "{\"messages\":[{\"role\":\"user\"}]}")]
    [InlineData("story.json", "{\"messages\":[{\"role\":7,\"content\":\"hi\"}]}")]
    [InlineData("story.json", "{\"messages\":[]}")]
    [InlineData("story.docx", "not a zip")]
    public async Task BadPreparationStopsAndRetainsOriginal(string name, string text)
    {
        await using var db = Db(); var branch = await CampaignService.Create(db, new("Failed preparation", false), default);
        var bytes = Encoding.UTF8.GetBytes(text);
        var job = await ImportPreparation.Upload(db, Routing(), branch.Id, name, bytes, new(), true, "missing-model", "", "current", "", branch.HeadCheckpointId, default);
        Assert.Equal("paused", job.Status); Assert.Equal("normalize", job.Stage); Assert.Equal(0, job.Calls);
        Assert.Contains("Source preparation failed", job.Error); Assert.Equal(bytes, (await db.Sources.SingleAsync()).Bytes);
        Assert.Empty(await db.Segments.ToListAsync());
    }
    [Fact]
    public async Task PreviewDoesNotRequireProviderAndForkPreservesCurrentHead()
    {
        await using var db = Db(); var branch = await CampaignService.Create(db, new("New timeline", false), default);
        var job = await ImportPreparation.Upload(db, Routing(), branch.Id, "source.txt", Encoding.UTF8.GetBytes("Mara waits at the Harbor."), new(), true, "disabled-provider", "", "new", "Imported story", branch.HeadCheckpointId, default);
        Assert.Equal("prepared", job.Status); Assert.Equal(0, job.Calls); Assert.NotEqual(branch.Id, job.BranchId);
        Assert.Equal(branch.HeadCheckpointId, (await db.Branches.SingleAsync(x => x.Id == branch.Id)).HeadCheckpointId);
        Assert.Equal(2, await db.Branches.CountAsync());
    }
    [Fact]
    public async Task InvalidForkRollsBackWithoutSourceOrExtraTimeline()
    {
        await using var db = Db(); var branch = await CampaignService.Create(db, new("Original", false), default);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => ImportPreparation.Upload(db, Routing(), branch.Id, "source.txt", Encoding.UTF8.GetBytes("Story"), new(), true, null, null, "new", "Imported", Guid.NewGuid(), default));
        Assert.Equal(1, await db.Branches.CountAsync()); Assert.Empty(await db.Sources.ToListAsync());
    }
    private async Task<ImportJob> Ready(StoryDb db)
    {
        var branch = await CampaignService.Create(db, new("Review", false), default);
        var job = await ImportPreparation.Upload(db, Routing(), branch.Id, "source.txt", Encoding.UTF8.GetBytes("Mara is at the Harbor. Lyra stands beside her."), new(), true, null, null, "current", "", branch.HeadCheckpointId, default);
        var state = StoryState.Empty with { Location = "Harbor", Objective = "Cross the bay", Participants = [], Skills = new(), Inventory = new() };
        job.Status = "review"; job.ReviewCompleted = true; job.Processed = job.Total;
        job.ResumeJson = Json.Write(new ResumeProposal(state, [0], "Wait", false, [], "Mara"));
        await db.SaveChangesAsync(); return job;
    }
    [Fact]
    public async Task ReadinessReevaluatesStructuralWarningsButRetainsUncertainty()
    {
        await using var db = Db(); var job = await Ready(db);
        db.ImportIssues.AddRange(new ImportIssue { JobId = job.Id, Code = "resume:player", Text = "Old missing player", Blocking = true }, new ImportIssue { JobId = job.Id, Code = "uncertain:weather", Text = "Weather unknown", Blocking = true }); await db.SaveChangesAsync();
        var readiness = await ImportApproval.Evaluate(db, job, default);
        Assert.Single(readiness.Blockers); Assert.Equal("uncertain:weather", readiness.Blockers[0].Code);
        (await db.ImportIssues.SingleAsync(x => x.Code == "uncertain:weather")).Resolution = "Unknown is acceptable"; await db.SaveChangesAsync();
        Assert.True((await ImportApproval.Evaluate(db, job, default)).CanApprove);
        await ImportReview.Approve(db, job.Id, new(job.ExpectedCheckpointId, job.ProposalRevision), default);
        Assert.Equal("approved", job.Status);
    }
    [Fact]
    public async Task ReadinessDetectsAdvancedTimelineAndPausedRunBeforeApproval()
    {
        await using var db = Db(); var job = await Ready(db); var branch = await db.Branches.SingleAsync();
        var checkpoint = new Checkpoint { BranchId = branch.Id, Sequence = 2, StateJson = (await db.Checkpoints.SingleAsync()).StateJson };
        db.Add(checkpoint); branch.HeadCheckpointId = checkpoint.Id;
        db.GenerationRuns.Add(new GenerationRun { BranchId = branch.Id, ExpectedCheckpointId = checkpoint.Id, Status = "paused" }); await db.SaveChangesAsync();
        var readiness = await ImportApproval.Evaluate(db, job, default);
        Assert.Contains(readiness.Blockers, x => x.Code == "timeline"); Assert.Contains(readiness.Blockers, x => x.Code == "run");
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => ImportReview.Approve(db, job.Id, new(checkpoint.Id, job.ProposalRevision), default));
        Assert.Equal("review", job.Status);
    }
    [Fact]
    public async Task ProposalSelectorPagesAll180CitationsAndRejectsAnotherJobsProposal()
    {
        await using var db = Db(); var job = await Ready(db);
        for (var i = 1; i < 180; i++) db.Segments.Add(new ImportSegment { JobId = job.Id, Ordinal = i, Text = "Source " + i });
        var claim = new ImportClaim("fact:history", "fact", "Mara's history.", "", "", "", 0, "fact", "public", [], .9, Enumerable.Range(0, 180).ToArray(), "historical", []);
        var proposal = new ImportProposal { JobId = job.Id, Key = claim.Key, Category = claim.Category, ContentJson = Json.Write(claim) }; db.Add(proposal); await db.SaveChangesAsync();
        var seen = new List<int>();
        for (var page = 0; page < 9; page++) { var result = await ImportReview.EvidencePage(db, job.Id, null, proposal.Id, page, default); Assert.Equal(180, result.Total); Assert.Equal(20, result.Items.Count); seen.AddRange(result.Items.Select(x => x.Ordinal)); }
        Assert.Equal(Enumerable.Range(0, 180), seen);
        var other = await Ready(db);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => ImportReview.EvidencePage(db, other.Id, null, proposal.Id, 0, default));
    }
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public async Task PortableVersionsRestorePreparingJobsAsPaused(int version)
    {
        await using var db = Db(); var job = await Ready(db); job.Status = "preparing"; await db.SaveChangesAsync();
        var branch = await db.Branches.SingleAsync(); var export = await PortabilityService.Export(db, branch.CampaignId, default);
        var restored = await PortabilityService.Import(db, export with { Version = version }, "Copy", default);
        Assert.Equal("paused", (await db.ImportJobs.SingleAsync(x => x.BranchId == restored.Id)).Status);
    }
    [Fact]
    public async Task MalformedPortableReferencesDuplicatesAndJsonFailBeforePersistence()
    {
        await using var db = Db(); var branch = await CampaignService.Create(db, new("Export", false), default);
        var export = await PortabilityService.Export(db, branch.CampaignId, default);
        var bad = new[] { export with { Sources = null! }, export with { Branches = [.. export.Branches, export.Branches[0]] },
            export with { Checkpoints = [] }, export with { Checkpoints = [new Checkpoint { Id = export.Checkpoints[0].Id, BranchId = branch.Id, StateJson = "broken" }] } };
        foreach (var item in bad) await Assert.ThrowsAsync<InvalidOperationException>(() => PortabilityService.Import(db, item, "Invalid", default));
        Assert.Equal(1, await db.Campaigns.CountAsync()); Assert.DoesNotContain(db.ChangeTracker.Entries(), x => x.State == EntityState.Added);
    }
    [Fact]
    public async Task ReanalysisPreparesWithoutModelAndCanForkOriginalCheckpoint()
    {
        await using var db = Db(); var job = await Ready(db);
        var next = await ImportReview.Reanalyze(db, Routing(), job.Id, new(PrepareOnly: true, ForkOriginal: true), default);
        Assert.Equal("prepared", next.Status); Assert.NotEqual(job.Id, next.Id); Assert.NotEqual(job.BranchId, next.BranchId);
        Assert.Equal("review", job.Status); Assert.Equal(job.SourceId, next.SourceId); Assert.Equal(0, next.Calls);
    }
    [Fact]
    public async Task ReadinessRequiresPlayerAndCorrectStateEvenAfterWarningAcknowledgment()
    {
        await using var db = Db(); var job = await Ready(db);
        var resume = Json.Read<ResumeProposal>(job.ResumeJson!);
        job.ResumeJson = Json.Write(resume with { PlayerCharacterName = "" });
        db.ImportIssues.Add(new ImportIssue { JobId = job.Id, Code = "resume:player", Blocking = true, Resolution = "Acknowledged" });
        await db.SaveChangesAsync();
        Assert.Contains((await ImportApproval.Evaluate(db, job, default)).Blockers, x => x.Code == "resume:player");
        job.ResumeJson = Json.Write(resume);
        var claim = new ImportClaim("inventory:rations", "inventory", "Two rations remain.", "Rations", "", "", 2, "fact", "public", [], .9, [0], "current", []);
        var row = new ImportProposal { JobId = job.Id, Key = claim.Key, Category = claim.Category, ContentJson = Json.Write(claim) }; db.Add(row); await db.SaveChangesAsync();
        Assert.Contains((await ImportApproval.Evaluate(db, job, default)).Blockers, x => x.Code.StartsWith("state:") && x.ProposalId == row.Id);
        job.ResumeJson = Json.Write(resume with { State = resume.State! with { Inventory = new() { ["Rations"] = 2 } } }); await db.SaveChangesAsync();
        Assert.True((await ImportApproval.Evaluate(db, job, default)).CanApprove);
    }
    [Fact]
    public async Task PortableNullSourceAndRequiredJsonRejectBeforePersistence()
    {
        await using var db = Db(); var job = await Ready(db); var branch = await db.Branches.SingleAsync();
        var export = await PortabilityService.Export(db, branch.CampaignId, default);
        export.Sources[0].Bytes = null!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => PortabilityService.Import(db, export, "Invalid", default));
        export.Sources[0].Bytes = [];
        export.Checkpoints[0].StateJson = null!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => PortabilityService.Import(db, export, "Invalid", default));
        Assert.Equal(1, await db.Campaigns.CountAsync());
    }
}
