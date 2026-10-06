using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Story;
using Xunit;

namespace StoryTests;

public class CharacterTests : IAsyncLifetime
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private StoryDb Db() => new(new DbContextOptionsBuilder<StoryDb>().UseSqlite(connection).Options);
    public async Task InitializeAsync() { await connection.OpenAsync(); await using var db = Db(); await db.Database.EnsureCreatedAsync(); }
    public Task DisposeAsync() => connection.DisposeAsync().AsTask();
    private static async Task<StoryWorld> World(StoryDb db, Branch b) => StoryWorld.From(await db.Checkpoints.SingleAsync(c => c.Id == b.HeadCheckpointId));
    private static JsonElement JsonOf(object o) => JsonSerializer.SerializeToElement(o, Json.Options);

    [Fact]
    public async Task ProfileRenameKeepsStableIdentityKnowledgeAndHistoricalSnapshot()
    {
        await using var db = Db(); var b = await CampaignService.Create(db, new("Profiles", false), default);
        await CharacterService.SaveProfile(db, b.Id, null, new(b.HeadCheckpointId, "Lyra", "A mage", new("", Aliases: ["L"]), LocationId: "hall"), default);
        var w = await World(db, b); var entity = w.Entities.Single(e => e.Name == "Lyra"); var p = w.CharacterProfiles.Single(p => p.EntityId == entity.Id);
        Assert.NotNull(p.CampaignCharacterId);
        db.Knowledge.Add(new KnowledgeRecord { BranchId = b.Id, CharacterId = p.CampaignCharacterId!.Value, Subject = "The key is hidden", EffectiveSequence = 1 }); await db.SaveChangesAsync();
        var prior = b.HeadCheckpointId;
        await CharacterService.SaveProfile(db, b.Id, entity.Id, new(prior, "Lyra Vale", "A mage", p, Goal: "Find the key", LocationId: "hall"), default);
        w = await World(db, b); Assert.Equal("Lyra Vale", w.Entities.Single(e => e.Id == entity.Id).Name);
        Assert.Contains("Lyra", w.CharacterProfiles.Single(p => p.EntityId == entity.Id).Aliases!);
        Assert.Equal(p.CampaignCharacterId, w.CharacterProfiles.Single(p => p.EntityId == entity.Id).CampaignCharacterId);
        Assert.Single(await db.Knowledge.Where(k => k.CharacterId == p.CampaignCharacterId).ToArrayAsync());
        Assert.Equal("Lyra", StoryWorld.From(await db.Checkpoints.SingleAsync(c => c.Id == prior)).Entities.Single(e => e.Id == entity.Id).Name);
    }
    [Fact]
    public async Task CapabilitiesKeepLegacySkillsInactiveValuesAndPrivateAudience()
    {
        await using var db = Db(); var b = await CampaignService.Create(db, new("Capabilities", true), default);
        var original = Json.Read<StoryState>((await db.Checkpoints.SingleAsync(c => c.Id == b.HeadCheckpointId)).StateJson);
        var w = await World(db, b); CharacterService.Normalize(w, original); Assert.Equal(original.Skills.Count, w.Capabilities.Count(c => c.OwnerId == "player"));
        await CharacterService.SaveCapability(db, b.Id, "player", new(b.HeadCheckpointId, new("magic", "player", "proficiency", "Magic Talent", Rank: "F · 2/5")), default);
        Assert.Equal("F · 2/5", Json.Read<StoryState>((await db.Checkpoints.SingleAsync(c => c.Id == b.HeadCheckpointId)).StateJson).Skills["Magic Talent"]);
        await CharacterService.SaveCapability(db, b.Id, "player", new(b.HeadCheckpointId, new("magic", "player", "proficiency", "Magic Talent", Rank: "F · 2/5", Active: false)), default);
        var state = Json.Read<StoryState>((await db.Checkpoints.SingleAsync(c => c.Id == b.HeadCheckpointId)).StateJson);
        w = await World(db, b); CharacterService.Normalize(w, state); Assert.False(w.Capabilities.Single(c => c.Id == "magic").Active); Assert.DoesNotContain("Magic Talent", state.Skills.Keys);
        var npc = w.Characters.First().EntityId;
        await CharacterService.SaveCapability(db, b.Id, npc, new(b.HeadCheckpointId, new("secret", npc, "power", "Shadow Step", Limitations: "Only in darkness", KnownTo: [npc, "director"])), default);
        w = await World(db, b);
        Assert.DoesNotContain(CharacterService.CapabilityContext(w, "player"), c => Json.Write(c).Contains("Shadow Step"));
        Assert.DoesNotContain(w.Memories, m => m.OwnerId == "player" && m.Text.Contains("Shadow Step", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(w.Events, e => e.PerceivedBy.Contains("player") && e.Text.Contains("step through shadows"));
        Assert.Contains(CharacterService.CapabilityContext(w, npc), c => Json.Write(c).Contains("Only in darkness"));
        Assert.Contains(CharacterService.CapabilityContext(w, "director"), c => Json.Write(c).Contains("Shadow Step"));
    }
    [Fact]
    public async Task RelationshipChangesRetainDirectionScoresTypesAndForkHistory()
    {
        await using var db = Db(); var b = await CampaignService.Create(db, new("Relationships", true), default);
        var npc = (await World(db, b)).Characters.First().EntityId;
        await CharacterService.SaveRelationship(db, b.Id, new(b.HeadCheckpointId, new("", "", "player", npc, "trust", "Cautious", 10, "First meeting", "", 0)), default);
        var first = (await World(db, b)).RelationshipHistory.Last(); var forkPoint = b.HeadCheckpointId;
        await CharacterService.SaveRelationship(db, b.Id, new(b.HeadCheckpointId, first with { Label = "Trusted", Score = 30, Reason = "Kept a promise" }), default);
        var last = (await World(db, b)).RelationshipHistory.Last(); Assert.Equal("Cautious", last.PreviousLabel); Assert.Equal(10, last.PreviousScore);
        await CharacterService.SaveRelationship(db, b.Id, new(b.HeadCheckpointId, last with { Active = false, Reason = "Parted ways" }), default);
        Assert.False((await World(db, b)).RelationshipHistory.Last().Active);
        var fork = await CampaignService.Fork(db, b.Id, new(forkPoint, "Earlier"), default);
        Assert.Single((await World(db, fork)).RelationshipHistory, h => h.RelationshipId == first.RelationshipId);
        Assert.True((await World(db, fork)).RelationshipHistory.Last().Active);
        await Assert.ThrowsAsync<InvalidOperationException>(() => CharacterService.SaveRelationship(db, b.Id, new(b.HeadCheckpointId, last with { FromId = npc, ToId = "player" }), default));
    }
    [Fact]
    public async Task StaleCheckpointsAndPausedDraftsBlockEdits()
    {
        await using var db = Db(); var b = await CampaignService.Create(db, new("Guards", true), default); var old = b.HeadCheckpointId;
        var cap = new CharacterCapability("one", "player", "ability", "Observe");
        await CharacterService.SaveCapability(db, b.Id, "player", new(old, cap), default);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => CharacterService.SaveCapability(db, b.Id, "player", new(old, cap with { Description = "Changed" }), default));
        db.GenerationRuns.Add(new GenerationRun { BranchId = b.Id, ExpectedCheckpointId = b.HeadCheckpointId, Status = "paused" }); await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => CharacterService.SaveCapability(db, b.Id, "player", new(b.HeadCheckpointId, cap), default));
    }
    [Fact]
    public async Task PortableRoundTripRemapsLinksAndRejectsDanglingOwnersAtomically()
    {
        await using var db = Db(); var b = await CampaignService.Create(db, new("Portable profiles", false), default);
        await CharacterService.SaveProfile(db, b.Id, null, new(b.HeadCheckpointId, "Mara", "Navigator", new("")), default);
        var w = await World(db, b); var entity = w.Entities.Single(e => e.Name == "Mara");
        await CharacterService.SaveCapability(db, b.Id, entity.Id, new(b.HeadCheckpointId, new("sailing", entity.Id, "proficiency", "Sailing", Rank: "Expert")), default);
        var export = await PortabilityService.Export(db, b.CampaignId, default); Assert.Equal(5, export.Version);
        var restored = await PortabilityService.Import(db, export, "Restored", default); var copied = await World(db, restored);
        Assert.Equal("Expert", copied.Capabilities.Single(c => c.Id == "sailing").Rank);
        Assert.NotEqual(w.CharacterProfiles.Single(p => p.EntityId == entity.Id).CampaignCharacterId, copied.CharacterProfiles.Single(p => p.EntityId == entity.Id).CampaignCharacterId);
        var bad = Json.Read<PortableExport>(Json.Write(export)); var cp = bad.Checkpoints.Single(c => c.Id == b.HeadCheckpointId); var broken = StoryWorld.From(cp); broken.Capabilities[0] = broken.Capabilities[0] with { OwnerId = "missing" }; cp.WorldJson = Json.Write(broken);
        var count = await db.Campaigns.CountAsync(); await Assert.ThrowsAsync<InvalidOperationException>(() => PortabilityService.Import(db, bad, "Bad", default)); Assert.Equal(count, await db.Campaigns.CountAsync());
    }
    [Fact]
    public async Task PlayerRosterHidesPrivateStateCapabilitiesAndOffscreenLocation()
    {
        await using var db = Db(); var b = await CampaignService.Create(db, new("Privacy", true), default); var w = await World(db, b); var npc = w.Characters.First().EntityId;
        await CharacterService.SaveCapability(db, b.Id, npc, new(b.HeadCheckpointId, new("hidden", npc, "ability", "Secret art", KnownTo: [npc, "director"])), default);
        var player = JsonOf(await CharacterService.Read(db, b.Id, null, false, default)); Assert.DoesNotContain("Secret art", player.GetRawText());
        Assert.All(player.GetProperty("characters").EnumerateArray(), c => Assert.Equal(JsonValueKind.Null, c.GetProperty("state").ValueKind));
        var author = JsonOf(await CharacterService.Read(db, b.Id, null, true, default)); Assert.Contains("Secret art", author.GetRawText());
    }
    [Fact]
    public async Task ReviewedNpcCapabilityResolvesOwnerAndRejectsUnknownIdentity()
    {
        var state = new StoryState("Harbor", "Noon", "Depart", "Healthy", new(), new(), ["Lyra"]);
        var resume = new ResumeProposal(state, [0], "Prepare to depart", false, [], "Mara");
        var job = new ImportJob(); var blocks = new Dictionary<int, ImportSegment> { [0] = new() { JobId = job.Id, Ordinal = 0, Text = "Mara and Lyra are at Harbor. Lyra can step through shadows only in darkness.", Start = 0, Length = 96 } };
        var skill = new ImportClaim("skill:shadow", "skill", "Lyra can step through shadows only in darkness.", "Shadow Step", "", "Trained", 0, "fact", "narrator", ["Lyra"], 1, [0], "current", [], Owner: "Lyra", CapabilityCategory: "power");
        var w = ImportedWorldCompiler.Compile(new(), StoryState.Empty, job, resume, [skill], blocks);
        var owner = w.Entities.Single(e => e.Name == "Lyra").Id;
        Assert.Equal(owner, Assert.Single(w.Capabilities).OwnerId); Assert.Equal("power", w.Capabilities.Single().Category); Assert.Empty(state.Skills);
        Assert.DoesNotContain(w.Memories, m => m.OwnerId == "player" && m.Text.Contains("step through shadows"));
        Assert.DoesNotContain(w.Events, e => e.PerceivedBy.Contains("player") && e.Text.Contains("step through shadows"));
        Assert.DoesNotContain(CharacterService.CapabilityContext(w, "player"), c => Json.Write(c).Contains("Shadow Step"));
        Assert.Throws<InvalidOperationException>(() => ImportedWorldCompiler.Compile(new(), StoryState.Empty, job, resume, [skill with { Owner = "Unknown mage" }], blocks));
    }
    [Fact]
    public async Task NarrationReceivesCapabilitiesAndGenerationCannotAdvanceRanks()
    {
        await using var db = Db(); var b = await CampaignService.Create(db, new("Narrator skills", true), default);
        await CharacterService.SaveCapability(db, b.Id, "player", new(b.HeadCheckpointId, new("nav", "player", "proficiency", "Navigation", Rank: "F · 2/5", Limitations: "Needs visible landmarks")), default);
        var state = Json.Read<StoryState>((await db.Checkpoints.SingleAsync(c => c.Id == b.HeadCheckpointId)).StateJson);
        var context = await new ContinuityService().Assemble(db, b.Id, state, "Navigate", default);
        var prompt = ContinuityService.NarrationPrompt(context, "Navigate"); Assert.Contains("Needs visible landmarks", prompt.Input);
        var proposed = state with { Skills = new Dictionary<string, string>(state.Skills) { ["Navigation"] = "Master" } };
        var run = new GenerationRun { BranchId = b.Id, ExpectedCheckpointId = b.HeadCheckpointId }; db.Add(run); await db.SaveChangesAsync();
        await CampaignService.CommitTurn(db, run, "You navigate the passage.", proposed, new(null, [], []), default);
        Assert.Equal("F · 2/5", Json.Read<StoryState>((await db.Checkpoints.SingleAsync(c => c.Id == b.HeadCheckpointId)).StateJson).Skills["Navigation"]);
    }
    [Fact]
    public void AmbiguousNamesAndAliasesNeverLinkIdentitiesAutomatically()
    {
        var identity = new Character { Name = "Mara" };
        var world = new StoryWorld { Entities = [new("one", "Mara", "character", "", ""), new("two", "Mara", "being", "", "")],
            CharacterProfiles = [new("one", Aliases: ["Navigator"])] };
        CharacterService.Normalize(world, StoryState.Empty, [identity]);
        Assert.All(world.CharacterProfiles, p => Assert.Null(p.CampaignCharacterId));
        world.Entities[1] = world.Entities[1] with { Name = "Navigator" };
        CharacterService.Normalize(world, StoryState.Empty, [identity]);
        Assert.Equal(identity.Id, world.CharacterProfiles.Single(p => p.EntityId == "one").CampaignCharacterId);
        Assert.Null(world.CharacterProfiles.Single(p => p.EntityId == "two").CampaignCharacterId);
    }
    [Fact]
    public async Task LegacyCheckpointDifferencesRecordOnlyObservedChanges()
    {
        await using var db = Db(); var b = await CampaignService.Create(db, new("Historical edges", true), default);
        var npc = (await World(db, b)).Characters.First().EntityId;
        await EngineApi.Edit(db, b.Id, b.HeadCheckpointId, "Recorded connection", w => w.Connections.Add(new("legacy-edge", "player", npc, "acquaintance", "")), default);
        var first = b.HeadCheckpointId;
        await EngineApi.Edit(db, b.Id, first, "Recorded correction", w => { var i = w.Connections.FindIndex(c => c.Id == "legacy-edge"); w.Connections[i] = w.Connections[i] with { Kind = "ally" }; }, default);
        var roster = JsonOf(await CharacterService.Read(db, b.Id, null, true, default));
        var history = roster.GetProperty("characters").EnumerateArray().Single(c => c.GetProperty("entity").GetProperty("id").GetString() == "player").GetProperty("relationships").EnumerateArray().Where(h => h.GetProperty("toId").GetString() == npc).ToArray();
        Assert.Contains(history, h => h.GetProperty("baseline").GetBoolean() && h.GetProperty("label").GetString() == "acquaintance");
        Assert.Contains(history, h => h.GetProperty("previousLabel").GetString() == "acquaintance" && h.GetProperty("label").GetString() == "ally");
        Assert.True(JsonOf(await CharacterService.Read(db, b.Id, first, true, default)).GetProperty("historical").GetBoolean());
        await CharacterService.SaveCapability(db, b.Id, "player", new(b.HeadCheckpointId, new("test", "player", "ability", "Observe")), default);
        Assert.Contains((await World(db, b)).RelationshipHistory, h => h.PreviousLabel == "acquaintance" && h.Label == "ally");
        await EngineApi.Edit(db, b.Id, b.HeadCheckpointId, "Recorded ending", world => world.Connections.RemoveAll(c => c.Id == "legacy-edge"), default);
        var ended = JsonOf(await CharacterService.Read(db, b.Id, null, true, default));
        Assert.Contains("Connection removed in this recorded checkpoint", ended.GetRawText());
    }
    [Fact]
    public async Task CharacterPromptsReceiveOwnLimitationsAndDirectedRelationships()
    {
        await using var db = Db(); var b = await CampaignService.Create(db, new("Character context", true), default);
        var npc = (await World(db, b)).Characters.First().EntityId;
        await CharacterService.SaveCapability(db, b.Id, npc, new(b.HeadCheckpointId, new("art", npc, "power", "Veil", Limitations: "Requires moonlight", KnownTo: [npc, "director"])), default);
        await CharacterService.SaveRelationship(db, b.Id, new(b.HeadCheckpointId, new("", "", npc, "player", "trust", "Wary", null, "Author correction", "", 0, KnownTo: [npc, "director"])), default);
        var w = await World(db, b); var context = Json.Write(StoryEngine.CharacterContext(w, npc, []));
        Assert.Contains("Requires moonlight", context); Assert.Contains("Wary", context);
        Assert.Empty(CharacterService.RelationshipContext(w, "player"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => CharacterService.SaveCapability(db, b.Id, npc, new(b.HeadCheckpointId, new("bad", npc, "power", null!)), default));
    }
    [Fact]
    public async Task PlayerPrivateProfileAndManualSkillEditsRemainSynchronized()
    {
        await using var db = Db(); var b = await CampaignService.Create(db, new("Player details", true), default);
        var player = (await World(db, b)).Entities.Single(e => e.Id == "player");
        await CharacterService.SaveProfile(db, b.Id, "player", new(b.HeadCheckpointId, player.Name, player.Description, new("player", Role: "Player"), Goal: "Find a lost sibling", Belief: "Trust the navigator", LocationId: player.LocationId), default);
        Assert.Contains("Find a lost sibling", JsonOf(await CharacterService.Read(db, b.Id, null, true, default)).GetRawText());
        Assert.DoesNotContain("Find a lost sibling", JsonOf(await CharacterService.Read(db, b.Id, null, false, default)).GetRawText());
        var cp = await MechanicsService.Apply(db, b.Id, new(b.HeadCheckpointId, "skill", "Navigation", 1, "Author progression"), default);
        var state = Json.Read<StoryState>(cp.StateJson);
        Assert.Equal(state.Skills["Navigation"], StoryWorld.From(cp).Capabilities.Single(c => c.OwnerId == "player" && c.Name == "Navigation").Rank);
    }
}
