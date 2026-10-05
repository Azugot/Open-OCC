using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Story;
using Xunit;

namespace StoryTests;

public class CampaignGuidanceTests : IAsyncLifetime
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private StoryDb Db() => new(new DbContextOptionsBuilder<StoryDb>().UseSqlite(connection).Options);
    public async Task InitializeAsync() { await connection.OpenAsync(); await using var db = Db(); await db.Database.EnsureCreatedAsync(); }
    public Task DisposeAsync() => connection.DisposeAsync().AsTask();
    private sealed class Agent : IAgentProvider
    {
        public bool Pause { get; set; }
        public List<AgentRequest> Requests { get; } = [];
        public Task<T> Generate<T>(AgentRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            if (Pause && request.Task == "narrate") throw new InvalidOperationException("Pause for retry test.");
            object result = request.Example;
            if (result is TurnPlan plan) result = plan with { PlayerAttempt = new("Attempt", true, 1, [], "Contested attempt.") };
            if (result is Attempt attempt) result = attempt with { Uncertain = true, Difficulty = 1 };
            return Task.FromResult(Json.Read<T>(Json.Write(result)));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PlayerAndNpcChecksUseCapturedSettingsAcrossRetries(bool enabled)
    {
        await using var db = Db();
        var branch = await CampaignService.Create(db, new("Campaign controls"), default);
        await CampaignService.UpdateSettings(db, branch.CampaignId, new(enabled, "Enhance every scene."), default);
        var run = new GenerationRun { BranchId = branch.Id, ExpectedCheckpointId = branch.HeadCheckpointId, Action = "Persuade Lyra" };
        db.Add(run); await db.SaveChangesAsync();
        var agent = new Agent { Pause = true }; var config = new ConfigurationBuilder().Build();
        await Assert.ThrowsAsync<InvalidOperationException>(() => StoryEngine.Execute(db, run, config, default, agent, agent));
        var saved = await db.GenerationRuns.SingleAsync(x => x.Id == run.Id);
        var draft = Json.Read<TurnDraft>(saved.DraftJson);
        Assert.Equal(enabled, draft.Guidance!.RollModeEnabled);
        if (enabled) { Assert.Contains(draft.Rolls.Values, x => x.ActorId == "player"); Assert.Contains(draft.Rolls.Values, x => x.ActorId != "player"); }
        else Assert.Empty(draft.Rolls);
        var rollJson = Json.Write(draft.Rolls);
        await CampaignService.UpdateSettings(db, branch.CampaignId, new(!enabled, "New guidance for future turns."), default);
        agent.Pause = false;
        await StoryEngine.Execute(db, saved, config, default, agent, agent);
        Assert.Equal("completed", saved.Status);
        Assert.Equal(rollJson, Json.Write(Json.Read<TurnDraft>(saved.DraftJson).Rolls));
        Assert.All(agent.Requests, x => {
            Assert.Contains("Enhance every scene.", x.Instructions);
            Assert.Contains("take priority over world author instructions", x.Instructions);
            Assert.Contains("Mandatory privacy", x.Instructions);
            Assert.DoesNotContain("New guidance", x.Instructions);
        });
        foreach (var task in new[] { "plan", "character", "assess", "resolve", "narrate" }) Assert.Contains(agent.Requests, x => x.Task == task);
        var head = await db.Branches.AsNoTracking().Where(x => x.Id == branch.Id).Select(x => x.HeadCheckpointId).SingleAsync();
        var next = new GenerationRun { BranchId = branch.Id, ExpectedCheckpointId = head, Action = "Persuade Lyra" };
        db.Add(next); await db.SaveChangesAsync();
        var nextAgent = new Agent();
        await StoryEngine.Execute(db, next, config, default, nextAgent, nextAgent);
        var nextDraft = Json.Read<TurnDraft>(next.DraftJson);
        Assert.Equal(!enabled, nextDraft.Guidance!.RollModeEnabled);
        Assert.All(nextAgent.Requests, x => Assert.Contains("New guidance", x.Instructions));
        if (enabled) Assert.Empty(nextDraft.Rolls); else Assert.NotEmpty(nextDraft.Rolls);
    }

    [Fact]
    public async Task LegacyPausedDraftRetainsOriginalRollBehavior()
    {
        await using var db = Db(); var branch = await CampaignService.Create(db, new("Legacy paused turn"), default);
        var cp = await db.Checkpoints.SingleAsync(x => x.Id == branch.HeadCheckpointId);
        var draft = new TurnDraft { World = StoryWorld.From(cp), State = StoryState.Synthetic };
        var run = new GenerationRun { BranchId = branch.Id, ExpectedCheckpointId = cp.Id, Action = "Persuade Lyra", Status = "paused", DraftJson = Json.Write(draft) };
        db.Add(run); await db.SaveChangesAsync();
        await CampaignService.UpdateSettings(db, branch.CampaignId, new(false, "New campaign guidance"), default);
        var agent = new Agent();
        await StoryEngine.Execute(db, run, new ConfigurationBuilder().Build(), default, agent, agent);
        var resumed = Json.Read<TurnDraft>(run.DraftJson);
        Assert.True(resumed.Guidance!.RollModeEnabled); Assert.Empty(resumed.Guidance.GeneralInstructions);
        Assert.NotEmpty(resumed.Rolls);
        Assert.All(agent.Requests, x => Assert.DoesNotContain("New campaign guidance", x.Instructions));
    }

    [Fact]
    public void MigrationBackfillsExistingCampaignsWithRollsEnabledAndBlankInstructions()
    {
        var migration = new Story.Api.Migrations.CampaignGuidance();
        var columns = migration.UpOperations.OfType<Microsoft.EntityFrameworkCore.Migrations.Operations.AddColumnOperation>().ToArray();
        Assert.Contains(columns, x => x.Table == "Campaigns" && x.Name == "RollModeEnabled" && Equals(x.DefaultValue, true) && !x.IsNullable);
        Assert.Contains(columns, x => x.Table == "Campaigns" && x.Name == "GeneralInstructions" && Equals(x.DefaultValue, "") && !x.IsNullable);
    }

    [Fact]
    public async Task SettingsValidatePersistAndRoundTripWhileLegacyExportsKeepDefaults()
    {
        await using var db = Db(); var branch = await CampaignService.Create(db, new("Defaults"), default);
        var campaign = await db.Campaigns.SingleAsync();
        Assert.True(campaign.RollModeEnabled); Assert.Empty(campaign.GeneralInstructions);
        await Assert.ThrowsAsync<InvalidOperationException>(() => CampaignService.UpdateSettings(db, campaign.Id, new(false, new string('x', 8001)), default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => CampaignService.UpdateSettings(db, Guid.NewGuid(), new(false, ""), default));
        await CampaignService.UpdateSettings(db, campaign.Id, new(false, "  Always accommodate player intent.  "), default);
        var export = await PortabilityService.Export(db, campaign.Id, default);
        Assert.Equal(5, export.Version);
        var copy = await PortabilityService.Import(db, export, "Copy", default);
        var restored = await db.Campaigns.SingleAsync(x => x.Id == copy.CampaignId);
        Assert.False(restored.RollModeEnabled); Assert.Equal("Always accommodate player intent.", restored.GeneralInstructions);
        var legacy = JsonNode.Parse(Json.Write(export))!;
        legacy["version"] = 4;
        legacy["campaign"]!.AsObject().Remove("rollModeEnabled");
        legacy["campaign"]!.AsObject().Remove("generalInstructions");
        var oldCopy = await PortabilityService.Import(db, Json.Read<PortableExport>(legacy.ToJsonString()), "Legacy", default);
        var oldCampaign = await db.Campaigns.SingleAsync(x => x.Id == oldCopy.CampaignId);
        Assert.True(oldCampaign.RollModeEnabled); Assert.Empty(oldCampaign.GeneralInstructions);
        var context = await new ContinuityService().Assemble(db, branch.Id, StoryState.Synthetic, "Continue", default);
        var prompt = ContinuityService.NarrationPrompt(context, "Continue");
        Assert.Contains(campaign.GeneralInstructions, prompt.Instructions);
        Assert.Contains("Narrator mode resolves actions narratively", prompt.Instructions);
        Assert.Contains("Mandatory privacy", prompt.Instructions);
        Assert.Equal(campaign.GeneralInstructions, Json.Read<StoryContext>(Json.Write(context)).Guidance!.GeneralInstructions);
    }
}
