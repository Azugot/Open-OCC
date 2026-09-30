using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Story;
using Xunit;

namespace StoryTests;

public class CampaignDeletionTests : IAsyncLifetime
{
    readonly SqliteConnection connection = new("Data Source=:memory:");
    StoryDb Db() => new(new DbContextOptionsBuilder<StoryDb>().UseSqlite(connection).Options);
    public async Task InitializeAsync() { await connection.OpenAsync(); await using var db = Db(); await db.Database.EnsureCreatedAsync(); }
    public Task DisposeAsync() => connection.DisposeAsync().AsTask();

    [Fact]
    public async Task DeletesAllBranchesAndImportsButPreservesOtherStoryAndSharedWorld()
    {
        await using var db = Db();
        var world = await WorldService.Create(db, new("Shared world"), default);
        var version = await WorldService.CreateVersion(db, world.Id, new(), default);
        var branch = await CampaignService.Create(db, new("Delete me", true, version.Id), default);
        var keep = await CampaignService.Create(db, new("Keep me", true, version.Id), default);
        var source = new ImportedSource { CampaignId = branch.CampaignId, FileName = "story.txt", Bytes = [1] };
        var job = new ImportJob { BranchId = branch.Id, SourceId = source.Id, Status = "paused" };
        var segment = new ImportSegment { JobId = job.Id, Text = "Evidence" };
        var fact = new Fact { BranchId = branch.Id, JobId = job.Id, ReviewStatus = "accepted", Text = "Known fact" };
        var character = new Character { CampaignId = branch.CampaignId, Name = "Mara" };
        var other = new Character { CampaignId = branch.CampaignId, Name = "Ivo" };
        db.AddRange(source, job, segment, fact, character, other);
        db.AddRange(new FactEvidence { FactId = fact.Id, SegmentId = segment.Id },
            new FactCorrection { BranchId = branch.Id, FactId = fact.Id },
            new KnowledgeRecord { BranchId = branch.Id, CharacterId = character.Id, FactId = fact.Id },
            new Relationship { BranchId = branch.Id, FromCharacterId = character.Id, ToCharacterId = other.Id },
            new ImportSection { JobId = job.Id }, new ImportProposal { JobId = job.Id }, new ImportIssue { JobId = job.Id }, new ImportStageResult { JobId = job.Id },
            new GenerationRun { BranchId = branch.Id, Status = "paused" }, new NarrativeThread { BranchId = branch.Id },
            new MechanicsLedgerEntry { BranchId = branch.Id }, new StoryEvent { BranchId = branch.Id }, new CampaignSummary { BranchId = branch.Id });
        await db.SaveChangesAsync();
        await CampaignService.Fork(db, branch.Id, new(branch.HeadCheckpointId, "Alternate"), default);
        await CampaignDeletion.Delete(db, branch.CampaignId, default);
        await using var verify = Db();
        Assert.Equal(keep.CampaignId, (await verify.Campaigns.SingleAsync()).Id);
        Assert.Equal(keep.Id, (await verify.Branches.SingleAsync()).Id);
        Assert.Single(await verify.Checkpoints.ToListAsync()); Assert.Single(await verify.Messages.ToListAsync());
        Assert.Empty(await verify.Sources.ToListAsync()); Assert.Empty(await verify.ImportJobs.ToListAsync());
        Assert.Empty(await verify.Segments.ToListAsync()); Assert.Empty(await verify.Evidence.ToListAsync()); Assert.Empty(await verify.Facts.ToListAsync());
        Assert.Empty(await verify.Characters.ToListAsync()); Assert.Empty(await verify.Relationships.ToListAsync()); Assert.Empty(await verify.Knowledge.ToListAsync());
        Assert.Empty(await verify.GenerationRuns.ToListAsync()); Assert.Empty(await verify.ImportProposals.ToListAsync());
        Assert.Empty(await verify.ImportIssues.ToListAsync()); Assert.Empty(await verify.ImportSections.ToListAsync()); Assert.Empty(await verify.ImportResults.ToListAsync());
        Assert.Empty(await verify.FactCorrections.ToListAsync()); Assert.Empty(await verify.Events.ToListAsync()); Assert.Empty(await verify.Mechanics.ToListAsync());
        Assert.Empty(await verify.NarrativeThreads.ToListAsync()); Assert.Empty(await verify.Summaries.ToListAsync());
        Assert.Single(await verify.Worlds.ToListAsync()); Assert.Equal(2, await verify.WorldVersions.CountAsync());
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task ActiveWorkRejectsDeletionWithoutChangingAnything(bool generation)
    {
        await using var db = Db();
        var branch = await CampaignService.Create(db, new("Busy story"), default);
        if (generation) db.Add(new GenerationRun { BranchId = branch.Id, Status = "running" });
        else {
            var source = new ImportedSource { CampaignId = branch.CampaignId };
            db.AddRange(source, new ImportJob { BranchId = branch.Id, SourceId = source.Id, Status = "processing", LeaseOwner = "worker", LeaseUntil = DateTime.UtcNow.AddMinutes(1) });
        }
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => CampaignDeletion.Delete(db, branch.CampaignId, default));
        Assert.Single(await db.Campaigns.ToListAsync()); Assert.Single(await db.Checkpoints.ToListAsync()); Assert.Single(await db.Messages.ToListAsync());
    }

    [Fact]
    public async Task FailureAtFinalDeleteRollsBackEarlierHistoryDeletes()
    {
        await using var db = Db();
        var branch = await CampaignService.Create(db, new("Protected story"), default);
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER prevent_campaign_delete BEFORE DELETE ON Campaigns BEGIN SELECT RAISE(ABORT, 'simulated concurrent constraint'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => CampaignDeletion.Delete(db, branch.CampaignId, default));
        await using var verify = Db();
        Assert.Single(await verify.Campaigns.ToListAsync()); Assert.Single(await verify.Branches.ToListAsync());
        Assert.Single(await verify.Checkpoints.ToListAsync()); Assert.Single(await verify.Messages.ToListAsync());
    }

    [Fact]
    public async Task MissingStoryReturnsNotFoundAndCancellationLeavesStoryUntouched()
    {
        await using var db = Db();
        var branch = await CampaignService.Create(db, new("Safe story"), default);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => CampaignDeletion.Delete(db, Guid.NewGuid(), default));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CampaignDeletion.Delete(db, branch.CampaignId, cancel.Token));
        Assert.Single(await db.Campaigns.ToListAsync());
    }
}
