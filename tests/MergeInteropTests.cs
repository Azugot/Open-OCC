using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Story;
using Xunit;

namespace StoryTests;

public sealed class MergeInteropTests : IAsyncLifetime
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private StoryDb Db() => new(new DbContextOptionsBuilder<StoryDb>().UseSqlite(connection).Options);
    public async Task InitializeAsync()
    {
        await connection.OpenAsync();
        await using var db = Db();
        await db.Database.EnsureCreatedAsync();
    }
    public Task DisposeAsync() => connection.DisposeAsync().AsTask();

    private static async Task<Checkpoint> Head(StoryDb db, Guid branchId)
    {
        var branch = await db.Branches.AsNoTracking().SingleAsync(x => x.Id == branchId);
        return await db.Checkpoints.AsNoTracking().SingleAsync(x => x.Id == branch.HeadCheckpointId);
    }

    private static async Task<Branch> Played(StoryDb db)
    {
        var branch = await CampaignService.Create(db, new("Merged campaign"), default);
        var run = new GenerationRun { BranchId = branch.Id, ExpectedCheckpointId = branch.HeadCheckpointId, Action = "Ask Lyra about registration" };
        db.GenerationRuns.Add(run); await db.SaveChangesAsync();
        await StoryEngine.Execute(db, run, new ConfigurationBuilder().Build(), default);
        return branch;
    }

    [Fact]
    public async Task MechanicsAndCanonCorrectionsRetainAutonomousGraphAndMemories()
    {
        Guid branchId;
        string worldJson;
        await using (var db = Db())
        {
            var branch = await Played(db); branchId = branch.Id;
            var before = await Head(db, branch.Id); worldJson = before.WorldJson;
            var world = StoryWorld.From(before);
            Assert.Contains(world.Memories, x => x.OwnerId == "lyra" && x.Id == "lyra-secret");
            Assert.Contains(world.Events, x => x.ActorId == "player" && x.Id != "opening");
            Assert.Contains(world.Connections, x => x.SourceId == "lyra" && x.TargetId == "sword");
            var mechanics = await MechanicsService.Apply(db, branch.Id, new(before.Id, "inventory", "Crowns", -2, "Pay registration fee"), default);
            Assert.Equal(worldJson, mechanics.WorldJson);
            Assert.Equal(18, Json.Read<StoryState>(mechanics.StateJson).Inventory["Crowns"]);
            var fact = new Fact { BranchId = branch.Id, Text = "The registration fee is one Crown.", Visibility = "public", ReviewStatus = "accepted" };
            db.Facts.Add(fact); await db.SaveChangesAsync();
            await CampaignService.CorrectFact(db, branch.Id, fact.Id, new(mechanics.Id, "The registration fee is two Crowns.", "Correct the posted fee"), default);
            Assert.Equal(worldJson, (await Head(db, branch.Id)).WorldJson);
            Assert.Single(await db.FactCorrections.Where(x => x.BranchId == branch.Id).ToListAsync());
        }
        await using (var db = Db())
        {
            var persisted = await Head(db, branchId);
            Assert.Equal(worldJson, persisted.WorldJson);
            Assert.Equal(18, Json.Read<StoryState>(persisted.StateJson).Inventory["Crowns"]);
            Assert.Contains(await db.Facts.Where(x => x.BranchId == branchId).ToListAsync(), x => x.Text == "The registration fee is two Crowns.");
        }
    }

    [Fact]
    public async Task LegacyContinuityTurnUpdatesSceneWithoutReplacingEngineHistory()
    {
        await using var db = Db();
        var branch = await Played(db);
        var current = await Head(db, branch.Id);
        var previous = StoryWorld.From(current);
        var state = Json.Read<StoryState>(current.StateJson) with { Location = "Academy archive", Participants = ["Lyra Fen", "Mira Dorne"] };
        var run = new GenerationRun { BranchId = branch.Id, ExpectedCheckpointId = current.Id, Action = "Walk to the archive", Provider = "openai", Model = "narrator" };
        db.GenerationRuns.Add(run); await db.SaveChangesAsync();
        var transition = new StateTransition(state,
            [new("The archive closes at sunset.", "fact", "public", [], 1)],
            [new("Find the archive register", "Mira can help locate it.", "goal", "active", 4)]);
        await CampaignService.CommitTurn(db, run, "Lyra follows you to the archive, where Mira greets you.", state, transition, default);
        var committed = await Head(db, branch.Id);
        var next = StoryWorld.From(committed);
        Assert.Equal("Academy archive", next.Entities.Single(x => x.Id == next.PlayerLocationId).Name);
        Assert.Equal(next.PlayerLocationId, next.Entities.Single(x => x.Id == "player").LocationId);
        Assert.Equal(next.PlayerLocationId, next.Entities.Single(x => x.Id == "lyra").LocationId);
        var mira = next.Entities.Single(x => x.Name == "Mira Dorne");
        Assert.Equal(next.PlayerLocationId, mira.LocationId);
        Assert.Contains(next.Characters, x => x.EntityId == mira.Id);
        foreach (var memory in previous.Memories) Assert.Contains(next.Memories, x => Json.Write(x) == Json.Write(memory));
        foreach (var edge in previous.Connections) Assert.Contains(next.Connections, x => x == edge);
        foreach (var occurrence in previous.Events) Assert.Contains(next.Events, x => Json.Write(x) == Json.Write(occurrence));
        var narrated = Assert.Single(next.Events, x => x.Id == "narration-" + run.Id.ToString("N"));
        Assert.Contains("player", narrated.PerceivedBy); Assert.Contains("lyra", narrated.PerceivedBy); Assert.Contains(mira.Id, narrated.PerceivedBy);
        Assert.DoesNotContain("keeper", narrated.PerceivedBy);
        Assert.Contains(await db.Facts.Where(x => x.BranchId == branch.Id).ToListAsync(), x => x.Text == "The archive closes at sunset.");
        Assert.Contains(await db.NarrativeThreads.Where(x => x.BranchId == branch.Id).ToListAsync(), x => x.Title == "Find the archive register");
        Assert.Equal("completed", run.Status);
    }

    [Fact]
    public async Task PortableCampaignPreservesWorldAndPausedRollDraftWithRemappedCheckpoint()
    {
        await using var db = Db();
        var branch = await Played(db);
        var current = await Head(db, branch.Id);
        var world = StoryWorld.From(current);
        var attempt = new Attempt("Convince the guard", true, 15, [new("Genuine invitation", 3)], "The guard is suspicious.");
        var check = StoryEngine.Roll("player-check", "player", attempt, () => 12);
        var draft = new TurnDraft { World = Json.Read<StoryWorld>(current.WorldJson), State = Json.Read<StoryState>(current.StateJson) };
        draft.Rolls.Add("player-check", check); draft.World.Checks.Add(check);
        draft.Steps.Add(new("plan", "plan", Json.Write(new TurnPlan(["guard"], 0, attempt)), DateTime.UtcNow));
        var paused = new GenerationRun { BranchId = branch.Id, ExpectedCheckpointId = current.Id, Action = "Convince the guard", Status = "paused", DraftJson = Json.Write(draft), Error = "Provider unavailable" };
        db.GenerationRuns.Add(paused); await db.SaveChangesAsync();
        var export = await PortabilityService.Export(db, branch.CampaignId, default);
        // Exercise the exported JSON boundary, not only in-memory object copying.
        var portable = Json.Read<PortableExport>(Json.Write(export));
        var imported = await PortabilityService.Import(db, portable, "Restored campaign", default);
        Assert.NotEqual(branch.Id, imported.Id); Assert.NotEqual(current.Id, imported.HeadCheckpointId);
        var importedHead = await Head(db, imported.Id);
        Assert.Equal(current.WorldJson, importedHead.WorldJson);
        var importedRun = await db.GenerationRuns.SingleAsync(x => x.BranchId == imported.Id && x.Status == "paused");
        Assert.NotEqual(paused.Id, importedRun.Id);
        Assert.Equal(imported.HeadCheckpointId, importedRun.ExpectedCheckpointId);
        Assert.Equal(paused.DraftJson, importedRun.DraftJson);
        var resumed = Json.Read<TurnDraft>(importedRun.DraftJson);
        Assert.Equal(12, resumed.Rolls["player-check"].Roll);
        Assert.Equal(15, resumed.Rolls["player-check"].Total);
        Assert.True(resumed.Rolls["player-check"].Success);
        Assert.Equal(Json.Write(check), Json.Write(resumed.World.Checks.Last()));
        Assert.Equal(Json.Write(world.Memories), Json.Write(resumed.World.Memories));
        Assert.Single(resumed.Steps, x => x.Key == "plan");
    }

    [Fact]
    public void ImportedFactsRetainKnowledgeOwnershipAndEpistemicLabels()
    {
        static Fact Fact(string text, string kind = "fact", string visibility = "public", params string[] owners) => new()
        { Text = text, Kind = kind, Visibility = visibility, KnownByJson = Json.Write(owners), Confidence = kind == "rumor" ? 0.4 : 1, ReviewStatus = "accepted" };
        var facts = new[]
        {
            Fact("Registration ends at noon."),
            Fact("A silver key opens the vault.", "secret", "narrator", "Lyra Fen"),
            Fact("Sera trusts every applicant.", "belief", "public", "Lyra Fen"),
            Fact("The academy is haunted.", "rumor"),
            Fact("The secret entrance faces east.", "secret"),
            Fact("All guards are friendly.", "belief"),
            Fact("A private clerk conversation.", "fact", "public", "Clerk Orin"),
            Fact("Director-only chronology.", "fact", "narrator")
        };
        var lyra = StoryEngine.PerceivedFacts(facts, "lyra fen");
        var sera = StoryEngine.PerceivedFacts(facts, "Guard Sera");
        Assert.Contains(lyra, x => x.EndsWith("A silver key opens the vault."));
        Assert.Contains(lyra, x => x.StartsWith("[belief;") && x.EndsWith("Sera trusts every applicant."));
        Assert.DoesNotContain(sera, x => x.Contains("silver key") || x.Contains("Sera trusts"));
        Assert.All(new[] { lyra, sera }, perceived =>
        {
            Assert.Contains(perceived, x => x.StartsWith("[rumor;") && x.EndsWith("The academy is haunted."));
            Assert.Contains(perceived, x => x.EndsWith("Registration ends at noon."));
            Assert.DoesNotContain(perceived, x => x.Contains("secret entrance") || x.Contains("All guards") || x.Contains("private clerk") || x.Contains("Director-only"));
        });
        var world = StoryWorld.Crownspire();
        var context = JsonSerializer.SerializeToElement(StoryEngine.CharacterContext(world, "guard", sera), Json.Options);
        var common = context.GetProperty("commonFacts").EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.Contains(common, x => x!.StartsWith("[rumor;"));
        Assert.DoesNotContain(common, x => x!.Contains("silver key"));
    }
}
