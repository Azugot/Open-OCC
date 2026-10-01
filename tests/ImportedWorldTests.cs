using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Story;
using Xunit;

namespace StoryTests;

public class ImportedWorldTests : IAsyncLifetime
{
    readonly SqliteConnection connection = new("Data Source=:memory:");
    StoryDb Db() => new(new DbContextOptionsBuilder<StoryDb>().UseSqlite(connection).Options);
    public async Task InitializeAsync() { await connection.OpenAsync(); await using var db = Db(); await db.Database.EnsureCreatedAsync(); }
    public Task DisposeAsync() => connection.DisposeAsync().AsTask();
    static ImportClaim Claim(string key, string category, string text, string subject = "", string target = "", string value = "",
        int amount = 0, string kind = "fact", string visibility = "public", string[]? knowers = null, string disposition = "current",
        string? entityKind = null, string? location = null, string? actor = null)
        => new(key, category, text, subject, target, value, amount, kind, visibility, knowers ?? [], .95, [0], disposition, [], entityKind, location, actor);
    async Task<(Branch branch, ImportJob job)> Draft(StoryDb db)
    {
        var branch = await CampaignService.Create(db, new("Imported test story", false), default, new EngineSettings(MaxBeats: 2));
        var source = new ImportedSource { CampaignId = branch.CampaignId, FileName = "story.txt", Bytes = System.Text.Encoding.UTF8.GetBytes("Reviewed source evidence."), NormalizedText = "Reviewed source evidence." };
        var state = new StoryState("Harbor", "Day 7 · noon", "Cross the bay", "Rested", new() { ["Navigation"] = "F · 2/5" }, new() { ["Rations"] = 2 }, ["Mara", "Lyra"]);
        var job = new ImportJob { BranchId = branch.Id, SourceId = source.Id, ExpectedCheckpointId = branch.HeadCheckpointId,
            Method = Reconstruction.Method, Status = "review", ReviewCompleted = true, Processed = 1, Total = 1,
            ResumeJson = Json.Write(new ResumeProposal(state, [0], "Pack rations privately", false, [], "Mara")) };
        var block = new ImportSegment { JobId = job.Id, Ordinal = 0, Text = source.NormalizedText, Start = 0, Length = source.NormalizedText.Length };
        var claims = new[] {
            Claim("world:harbor", "world", "The harbor has a ferry dock.", "location", value: "Harbor"),
            Claim("world:mill", "world", "An old mill lies inland.", "location", value: "Mill"),
            Claim("character:mara", "character", "Mara is a navigator.", "Mara", value: "Cross the bay"),
            Claim("character:lyra", "character", "Lyra accompanies Mara.", "Lyra", value: "Find passage", location: "Harbor"),
            Claim("character:keeper", "character", "The Keeper tends the mill.", "Keeper", value: "Tend the mill", location: "Mill"),
            Claim("character:absent", "character", "Nera left months ago.", "Nera", disposition: "historical"),
            Claim("entity:fox", "entity", "A fox lives beside the mill.", "Fox", entityKind: "being", location: "Mill"),
            Claim("inventory:rations", "inventory", "Mara carries two rations.", "Rations", amount: 2),
            Claim("relationship:friends", "relationship", "Mara and Lyra are friends.", "Mara", "Lyra", "friend", knowers: ["Mara", "Lyra"]),
            Claim("connection:path", "connection", "A path connects the harbor to the mill.", "world:harbor", "world:mill", "path"),
            Claim("fact:letter", "fact", "Lyra secretly carries a sealed letter.", kind: "secret", visibility: "narrator", knowers: ["Lyra"]),
            Claim("knowledge:lyra", "knowledge", "The Keeper distrusts me.", "Lyra", kind: "belief", visibility: "narrator", knowers: ["Lyra"]),
            Claim("event:departure", "event", "Nera left the harbor months ago.", disposition: "historical", actor: "character:absent", location: "Harbor")
        };
        db.AddRange(source, job, block);
        foreach (var claim in claims) db.ImportProposals.Add(new() { JobId = job.Id, Key = claim.Key, Category = claim.Category, ContentJson = Json.Write(claim) });
        await db.SaveChangesAsync(); return (branch, job);
    }
    static async Task<StoryWorld> Head(StoryDb db, Branch branch)
        => StoryWorld.From(await db.Checkpoints.SingleAsync(x => x.Id == branch.HeadCheckpointId));
    static async Task<GenerationRun> Turn(StoryDb db, Branch branch, string action)
    {
        var run = new GenerationRun { BranchId = branch.Id, ExpectedCheckpointId = branch.HeadCheckpointId, Action = action };
        db.Add(run); await db.SaveChangesAsync();
        await StoryEngine.Execute(db, run, new ConfigurationBuilder().Build(), default, new FixtureAgentProvider(), new FixtureAgentProvider());
        return run;
    }
    [Fact]
    public async Task ReviewedEndingInventoryCreatesObjectsWithoutRedundantClaims()
    {
        await using var db = Db(); var (branch, job) = await Draft(db);
        var resume = Json.Read<ResumeProposal>(job.ResumeJson!);
        resume.State!.Inventory["Compass"] = 1;
        job.ResumeJson = Json.Write(resume); await db.SaveChangesAsync();
        await ImportReview.Approve(db, job.Id, new(branch.HeadCheckpointId, job.ProposalRevision), default);
        var world = await Head(db, branch);
        var compass = Assert.Single(world.Entities, e => e.Name == "Compass");
        Assert.Equal("object", compass.Kind);
        var connection = Assert.Single(world.Connections, c => c.TargetId == compass.Id && c.Kind == "carries");
        var evidence = world.Events.Single(e => e.Id == connection.EvidenceEventId);
        Assert.Equal("imported-resume", evidence.Kind);
        Assert.Contains(evidence.SourceEvidence!, s => s.SourceId == job.SourceId);
        Assert.Contains(world.Memories, m => m.OwnerId == "player" && m.Text.Contains("Compass"));
    }
    [Fact]
    public async Task UniqueGivenNameResolvesToRegisteredFullName()
    {
        await using var db = Db(); var (branch, job) = await Draft(db);
        var row = await db.ImportProposals.SingleAsync(p => p.JobId == job.Id && p.Key == "character:lyra");
        row.ContentJson = Json.Write(Json.Read<ImportClaim>(row.ContentJson) with { Subject = "Lyra Brightwell" });
        await db.SaveChangesAsync();
        await ImportReview.Approve(db, job.Id, new(branch.HeadCheckpointId, job.ProposalRevision), default);
        var world = await Head(db, branch);
        Assert.Single(world.Entities, e => e.Name.StartsWith("Lyra"));
        Assert.DoesNotContain(world.Entities, e => e.Name == "Lyra");
        Assert.Contains(world.Connections, c => c.Kind == "friend" && c.TargetId == world.Entities.Single(e => e.Name == "Lyra Brightwell").Id);
    }
    [Fact]
    public async Task PrivatePlayerBiographyIsRememberedWithoutBecomingPublicEntityDescription()
    {
        await using var db = Db(); var (branch, job) = await Draft(db);
        var row = await db.ImportProposals.SingleAsync(p => p.JobId == job.Id && p.Key == "character:mara");
        row.ContentJson = Json.Write(Json.Read<ImportClaim>(row.ContentJson) with { Text = "Mara secretly carries a forbidden blessing.", Visibility = "narrator" });
        await db.SaveChangesAsync();
        await ImportReview.Approve(db, job.Id, new(branch.HeadCheckpointId, job.ProposalRevision), default);
        var world = await Head(db, branch);
        Assert.Contains(world.Memories, m => m.OwnerId == "player" && m.Text.Contains("forbidden blessing"));
        Assert.DoesNotContain("forbidden blessing", world.Entities.Single(e => e.Id == "player").Description);
        var lyra = world.Entities.Single(e => e.Name == "Lyra");
        Assert.Contains("Mara", Json.Write(StoryEngine.CharacterContext(world, lyra.Id, [])));
        Assert.DoesNotContain("forbidden blessing", Json.Write(StoryEngine.CharacterContext(world, lyra.Id, [])));
    }
    [Fact]
    public async Task ReviewedOccurrenceRegistersItsNamedActorWithoutInventingCurrentWhereabouts()
    {
        await using var db = Db(); var (branch, job) = await Draft(db);
        var claim = Claim("event:orientation", "event", "A Council Member distributed orientation papers at the harbor.", actor: "Council Member", location: "Harbor", disposition: "historical");
        db.ImportProposals.Add(new() { JobId = job.Id, Key = claim.Key, Category = claim.Category, ContentJson = Json.Write(claim) }); await db.SaveChangesAsync();
        await ImportReview.Approve(db, job.Id, new(branch.HeadCheckpointId, job.ProposalRevision), default);
        var world = await Head(db, branch);
        var actor = Assert.Single(world.Entities, e => e.Name == "Council Member");
        Assert.Equal("", actor.LocationId);
        Assert.Contains(world.Events, e => e.ActorId == actor.Id && e.Text.Contains("orientation papers"));
        Assert.Contains(await db.Characters.ToListAsync(), c => c.Name == actor.Name);
    }
    [Fact]
    public async Task ApprovalPopulatesWorldAndPlaysTwoTurnsWithoutLeakingHistoryOrPlayerIdentity()
    {
        await using var db = Db(); var (branch, job) = await Draft(db);
        await ImportReview.Approve(db, job.Id, new(branch.HeadCheckpointId, job.ProposalRevision), default);
        var approved = branch.HeadCheckpointId; var world = await Head(db, branch);
        Assert.Equal("Mara", world.Entities.Single(x => x.Id == "player").Name);
        Assert.DoesNotContain(world.Characters, x => x.EntityId == "player");
        Assert.Equal(2, world.Settings.MaxBeats);
        var lyra = world.Entities.Single(x => x.Name == "Lyra"); var keeper = world.Entities.Single(x => x.Name == "Keeper");
        Assert.NotEqual(world.PlayerLocationId, keeper.LocationId);
        Assert.Equal("", world.Entities.Single(x => x.Name == "Nera").LocationId);
        Assert.Equal("being", world.Entities.Single(x => x.Name == "Fox").Kind);
        Assert.Contains(world.Connections, x => x.SourceId == "player" && x.Kind == "carries");
        Assert.Contains(world.Connections, x => x.Kind == "path");
        Assert.All(world.Connections, x => Assert.Contains(world.Events, e => e.Id == x.EvidenceEventId && e.SourceEvidence!.Any(s => s.SourceId == job.SourceId)));
        var history = world.Events.Single(x => x.Text.StartsWith("Nera left the harbor"));
        Assert.Empty(history.PerceivedBy);
        Assert.All(world.Events.Where(x => x.Kind.StartsWith("imported-")), e => Assert.All(e.SourceEvidence!, source => { Assert.Equal(0, source.StartOffset); Assert.True(source.EndOffset > source.StartOffset); }));
        var lyraContext = Json.Write(StoryEngine.CharacterContext(world, lyra.Id, []));
        var keeperContext = Json.Write(StoryEngine.CharacterContext(world, keeper.Id, []));
        Assert.Contains("sealed letter", lyraContext); Assert.Contains("distrusts me", lyraContext);
        Assert.DoesNotContain("sealed letter", keeperContext); Assert.DoesNotContain("distrusts me", keeperContext);
        Assert.DoesNotContain("Pack rations privately", lyraContext);
        Assert.DoesNotContain(await db.Messages.Where(x => x.BranchId == branch.Id).ToListAsync(), x => x.Provider == "imported-source");
        Assert.Single(await db.ImportedHistory.Where(x => x.BranchId == branch.Id).ToListAsync());
        Assert.Equal("completed", (await Turn(db, branch, "Ask Lyra about the ferry")).Status);
        Assert.Equal("completed", (await Turn(db, branch, "Wait for the ferry")).Status);
        var final = await Head(db, branch);
        Assert.Contains(final.Events, x => x.ActorId == keeper.Id && x.Kind == "scene" && !x.PerceivedBy.Contains("player"));
        var fork = await CampaignService.Fork(db, branch.Id, new(approved, "At import"), default);
        var forkWorld = await Head(db, fork);
        Assert.Equal(world.Events.Count, forkWorld.Events.Count);
        Assert.Equal(world.Memories.Count, forkWorld.Memories.Count);
        Assert.Single(await db.ImportedHistory.Where(x => x.BranchId == fork.Id).ToListAsync());
        Assert.True(final.Events.Count > forkWorld.Events.Count);
    }
    [Theory]
    [InlineData("running")]
    [InlineData("paused")]
    public async Task ApprovalRejectsUnfinishedTurnAndLeavesBranchUnchanged(string status)
    {
        await using var db = Db(); var (branch, job) = await Draft(db); var head = branch.HeadCheckpointId;
        db.GenerationRuns.Add(new() { BranchId = branch.Id, ExpectedCheckpointId = head, Status = status }); await db.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ImportReview.Approve(db, job.Id, new(head, job.ProposalRevision), default));
        Assert.Contains("existing story draft", error.Message);
        Assert.Equal(head, branch.HeadCheckpointId); Assert.Empty(await db.ImportedHistory.ToListAsync()); Assert.Empty(await db.Facts.ToListAsync());
    }
    [Fact]
    public async Task FailedCommitRollsBackGraphHistoryFactsRegistrationsAndCheckpoint()
    {
        await using var db = Db(); var (branch, job) = await Draft(db); var head = branch.HeadCheckpointId;
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER reject_import BEFORE INSERT ON Messages WHEN NEW.Provider = 'manual-review' BEGIN SELECT RAISE(ABORT, 'test rollback'); END;");
        await Assert.ThrowsAsync<DbUpdateException>(() => ImportReview.Approve(db, job.Id, new(head, job.ProposalRevision), default));
        db.ChangeTracker.Clear();
        Assert.Equal(head, (await db.Branches.SingleAsync()).HeadCheckpointId);
        Assert.Single(await db.Checkpoints.ToListAsync()); Assert.Empty(await db.ImportedHistory.ToListAsync());
        Assert.Empty(await db.Facts.ToListAsync()); Assert.Empty(await db.Characters.ToListAsync());
        Assert.Equal("review", (await db.ImportJobs.SingleAsync()).Status);
    }
    [Fact]
    public async Task RumoredEntitiesRemainBeliefsAndCitationPayloadsStayOutOfActorContext()
    {
        await using var db = Db(); var (branch, job) = await Draft(db);
        var rumor = Claim("entity:dragon", "entity", "A dragon supposedly lives inland.", "Dragon", kind: "rumor", visibility: "narrator", knowers: ["Lyra"], entityKind: "being");
        db.ImportProposals.Add(new() { JobId = job.Id, Key = rumor.Key, Category = rumor.Category, ContentJson = Json.Write(rumor) }); await db.SaveChangesAsync();
        await ImportReview.Approve(db, job.Id, new(branch.HeadCheckpointId, job.ProposalRevision), default);
        var world = await Head(db, branch); var lyra = world.Entities.Single(e => e.Name == "Lyra");
        Assert.DoesNotContain(world.Entities, e => e.Name == "Dragon");
        Assert.Contains(world.Memories, m => m.OwnerId == lyra.Id && m.Kind == "rumor" && m.Text.Contains("dragon"));
        Assert.Contains(world.Events, e => e.Text.Contains("dragon") && e.SourceEvidence!.Length > 0);
        Assert.DoesNotContain(job.SourceId.ToString(), Json.Write(StoryEngine.CharacterContext(world, lyra.Id, [])));
    }
    [Fact]
    public void ExplicitIdentityRepairsOldPlayerNpcWithoutChangingEarlierWorld()
    {
        var state = new StoryState("Harbor", "noon", "Sail", "Rested", new(), new(), ["Mara", "Lyra"]);
        var old = StoryWorld.FromState(state); var former = old.Entities.Single(e => e.Name == "Mara").Id;
        old.Events.Add(new("past", 0, 0, former, "hall", "Mara looked at the ferry.", [former], [former], "noon", "scene"));
        old.Memories.Add(new("mara-memory", former, "long", "fact", "I saw the ferry.", false, ["past"]));
        var block = new ImportSegment { Ordinal = 0, Start = 0, Length = 10, Text = "Ten letters" };
        var job = new ImportJob();
        var compiled = ImportedWorldCompiler.Compile(old, state, job, new(state, [0], "Wait", false, [], "Mara"), [], new Dictionary<int, ImportSegment> { [0] = block });
        Assert.Equal(former, old.Events[0].ActorId);
        Assert.Equal("player", compiled.Events[0].ActorId);
        Assert.Equal("Mara", compiled.Entities.Single(e => e.Id == "player").Name);
        Assert.DoesNotContain(compiled.Characters, c => c.EntityId == former || c.EntityId == "player");
        Assert.Contains(compiled.Memories, m => m.Id == "mara-memory" && m.OwnerId == "player");
    }
    [Fact]
    public async Task LargeExistingGraphCanContinueWithoutSendingCitationListsToModels()
    {
        await using var db = Db(); var (branch, job) = await Draft(db);
        await ImportReview.Approve(db, job.Id, new(branch.HeadCheckpointId, job.ProposalRevision), default);
        var checkpoint = await db.Checkpoints.SingleAsync(x => x.Id == branch.HeadCheckpointId); var world = StoryWorld.From(checkpoint);
        world.Entities.AddRange(Enumerable.Range(0, 650).Select(i => new WorldEntity("archived-object-" + i, "Archived object " + i, "object", "", "A reviewed historical object.")));
        checkpoint.WorldJson = Json.Write(world); await db.SaveChangesAsync();
        var provider = new ContextCapture();
        var run = new GenerationRun { BranchId = branch.Id, ExpectedCheckpointId = checkpoint.Id, Action = "Ask Lyra about the ferry" };
        db.Add(run); await db.SaveChangesAsync();
        await StoryEngine.Execute(db, run, new ConfigurationBuilder().Build(), default, provider, provider);
        Assert.Equal("completed", run.Status);
        Assert.DoesNotContain(provider.Contexts, text => text.Contains(job.SourceId.ToString()));
        Assert.All(provider.Contexts, context => Assert.True(context.Length < 100_000, "Model contexts must stay bounded as imported graph history grows."));
        Assert.True((await Head(db, branch)).Entities.Count > 650);
    }
    class ContextCapture : IAgentProvider
    {
        public List<string> Contexts { get; } = [];
        public Task<T> Generate<T>(AgentRequest request, CancellationToken ct)
        {
            Contexts.Add(Json.Write(request.Context)); return Task.FromResult(Json.Read<T>(Json.Write(request.Example)));
        }
    }
}

