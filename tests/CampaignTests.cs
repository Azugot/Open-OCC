using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Story;
using Xunit;

namespace StoryTests;

public class CampaignTests : IAsyncLifetime
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private StoryDb Db() => new(new DbContextOptionsBuilder<StoryDb>().UseSqlite(connection).Options);
    public async Task InitializeAsync() { await connection.OpenAsync(); await using var db = Db(); await db.Database.EnsureCreatedAsync(); }
    public Task DisposeAsync() => connection.DisposeAsync().AsTask();

    [Fact]
    public async Task CampaignSurvivesNewContextAndForkExcludesFutureTurns()
    {
        Guid branchId, initialId;
        await using (var db = Db())
        {
            var branch = await CampaignService.Create(db, new("Test world"), default);
            branchId = branch.Id; initialId = branch.HeadCheckpointId;
            var run = new GenerationRun { BranchId = branch.Id, ExpectedCheckpointId = initialId, Action = "Look around" };
            db.GenerationRuns.Add(run); await db.SaveChangesAsync();
            await CampaignService.CommitTurn(db, run, "Fixture reply", default);
        }
        await using (var db = Db())
        {
            var branch = await db.Branches.SingleAsync(x => x.Id == branchId);
            Assert.NotEqual(initialId, branch.HeadCheckpointId);
            Assert.Equal(3, await db.Messages.CountAsync());
            var fork = await CampaignService.Fork(db, branchId, new(initialId, "Other path"), default);
            Assert.Single(await db.Messages.Where(x => x.BranchId == fork.Id).ToListAsync());
            var state = Json.Read<StoryState>((await db.Checkpoints.SingleAsync(x => x.Id == fork.HeadCheckpointId)).StateJson);
            Assert.Equal(20, state.Inventory["Crowns"]);
        }
    }

    [Fact]
    public async Task StaleTurnCannotCommitAnyMessagesOrCheckpoint()
    {
        await using var db = Db();
        var branch = await CampaignService.Create(db, new("Concurrent world"), default);
        var a = new GenerationRun { BranchId = branch.Id, ExpectedCheckpointId = branch.HeadCheckpointId, Action = "First" };
        var b = new GenerationRun { BranchId = branch.Id, ExpectedCheckpointId = branch.HeadCheckpointId, Action = "Second" };
        db.AddRange(a, b); await db.SaveChangesAsync();
        await CampaignService.CommitTurn(db, a, "First reply", default);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => CampaignService.CommitTurn(db, b, "Stale reply", default));
        Assert.Equal(3, await db.Messages.CountAsync());
        Assert.Equal(2, await db.Checkpoints.CountAsync());
    }

    [Fact]
    public async Task ImportBatchesResumeWithoutDuplicateEvidenceAndApproveAtomically()
    {
        Guid jobId, branchId, head;
        var bytes = Encoding.UTF8.GetBytes(string.Join("\n\n", Enumerable.Range(1, 30).Select(i => $"Passage {i}")));
        await using (var db = Db())
        {
            var branch = await CampaignService.Create(db, new("Import world", false), default);
            branchId = branch.Id; head = branch.HeadCheckpointId;
            var source = new ImportedSource { CampaignId = branch.CampaignId, FileName = "story.txt", Bytes = bytes };
            var job = new ImportJob { SourceId = source.Id, BranchId = branch.Id, ExpectedCheckpointId = head };
            jobId = job.Id; db.AddRange(source, job); await db.SaveChangesAsync();
            await ImportWorker.ProcessBatch(db, job, default);
            Assert.Equal(25, job.Processed); Assert.Equal("processing", job.Status);
            Assert.Equal(head, branch.HeadCheckpointId);
        }
        await using (var db = Db())
        {
            var job = await db.ImportJobs.SingleAsync(x => x.Id == jobId);
            await ImportWorker.ProcessBatch(db, job, default);
            Assert.Equal("review", job.Status);
            Assert.Equal(30, await db.Segments.CountAsync()); Assert.Equal(30, await db.Evidence.CountAsync());
            Assert.Equal(bytes, (await db.Sources.SingleAsync()).Bytes);
            var facts = await db.Facts.ToListAsync();
            var decisions = facts.Select((f, i) => new ReviewDecision(f.Id, i == 0, i == 0 ? "The player has a secret." : f.Text, "narrator")).ToArray();
            await CampaignService.Approve(db, jobId, new(head, StoryState.Synthetic, decisions), default);
            Assert.Single(await db.Facts.Where(x => x.ReviewStatus == "accepted").ToListAsync());
            Assert.Empty(await db.Facts.Where(x => x.ReviewStatus == "accepted" && x.Visibility == "public").ToListAsync());
            Assert.Equal("approved", job.Status);
            var fork = await CampaignService.Fork(db, branchId, new(head, "Before import"), default);
            Assert.Empty(await db.Facts.Where(x => x.BranchId == fork.Id).ToListAsync());
        }
    }

    [Fact]
    public async Task InvalidReviewLeavesApprovedStateAndFactsUntouched()
    {
        await using var db = Db();
        var branch = await CampaignService.Create(db, new("Validation world"), default);
        var head = branch.HeadCheckpointId;
        var source = new ImportedSource { CampaignId = branch.CampaignId, FileName = "story.txt", Bytes = Encoding.UTF8.GetBytes("Evidence") };
        var job = new ImportJob { SourceId = source.Id, BranchId = branch.Id, ExpectedCheckpointId = head };
        db.AddRange(source, job); await db.SaveChangesAsync(); await ImportWorker.ProcessBatch(db, job, default);
        var invalid = StoryState.Synthetic with { Inventory = new() { ["Coins"] = -1 } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => CampaignService.Approve(db, job.Id, new(head, invalid, []), default));
        Assert.Equal(head, branch.HeadCheckpointId); Assert.Equal("review", job.Status);
        Assert.All(await db.Facts.ToListAsync(), f => Assert.Equal("pending", f.ReviewStatus));
    }

    [Fact]
    public async Task CancelledProviderDoesNotProduceNarration()
    {
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FixtureProvider().Narrate(new("Wait", StoryState.Synthetic, []), cancel.Token));
    }

    [Fact]
    public async Task CancellationWinsAgainstAnInflightImportBatch()
    {
        Guid jobId;
        await using (var db = Db())
        {
            var branch = await CampaignService.Create(db, new("Cancellation"), default);
            var source = new ImportedSource { CampaignId = branch.CampaignId, FileName = "source.txt", Bytes = Encoding.UTF8.GetBytes("Private passage") };
            var job = new ImportJob { SourceId = source.Id, BranchId = branch.Id, ExpectedCheckpointId = branch.HeadCheckpointId };
            jobId = job.Id; db.AddRange(source, job); await db.SaveChangesAsync();
        }
        await using var worker = Db();
        var staleJob = await worker.ImportJobs.SingleAsync(x => x.Id == jobId);
        await using (var cancel = Db())
        {
            var job = await cancel.ImportJobs.SingleAsync(x => x.Id == jobId);
            job.Status = "cancelled"; await cancel.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => ImportWorker.ProcessBatch(worker, staleJob, default));
        await using var verify = Db();
        Assert.Empty(await verify.Segments.ToListAsync());
        Assert.Empty(await verify.Facts.ToListAsync());
        Assert.Empty(await verify.Evidence.ToListAsync());
        Assert.Equal("cancelled", (await verify.ImportJobs.SingleAsync()).Status);
        Assert.Single(await verify.Checkpoints.ToListAsync());
    }

    [Fact]
    public async Task ApprovalCannotOverwriteTurnsCreatedAfterUpload()
    {
        await using var db = Db();
        var branch = await CampaignService.Create(db, new("Stale import"), default);
        var head = branch.HeadCheckpointId;
        var source = new ImportedSource { CampaignId = branch.CampaignId, FileName = "source.txt", Bytes = Encoding.UTF8.GetBytes("A fact") };
        var job = new ImportJob { SourceId = source.Id, BranchId = branch.Id, ExpectedCheckpointId = head };
        db.AddRange(source, job); await db.SaveChangesAsync(); await ImportWorker.ProcessBatch(db, job, default);
        var run = new GenerationRun { BranchId = branch.Id, ExpectedCheckpointId = head, Action = "Continue" };
        db.Add(run); await db.SaveChangesAsync(); await CampaignService.CommitTurn(db, run, "Fixture", default);
        var decisions = (await db.Facts.ToListAsync()).Select(f => new ReviewDecision(f.Id, true, f.Text, "public")).ToArray();
        await Assert.ThrowsAsync<InvalidOperationException>(() => CampaignService.Approve(db, job.Id, new(branch.HeadCheckpointId, StoryState.Synthetic, decisions), default));
        Assert.Equal("review", job.Status); Assert.Equal(3, await db.Messages.CountAsync());
        Assert.All(await db.Facts.ToListAsync(), f => Assert.Equal("pending", f.ReviewStatus));
    }

    [Theory]
    [InlineData("openai")][InlineData("anthropic")][InlineData("openai-compatible")][InlineData("ollama")]
    public async Task StubsFailExplicitlyWithoutFallback(string adapter)
    {
        var provider = ProviderRegistry.Resolve(adapter);
        Assert.False(provider.Capabilities.Available);
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.Narrate(new("Continue", StoryState.Synthetic, []), default));
    }

    [Fact]
    public void JsonImportKeepsSpeakerOrderAndRejectsUndocumentedShapes()
    {
        var entries = ImportParser.Parse("story.json", Encoding.UTF8.GetBytes("{\"messages\":[{\"role\":\"user\",\"content\":\"Hello\"},{\"role\":\"assistant\",\"content\":\"Welcome\"}]}"));
        Assert.Equal(new[] { "user", "assistant" }, entries.Select(x => x.Speaker));
        Assert.Throws<InvalidOperationException>(() => ImportParser.Parse("story.json", Encoding.UTF8.GetBytes("{}")));
    }
}
