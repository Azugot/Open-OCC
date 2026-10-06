using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Story;
using Xunit;

namespace StoryTests;

public class GenerationProgressTests : IAsyncLifetime
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private StoryDb Db() => new(new DbContextOptionsBuilder<StoryDb>().UseSqlite(connection).Options);
    public async Task InitializeAsync() { await connection.OpenAsync(); await using var db = Db(); await db.Database.EnsureCreatedAsync(); }
    public Task DisposeAsync() => connection.DisposeAsync().AsTask();
    private sealed class InspectAgent(Func<AgentRequest, Task> inspect) : IAgentProvider
    {
        public async Task<T> Generate<T>(AgentRequest request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); await inspect(request);
            return Json.Read<T>(Json.Write(request.Example));
        }
    }

    [Fact]
    public void MigrationAndLegacyProgressDefaultSafely()
    {
        var column = Assert.Single(new Story.Api.Migrations.GenerationProgress().UpOperations
            .OfType<Microsoft.EntityFrameworkCore.Migrations.Operations.AddColumnOperation>());
        Assert.Equal("GenerationRuns", column.Table); Assert.Equal("ProgressJson", column.Name);
        Assert.Equal("{}", column.DefaultValue); Assert.False(column.IsNullable);
        var run = new GenerationRun(); Assert.Equal("{}", run.ProgressJson);
        Assert.Equal("processing", GenerationProgress.View(run).Phase);
        run.ProgressJson = "{bad"; Assert.Equal("processing", GenerationProgress.View(run).Phase);
        run.ProgressJson = "{\"mode\":\"narrator\",\"phase\":\"secret-activity\"}";
        Assert.Equal("processing", GenerationProgress.View(run).Phase);
    }

    [Fact]
    public async Task EveryProviderCallSeesItsAlreadyPersistedStage()
    {
        await using var db = Db(); var branch = await CampaignService.Create(db, new("Progress", true), default);
        var run = new GenerationRun { BranchId = branch.Id, ExpectedCheckpointId = branch.HeadCheckpointId, Action = "Ask Lyra for help" };
        db.Add(run); await db.SaveChangesAsync(); var phases = new HashSet<string>();
        var agent = new InspectAgent(async request => {
            var saved = await db.GenerationRuns.AsNoTracking().SingleAsync(x => x.Id == run.Id);
            var expected = request.Task switch { "plan" => "director-planning", "character" => "cast-deciding", "assess" => "director-assessing", "resolve" => "director-resolving", "narrate" => "narrating", _ => throw new Exception(request.Task) };
            Assert.Equal(expected, GenerationProgress.View(saved).Phase); Assert.Equal("running", saved.Status); phases.Add(expected);
        });
        await StoryEngine.Execute(db, run, new ConfigurationBuilder().Build(), default, agent, agent);
        Assert.Equal(5, phases.Count); Assert.Equal("completed", run.Status);
        Assert.Equal("saving", GenerationProgress.View(run).Phase);
    }

    [Fact]
    public async Task RetryUpdatesClockAndSkipsCompletedWorkWhileFailureFreezesItsStage()
    {
        await using var db = Db(); var branch = await CampaignService.Create(db, new("Resume progress", true), default);
        var run = new GenerationRun { BranchId = branch.Id, ExpectedCheckpointId = branch.HeadCheckpointId, Action = "Continue" };
        db.Add(run); await db.SaveChangesAsync(); var plans = 0; var fail = true;
        var agent = new InspectAgent(request => {
            if (request.Task == "plan") plans++;
            if (request.Task == "resolve" && fail) throw new InvalidOperationException("Recorded failure");
            return Task.CompletedTask;
        });
        var config = new ConfigurationBuilder().Build();
        await Assert.ThrowsAsync<InvalidOperationException>(() => StoryEngine.Execute(db, run, config, default, agent, agent));
        run = await db.GenerationRuns.SingleAsync(x => x.Id == run.Id);
        Assert.Equal("paused", run.Status); var frozen = GenerationProgress.View(run);
        Assert.Equal("director-resolving", frozen.Phase); Assert.True(frozen.UpdatedAt >= frozen.StartedAt);
        Assert.Equal(frozen.UpdatedAt, GenerationProgress.View(run).UpdatedAt);
        fail = false; await StoryEngine.Execute(db, run, config, default, agent, agent);
        Assert.Equal(1, plans); Assert.Equal("completed", run.Status);
        Assert.True(GenerationProgress.View(run).StartedAt > frozen.StartedAt);
    }

    [Fact]
    public async Task ReadsAreScopedAndContainOnlyOperationalFields()
    {
        await using var db = Db(); var first = await CampaignService.Create(db, new("One", true), default);
        var second = await CampaignService.Create(db, new("Two", true), default);
        var run = new GenerationRun { BranchId = first.Id, ExpectedCheckpointId = first.HeadCheckpointId, DraftJson = "hidden-cast-and-private-thoughts" };
        GenerationProgress.Set(run, "autonomous", "cast-deciding"); db.Add(run); await db.SaveChangesAsync();
        var view = await GenerationProgress.Read(db, first.Id, null, first.HeadCheckpointId, default);
        Assert.Equal(run.Id, view!.RunId); Assert.DoesNotContain("hidden", Json.Write(view));
        Assert.Equal(new[] { "runId", "status", "mode", "phase", "startedAt", "phaseStartedAt", "updatedAt", "asOf" }, JsonNode.Parse(Json.Write(view))!.AsObject().Select(x => x.Key));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => GenerationProgress.Read(db, second.Id, run.Id, null, default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => GenerationProgress.Read(db, first.Id, null, second.HeadCheckpointId, default));
        Assert.Null(await GenerationProgress.Read(db, second.Id, null, second.HeadCheckpointId, default));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task VersionFiveRestoresRunningWorkAsStoppedWithOptionalProgress(bool legacy, bool savedDraft)
    {
        await using var db = Db(); var branch = await CampaignService.Create(db, new("Portable progress", true), default);
        var run = new GenerationRun { BranchId = branch.Id, ExpectedCheckpointId = branch.HeadCheckpointId };
        if (savedDraft) run.DraftJson = Json.Write(new TurnDraft { World = StoryWorld.Crownspire(), State = StoryState.Synthetic });
        GenerationProgress.Set(run, "autonomous", "narrating"); db.Add(run); await db.SaveChangesAsync();
        var export = await PortabilityService.Export(db, branch.CampaignId, default); Assert.Equal(5, export.Version);
        var node = JsonNode.Parse(Json.Write(export))!;
        if (legacy) foreach (var entry in node["runs"]!.AsArray()) entry!.AsObject().Remove("progressJson");
        var copy = await PortabilityService.Import(db, Json.Read<PortableExport>(node.ToJsonString()), "Restored", default);
        var restored = await db.GenerationRuns.SingleAsync(x => x.BranchId == copy.Id);
        Assert.Equal(savedDraft ? "paused" : "interrupted", restored.Status); Assert.Equal(legacy ? "processing" : "narrating", GenerationProgress.View(restored).Phase);
    }

    [Fact]
    public async Task CancellationFreezesProgressAndPreservesTheCheckpoint()
    {
        await using var db = Db(); var branch = await CampaignService.Create(db, new("Cancel progress", true), default);
        var run = new GenerationRun { BranchId = branch.Id, ExpectedCheckpointId = branch.HeadCheckpointId, Action = "Continue" };
        db.Add(run); await db.SaveChangesAsync(); using var cancellation = new CancellationTokenSource();
        var agent = new InspectAgent(_ => { cancellation.Cancel(); return Task.CompletedTask; });
        await StoryEngine.Execute(db, run, new ConfigurationBuilder().Build(), cancellation.Token, agent, agent);
        var saved = await db.GenerationRuns.SingleAsync(x => x.Id == run.Id);
        Assert.Equal("cancelled", saved.Status); Assert.Equal("director-planning", GenerationProgress.View(saved).Phase);
        Assert.Equal(branch.HeadCheckpointId, (await db.Branches.AsNoTracking().SingleAsync()).HeadCheckpointId);
        Assert.Equal(GenerationProgress.View(saved).UpdatedAt, GenerationProgress.View(saved).UpdatedAt);
    }

    [Fact]
    public async Task RestartRecoveryPausesActiveAutonomousWorkWithoutInventingAnotherStage()
    {
        await using var db = Db(); var branch = await CampaignService.Create(db, new("Restart progress", true), default);
        var run = new GenerationRun { BranchId = branch.Id, ExpectedCheckpointId = branch.HeadCheckpointId };
        GenerationProgress.Set(run, "autonomous", "narrating"); db.Add(run); await db.SaveChangesAsync();
        await StoryEngine.Recover(db);
        var saved = await db.GenerationRuns.SingleAsync(x => x.Id == run.Id);
        Assert.Equal("paused", saved.Status); Assert.Equal("narrating", GenerationProgress.View(saved).Phase);
    }
}
