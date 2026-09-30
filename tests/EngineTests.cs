using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Story;
using Xunit;

namespace StoryTests;

public class EngineTests : IAsyncLifetime
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private StoryDb Db() => new(new DbContextOptionsBuilder<StoryDb>().UseSqlite(connection).Options);
    private static IConfiguration Config => new ConfigurationBuilder().Build();
    public async Task InitializeAsync() { await connection.OpenAsync(); await using var db = Db(); await db.Database.EnsureCreatedAsync(); }
    public Task DisposeAsync() => connection.DisposeAsync().AsTask();

    private static async Task<GenerationRun> Start(StoryDb db, Branch branch, string action = "Look around")
    {
        var run = new GenerationRun { BranchId = branch.Id, ExpectedCheckpointId = branch.HeadCheckpointId, Action = action };
        db.Add(run); await db.SaveChangesAsync(); return run;
    }

    private static async Task<StoryWorld> World(StoryDb db, Guid id)
    {
        var branch = await db.Branches.AsNoTracking().SingleAsync(x => x.Id == id);
        return StoryWorld.From(await db.Checkpoints.AsNoTracking().SingleAsync(x => x.Id == branch.HeadCheckpointId));
    }

    private static bool ActorIs(AgentRequest request, string id) => JsonSerializer.SerializeToElement(request.Context, Json.Options).TryGetProperty("actorId", out var actor) && actor.GetString() == id;

    private sealed class Agent(Func<AgentRequest, object>? transform = null) : IAgentProvider
    {
        public List<AgentRequest> Requests { get; } = [];
        public Task<T> Generate<T>(AgentRequest request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Requests.Add(request);
            return Task.FromResult(Json.Read<T>(Json.Write(transform?.Invoke(request) ?? request.Example)));
        }
    }

    [Fact]
    public async Task CharactersReactInOrderWithoutReadingOtherSecrets()
    {
        await using var db = Db(); var branch = await CampaignService.Create(db, new("Perception"), default);
        var actor = new Agent(); var director = new Agent(); var run = await Start(db, branch);
        await StoryEngine.Execute(db, run, Config, default, actor, director);
        Assert.Equal("completed", run.Status);
        Assert.Equal(3, actor.Requests.Count);
        var lyra = Json.Write(actor.Requests[0].Context);
        var clerk = Json.Write(actor.Requests[1].Context);
        Assert.Contains("expelled mentor", lyra);
        Assert.DoesNotContain("expelled mentor", clerk);
        Assert.DoesNotContain("Keeper Vale", clerk);
        Assert.Contains("Help a fellow applicant", clerk);
    }

    [Fact]
    public async Task LaterCharactersReadPublishedEventsAndNarrationExcludesSecrets()
    {
        await using var db = Db(); var branch = await CampaignService.Create(db, new("Room"), default);
        var actor = new Agent(); var director = new Agent(); var run = await Start(db, branch);
        await StoryEngine.Execute(db, run, Config, default, actor, director);
        Assert.Contains("Help a fellow applicant", Json.Write(actor.Requests[1].Context));
        var narration = director.Requests.Single(x => x.Task == "narrate");
        Assert.DoesNotContain("expelled mentor", Json.Write(narration.Context));
        Assert.DoesNotContain("silver fox", Json.Write(narration.Context));
        Assert.Contains("Crowns", Json.Write(director.Requests.First().Context));
        Assert.Contains("Sword practice", Json.Write(director.Requests.First().Context));
    }

    [Fact]
    public void PreviouslySeenEntitiesDoNotLeakPrivateMovement()
    {
        var w = StoryWorld.Crownspire();
        var i = w.Entities.FindIndex(x => x.Id == "lyra");
        w.Entities[i] = w.Entities[i] with { LocationId = "courtyard", Description = "Secretly met the expelled mentor" };
        var context = Json.Write(StoryEngine.CharacterContext(w, "clerk", []));
        Assert.DoesNotContain("courtyard", context);
        Assert.DoesNotContain("expelled mentor", context);
        Assert.Contains("whereabouts unknown", context);
    }

    [Fact]
    public void PrivateObjectsInTheSameRoomRequireDiscovery()
    {
        var world = StoryWorld.Crownspire();
        world.Entities.Add(new("letter", "Secret letter", "object", "hall", "Evidence about the expelled mentor", ["lyra"]));
        Assert.DoesNotContain("Secret letter", Json.Write(StoryEngine.CharacterContext(world, "clerk", [])));
        Assert.Contains("Secret letter", Json.Write(StoryEngine.CharacterContext(world, "lyra", [])));
    }

    [Fact]
    public async Task LoopAndNpcLimitsAreEnforced()
    {
        await using var db = Db(); var branch = await CampaignService.Create(db, new("Bounds"), default, new(MaxBeats: 2, MaxNpcs: 2));
        var actor = new Agent(); var director = new Agent(r => r.Example is Resolution x ? x with { NeedsPlayerDecision = false } : r.Example);
        await StoryEngine.Execute(db, await Start(db, branch), Config, default, actor, director);
        Assert.Equal(4, actor.Requests.Count);
        var w = await World(db, branch.Id);
        Assert.Equal(2, w.Events.Max(x => x.Beat));
        Assert.DoesNotContain(w.Events.Where(x => x.Beat > 0), x => x.ActorId == "guard");
    }

    [Fact]
    public async Task TimeAdvanceUpdatesOffscreenOnceEvenAfterPlayerHandoff()
    {
        await using var db = Db(); var branch = await CampaignService.Create(db, new("Time"), default);
        var actor = new Agent(); var director = new Agent(r => r.Example is Resolution x ? x with { NeedsPlayerDecision = true } : r.Example);
        await StoryEngine.Execute(db, await Start(db, branch, "Wait quietly"), Config, default, actor, director);
        Assert.Equal(2, actor.Requests.Count); // keeper + fox; no scene reactions after player resolution handoff
        Assert.All(actor.Requests, x => Assert.DoesNotContain("Wait quietly", Json.Write(x.Context)));
        var w = await World(db, branch.Id);
        Assert.Equal(10, w.TimeMinutes);
        Assert.Single(w.Events, x => x.Beat > 0 && x.ActorId == "keeper");
        Assert.All(w.Events.Where(x => x.Beat > 0), x => Assert.DoesNotContain("player", x.PerceivedBy));
    }

    [Theory]
    [InlineData(1, 5, 10, false)]
    [InlineData(1, 15, 10, true)]
    [InlineData(20, -15, 10, false)]
    [InlineData(16, 3, 18, true)]
    public void DiceTotalsDecideOutcomeIncludingNaturalOneAndTwenty(int roll, int bonus, int difficulty, bool success)
    {
        var check = StoryEngine.Roll("check", "player", new("Attempt", true, difficulty, [new("Situation", bonus)], "Established circumstance"), () => roll);
        Assert.Equal(roll + bonus, check.Total); Assert.Equal(success, check.Success);
    }

    [Fact]
    public async Task RetryPreservesCompletedCallsAndRollAcrossNewDatabaseContext()
    {
        Guid runId, branchId;
        var failed = false;
        var director = new Agent(r => {
            if (r.Task == "resolve" && !failed) { failed = true; throw new InvalidOperationException("Transient provider failure"); }
            return r.Example;
        });
        DiceCheck before;
        await using (var db = Db())
        {
            var branch = await CampaignService.Create(db, new("Retry"), default); branchId = branch.Id;
            var run = await Start(db, branch, "Force the sealed door"); runId = run.Id;
            await Assert.ThrowsAsync<InvalidOperationException>(() => StoryEngine.Execute(db, run, Config, default, new Agent(), director));
            var saved = await db.GenerationRuns.SingleAsync(x => x.Id == runId);
            Assert.Equal("paused", saved.Status);
            before = Assert.Single(Json.Read<TurnDraft>(saved.DraftJson).Rolls.Values);
            Assert.Single(await db.Checkpoints.ToListAsync()); Assert.Single(await db.Messages.ToListAsync());
        }
        await using (var db = Db())
        {
            var run = await db.GenerationRuns.SingleAsync(x => x.Id == runId);
            await StoryEngine.Execute(db, run, Config, default, new Agent(), director);
            var after = Assert.Single((await World(db, branchId)).Checks);
            Assert.Equal(Json.Write(before), Json.Write(after));
            Assert.Single(director.Requests, x => x.Task == "plan");
            Assert.Equal(3, await db.Messages.CountAsync());
        }
    }

    [Fact]
    public async Task InvalidFailedCheckEffectsAreRegeneratedOnRetry()
    {
        await using var db = Db(); var branch = await CampaignService.Create(db, new("Bad outcome"), default);
        var bad = true;
        var director = new Agent(r => {
            if (r.Example is TurnPlan p) return p with { PlayerAttempt = p.PlayerAttempt with { Uncertain = true, Difficulty = 40 } };
            if (r.Task == "resolve" && r.Example is Resolution x && bad) return x with { MoveToId = "courtyard" };
            return r.Example;
        });
        var run = await Start(db, branch);
        await Assert.ThrowsAsync<InvalidOperationException>(() => StoryEngine.Execute(db, run, Config, default, new Agent(), director));
        var saved = await db.GenerationRuns.SingleAsync(x => x.Id == run.Id);
        Assert.DoesNotContain(Json.Read<TurnDraft>(saved.DraftJson).Steps, x => x.Key == "player-result");
        bad = false;
        await StoryEngine.Execute(db, saved, Config, default, new Agent(), director);
        Assert.Equal("hall", (await World(db, branch.Id)).PlayerLocationId);
        Assert.Single(director.Requests, x => x.Task == "plan");
        Assert.Equal(2, director.Requests.Count(x => x.Task == "resolve" && ActorIs(x, "player")));
    }

    [Fact]
    public async Task RetryAfterMovementRetainsHandoffAndDoesNotDuplicateEvents()
    {
        await using var db = Db(); var branch = await CampaignService.Create(db, new("Handoff retry"), default);
        var actor = new Agent(); var failNarration = true;
        var director = new Agent(r => {
            if (r.Example is Resolution x && ActorIs(r, "lyra")) return x with { MoveToId = "courtyard", NeedsPlayerDecision = true };
            if (r.Task == "narrate" && failNarration) { failNarration = false; throw new InvalidOperationException("Lost connection"); }
            return r.Example;
        });
        var run = await Start(db, branch);
        await Assert.ThrowsAsync<InvalidOperationException>(() => StoryEngine.Execute(db, run, Config, default, actor, director));
        var saved = await db.GenerationRuns.SingleAsync(x => x.Id == run.Id);
        await StoryEngine.Execute(db, saved, Config, default, actor, director);
        Assert.Single(actor.Requests);
        var w = await World(db, branch.Id);
        Assert.Single(w.Events, x => x.ActorId == "lyra" && x.Beat > 0);
        Assert.Equal("courtyard", w.Entities.Single(x => x.Id == "lyra").LocationId);
    }

    [Fact]
    public async Task CancellationLeavesWorldAndMessagesUnchanged()
    {
        await using var db = Db(); var branch = await CampaignService.Create(db, new("Cancel"), default);
        var head = branch.HeadCheckpointId;
        using var cancellation = new CancellationTokenSource();
        var director = new Agent(r => { if (r.Task == "resolve") cancellation.Cancel(); return r.Example; });
        var run = await Start(db, branch);
        await StoryEngine.Execute(db, run, Config, cancellation.Token, new Agent(), director);
        Assert.Equal("cancelled", (await db.GenerationRuns.SingleAsync()).Status);
        Assert.Equal(head, (await db.Branches.SingleAsync()).HeadCheckpointId);
        Assert.Single(await db.Checkpoints.ToListAsync()); Assert.Single(await db.Messages.ToListAsync());
    }

    [Fact]
    public async Task StartupRecoveryPausesRunningDraftWithoutChangingItsSavedRoll()
    {
        Guid runId;
        string draftJson;
        await using (var db = Db())
        {
            var branch = await CampaignService.Create(db, new("Restart"), default);
            var run = await Start(db, branch); runId = run.Id;
            var world = await World(db, branch.Id);
            var draft = new TurnDraft { World = world, State = StoryState.Synthetic };
            draft.Rolls["saved-check"] = StoryEngine.Roll("saved-check", "player", new("A prior attempt", true, 10, [], "Uncertain"), () => 12);
            run.DraftJson = draftJson = Json.Write(draft); await db.SaveChangesAsync();
        }
        await using (var db = Db())
        {
            await StoryEngine.Recover(db);
            var recovered = await db.GenerationRuns.SingleAsync(x => x.Id == runId);
            Assert.Equal("paused", recovered.Status); Assert.Equal(draftJson, recovered.DraftJson);
            Assert.Single(await db.Messages.ToListAsync()); Assert.Single(await db.Checkpoints.ToListAsync());
        }
    }

    [Fact]
    public async Task StaleDraftCannotOverwriteNewCheckpoint()
    {
        await using var db = Db(); var branch = await CampaignService.Create(db, new("Stale"), default);
        var run = await Start(db, branch);
        var other = await Start(db, branch);
        await CampaignService.CommitTurn(db, other, "Other continuation", default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => StoryEngine.Execute(db, run, Config, default, new Agent(), new Agent()));
        Assert.Equal(3, await db.Messages.CountAsync()); Assert.Equal(2, await db.Checkpoints.CountAsync());
    }

    [Fact]
    public async Task GraphAndMemoriesForkOnlyThroughSelectedCheckpoint()
    {
        await using var db = Db(); var branch = await CampaignService.Create(db, new("Timeline"), default);
        var initial = branch.HeadCheckpointId;
        var director = new Agent(r => r.Example is Resolution x && ActorIs(r, "lyra")
            ? x with { Connections = [new("lyra", "clerk", "trusts")] } : r.Example);
        await StoryEngine.Execute(db, await Start(db, branch), Config, default, new Agent(), director);
        var latest = await World(db, branch.Id);
        var fork = await CampaignService.Fork(db, branch.Id, new(initial, "Before"), default);
        var past = await World(db, fork.Id);
        Assert.DoesNotContain(past.Connections, x => x.Kind == "trusts");
        var relation = Assert.Single(latest.Connections, x => x.Kind == "trusts");
        Assert.Contains(latest.Events, x => x.Id == relation.EvidenceEventId);
        Assert.Equal(3, past.Events.Count); Assert.True(latest.Memories.Count > past.Memories.Count);
        Assert.DoesNotContain(past.Memories, m => m.EvidenceEventIds.Any(id => latest.Events.Any(e => e.Id == id && e.Beat > 0)));
    }

    [Fact]
    public async Task AuthorMemoryCorrectionIsVersionedAndDoesNotRewriteCanon()
    {
        await using var db = Db(); var branch = await CampaignService.Create(db, new("Memory"), default);
        var head = branch.HeadCheckpointId;
        await EngineApi.Edit(db, branch.Id, head, "Correction", w => {
            var i = w.Memories.FindIndex(x => x.Id == "guard-belief");
            w.Memories[i] = w.Memories[i] with { Text = "The applicant seems trustworthy", Pinned = true };
        }, default);
        var w = await World(db, branch.Id);
        Assert.Contains("trustworthy", Json.Write(StoryEngine.CharacterContext(w, "guard", [])));
        Assert.DoesNotContain("trustworthy", Json.Write(w.Events));
        var old = StoryWorld.From(await db.Checkpoints.SingleAsync(x => x.Id == head));
        Assert.Contains("sealed wing", old.Memories.Single(x => x.Id == "guard-belief").Text);
        await EngineApi.Edit(db, branch.Id, branch.HeadCheckpointId, "Remove", x => x.Memories.RemoveAll(m => m.Id == "guard-belief"), default);
        Assert.DoesNotContain(StoryEngine.RetrieveMemories(await World(db, branch.Id), "guard", "applicant"), x => x.Id == "guard-belief");
    }

    [Fact]
    public void RetrievalFiltersOwnershipBeforeRankingAndIncludesPinnedEntries()
    {
        var w = StoryWorld.Crownspire();
        w.Memories.Add(new("pin", "clerk", "long", "fact", "Remember an old promise", true, []));
        var memories = StoryEngine.RetrieveMemories(w, "clerk", "expelled mentor");
        Assert.All(memories, m => Assert.Equal("clerk", m.OwnerId));
        Assert.Contains(memories, m => m.Id == "pin");
        Assert.DoesNotContain(memories, m => m.Text.Contains("expelled mentor"));
    }

    [Fact]
    public async Task MalformedDirectorSelectionDoesNotPublishAnything()
    {
        await using var db = Db(); var branch = await CampaignService.Create(db, new("Invalid"), default);
        var director = new Agent(r => r.Example is TurnPlan p ? p with { ActorIds = ["imaginary"] } : r.Example);
        var run = await Start(db, branch);
        await Assert.ThrowsAsync<InvalidOperationException>(() => StoryEngine.Execute(db, run, Config, default, new Agent(), director));
        Assert.Single(await db.Messages.ToListAsync()); Assert.Single(await db.Checkpoints.ToListAsync());
        Assert.Empty(Json.Read<TurnDraft>((await db.GenerationRuns.SingleAsync()).DraftJson).Steps);
    }

    [Fact]
    public void AudienceRejectsOutOfSceneRecipientsAndPreservesPrivateEvents()
    {
        var w = StoryWorld.Crownspire();
        Assert.Equal(new[] { "lyra" }, StoryEngine.Audience(w, "lyra", "private", []));
        Assert.Throws<InvalidOperationException>(() => StoryEngine.Audience(w, "lyra", "direct", ["keeper"]));
        Assert.DoesNotContain("keeper", StoryEngine.Audience(w, "player", "scene", []));
    }
}
