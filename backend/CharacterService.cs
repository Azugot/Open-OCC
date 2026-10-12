using Microsoft.EntityFrameworkCore;

namespace Story;

public record CharacterEdit(Guid ExpectedCheckpointId, string Name, string Description, CharacterProfile Profile,
    string Goal = "", string Belief = "", string Emotion = "", string Intention = "", string LocationId = "");
public record CapabilityEdit(Guid ExpectedCheckpointId, CharacterCapability Capability);
public record RelationshipEdit(Guid ExpectedCheckpointId, CharacterRelationshipChange Relationship);

public static class CharacterService
{
    public static bool IsCharacter(WorldEntity e) => e.Kind is "character" or "being";
    public static HashSet<string> KnownEntities(StoryWorld w) => w.Events.Where(e => e.PerceivedBy.Contains("player"))
        .SelectMany(e => e.EntityIds.Append(e.ActorId).Append(e.LocationId)).Append("player").Append(w.PlayerLocationId)
        .Concat(w.Entities.Where(e => e.KnownTo?.Contains("player") == true).Select(e => e.Id)).ToHashSet();
    public static bool Visible(string[]? knownTo, string viewer) => knownTo is null || knownTo.Contains(viewer);

    public static void Normalize(StoryWorld w, StoryState state, IEnumerable<Character>? registered = null,
        IEnumerable<Relationship>? legacy = null, int sequence = 0)
    {
        w.CharacterProfiles ??= []; w.Capabilities ??= []; w.RelationshipHistory ??= [];
        var people = (registered ?? []).ToArray();
        foreach (var entity in w.Entities.Where(IsCharacter))
        {
            var profile = w.CharacterProfiles.SingleOrDefault(p => p.EntityId == entity.Id);
            var matches = people.Where(p => p.Name.Equals(entity.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
            profile ??= new(entity.Id, entity.Id == "player" ? "Player" : "NPC");
            if (profile.CampaignCharacterId is null && matches.Length == 1 && w.Entities.Count(e => IsCharacter(e) && e.Name.Equals(entity.Name, StringComparison.OrdinalIgnoreCase)) == 1) profile = profile with { CampaignCharacterId = matches[0].Id };
            var index = w.CharacterProfiles.FindIndex(p => p.EntityId == entity.Id);
            if (index < 0) w.CharacterProfiles.Add(profile); else w.CharacterProfiles[index] = profile;
        }
        // Only legacy worlds without any player capability collection inherit the old skill map.
        // Once managed, inactive skills must not be resurrected on reload.
        if (!w.Capabilities.Any(c => c.OwnerId == "player"))
            foreach (var skill in state.Skills) w.Capabilities.Add(new("legacy-skill-" + StoryId(skill.Key), "player", "proficiency", skill.Key, Rank: skill.Value));
        foreach (var edge in w.Connections)
        {
            if (!w.Entities.Any(e => e.Id == edge.SourceId && IsCharacter(e)) || !w.Entities.Any(e => e.Id == edge.TargetId && IsCharacter(e))) continue;
            if (w.RelationshipHistory.Any(h => h.Id == "connection-" + edge.Id)) continue;
            var ev = w.Events.SingleOrDefault(e => e.Id == edge.EvidenceEventId);
            // Connections of different kinds remain distinct; an occurrence never silently ends another link.
            w.RelationshipHistory.Add(new("connection-" + edge.Id, "connection-" + edge.SourceId + ":" + edge.TargetId + ":" + edge.Kind,
                edge.SourceId, edge.TargetId, edge.Kind, edge.Kind, null, ev?.Text ?? "Established connection", ev?.Time ?? "Unknown time",
                sequence, EvidenceEventId: ev?.Id, KnownTo: ev?.PerceivedBy ?? ["director"], Baseline: ev is null));
        }
        foreach (var row in legacy ?? [])
        {
            var from = w.CharacterProfiles.SingleOrDefault(p => p.CampaignCharacterId == row.FromCharacterId);
            var to = w.CharacterProfiles.SingleOrDefault(p => p.CampaignCharacterId == row.ToCharacterId);
            if (from is null || to is null || w.RelationshipHistory.Any(h => h.RelationshipId == "legacy-" + row.Id)) continue;
            w.RelationshipHistory.Add(new("legacy-" + row.Id, "legacy-" + row.Id, from.EntityId, to.EntityId, row.Label, row.Label,
                row.Score, row.Notes, "Unknown time", row.EffectiveSequence, KnownTo: ["director"], Baseline: true));
        }
    }
    private static string StoryId(string name) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name))).ToLowerInvariant()[..24];

    private static async Task RecoverCheckpointChanges(StoryDb db, Guid branch, int through, StoryWorld world, CancellationToken ct)
    {
        var snapshots = await db.Checkpoints.AsNoTracking().Where(c => c.BranchId == branch && c.Sequence <= through).OrderBy(c => c.Sequence)
            .Select(c => new { c.Sequence, c.WorldJson, c.StateJson }).ToArrayAsync(ct);
        var previous = new Dictionary<string, Connection>();
        foreach (var snapshot in snapshots)
        {
            if (snapshot.WorldJson == "{}") continue;
            var saved = Json.Read<StoryWorld>(snapshot.WorldJson);
            foreach (var removed in previous.Values.Where(old => !saved.Connections.Any(edge => edge.Id == old.Id)))
            {
                var key = "snapshot-end-" + removed.Id + ":" + snapshot.Sequence;
                var first = world.RelationshipHistory.FirstOrDefault(h => h.Id == "connection-" + removed.Id);
                if (first is not null && !world.RelationshipHistory.Any(h => h.Id == key))
                    world.RelationshipHistory.Add(first with { Id = key, Sequence = snapshot.Sequence, Active = false,
                        Reason = "Connection removed in this recorded checkpoint", PreviousLabel = removed.Kind, Baseline = false,
                        Time = Json.Read<StoryState>(snapshot.StateJson).Time, EvidenceEventId = null });
            }
            foreach (var edge in saved.Connections)
            {
                if (!previous.ContainsKey(edge.Id))
                {
                    var index = world.RelationshipHistory.FindIndex(h => h.Id == "connection-" + edge.Id);
                    if (index < 0 && world.Entities.Any(e => e.Id == edge.SourceId && IsCharacter(e)) && world.Entities.Any(e => e.Id == edge.TargetId && IsCharacter(e)))
                    {
                        var ev = world.Events.SingleOrDefault(e => e.Id == edge.EvidenceEventId);
                        world.RelationshipHistory.Add(new("connection-" + edge.Id, "connection-" + edge.SourceId + ":" + edge.TargetId + ":" + edge.Kind,
                            edge.SourceId, edge.TargetId, edge.Kind, edge.Kind, null, ev?.Text ?? "Established connection", ev?.Time ?? "Unknown time",
                            snapshot.Sequence, EvidenceEventId: ev?.Id, KnownTo: ev?.PerceivedBy ?? ["director"], Baseline: ev is null));
                    }
                    if (index >= 0)
                    {
                        var entry = world.RelationshipHistory[index];
                        world.RelationshipHistory[index] = entry with { Type = edge.Kind, Label = edge.Kind,
                            RelationshipId = "connection-" + edge.SourceId + ":" + edge.TargetId + ":" + edge.Kind, Sequence = snapshot.Sequence };
                    }
                }
                if (previous.TryGetValue(edge.Id, out var old) && old.Kind != edge.Kind && old.SourceId == edge.SourceId && old.TargetId == edge.TargetId &&
                    world.Entities.Any(e => e.Id == edge.SourceId && IsCharacter(e)) && world.Entities.Any(e => e.Id == edge.TargetId && IsCharacter(e)))
                {
                    var key = "snapshot-" + edge.Id + ":" + snapshot.Sequence;
                    if (!world.RelationshipHistory.Any(h => h.Id == key))
                    {
                        var ev = world.Events.SingleOrDefault(e => e.Id == edge.EvidenceEventId);
                        world.RelationshipHistory.Add(new(key, "connection-" + edge.SourceId + ":" + edge.TargetId + ":" + old.Kind,
                            edge.SourceId, edge.TargetId, old.Kind, edge.Kind, null, ev?.Text ?? "Recorded checkpoint correction", Json.Read<StoryState>(snapshot.StateJson).Time,
                            snapshot.Sequence, PreviousLabel: old.Kind, EvidenceEventId: ev?.Id, KnownTo: ev?.PerceivedBy ?? ["director"]));
                    }
                }
            }
            previous = saved.Connections.ToDictionary(e => e.Id);
        }
    }

    public static void SyncPlayerSkills(StoryWorld w, StoryState state)
    {
        state.Skills.Clear();
        foreach (var c in w.Capabilities.Where(c => c.OwnerId == "player" && c.Category == "proficiency" && c.Active)) state.Skills.Add(c.Name, c.Rank);
    }
    public static CharacterRelationshipChange[] RelationshipContext(StoryWorld w, string viewer) => w.RelationshipHistory
        .GroupBy(h => h.RelationshipId).Select(g => g.OrderBy(h => h.Sequence).Last())
        .Where(h => viewer == "director" || Visible(h.KnownTo, viewer) && (h.FromId == viewer || h.ToId == viewer))
        .ToArray();
    public static object[] CapabilityContext(StoryWorld w, string viewer) => w.Capabilities
        .Where(c => c.Active && (viewer == "director" || c.OwnerId == viewer || Visible(c.KnownTo, viewer)))
        .Where(c => viewer == "director" || c.OwnerId == viewer || w.Events.Any(e => e.PerceivedBy.Contains(viewer) && (e.ActorId == c.OwnerId || e.EntityIds.Contains(c.OwnerId))) ||
            w.Entities.Any(e => e.Id == c.OwnerId && e.LocationId != "" && e.LocationId == w.Entities.FirstOrDefault(p => p.Id == viewer)?.LocationId))
        .Select(c => (object)new { c.OwnerId, c.Category, c.Name, c.Description, c.Rank, c.Limitations }).ToArray();

    public static void Validate(StoryWorld w, IReadOnlySet<Guid>? registered = null)
    {
        var ids = w.Entities.Where(IsCharacter).Select(e => e.Id).ToHashSet();
        void Text(string? text, int max, string field, bool required = false)
        { if (text is null || text.Length > max || required && string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException($"Invalid {field}; maximum {max} characters."); }
        if (w.CharacterProfiles is null || w.Capabilities is null || w.RelationshipHistory is null) throw new InvalidOperationException("Character collections cannot be null.");
        if (w.CharacterProfiles.Select(p => p.EntityId).Distinct().Count() != w.CharacterProfiles.Count) throw new InvalidOperationException("Duplicate character profile.");
        if (w.CharacterProfiles.Where(p => p.CampaignCharacterId is not null).GroupBy(p => p.CampaignCharacterId).Any(g => g.Count() > 1)) throw new InvalidOperationException("A campaign identity is already linked to another character.");
        foreach (var p in w.CharacterProfiles)
        {
            if (!ids.Contains(p.EntityId) || p.CampaignCharacterId is { } link && registered is not null && !registered.Contains(link)) throw new InvalidOperationException("Unknown character profile identity.");
            Text(p.Role, 100, "role"); Text(p.Appearance, 2000, "appearance"); Text(p.Personality, 2000, "personality"); Text(p.SpeakingStyle, 1000, "speaking style"); Text(p.Guidance, 4000, "guidance");
            Text(p.Goal, 1000, "goal"); Text(p.Belief, 1000, "belief"); Text(p.Emotion, 200, "emotion"); Text(p.Intention, 1000, "intention");
            if (p.Status is not ("active" or "inactive" or "deceased" or "unknown") || p.Aliases is { Length: > 20 } || (p.Aliases ?? []).Any(a => string.IsNullOrWhiteSpace(a) || a.Length > 100)) throw new InvalidOperationException("Invalid character status or aliases.");
        }
        if (w.Capabilities.Count > 5000 || w.Capabilities.Select(c => c.Id).Distinct().Count() != w.Capabilities.Count) throw new InvalidOperationException("Duplicate or excessive capabilities.");
        foreach (var c in w.Capabilities)
        {
            if (!ids.Contains(c.OwnerId) || string.IsNullOrWhiteSpace(c.Id) || c.Category is not ("ability" or "power" or "proficiency")) throw new InvalidOperationException("Invalid capability identity, owner, or category.");
            Text(c.Name, 100, "capability name", true); Text(c.Description, 2000, "capability description"); Text(c.Rank, 100, "rank"); Text(c.Limitations, 2000, "limitations");
            if ((c.KnownTo ?? []).Any(id => id != "director" && !ids.Contains(id))) throw new InvalidOperationException("Unknown capability knower.");
        }
        if (w.Capabilities.GroupBy(c => (c.OwnerId, c.Category, Name: c.Name.ToUpperInvariant())).Any(g => g.Count() > 1)) throw new InvalidOperationException("Duplicate capability name for this owner and category.");
        if (w.Capabilities.Count(c => c.OwnerId == "player" && c.Category == "proficiency" && c.Active) > 50) throw new InvalidOperationException("The player can have at most 50 active proficiencies.");
        if (w.RelationshipHistory.Select(h => h.Id).Distinct().Count() != w.RelationshipHistory.Count) throw new InvalidOperationException("Duplicate relationship history entry.");
        foreach (var h in w.RelationshipHistory)
        {
            if (!ids.Contains(h.FromId) || !ids.Contains(h.ToId) || h.FromId == h.ToId || string.IsNullOrWhiteSpace(h.Id) || string.IsNullOrWhiteSpace(h.RelationshipId) || h.Score is < -100 or > 100)
                throw new InvalidOperationException("Invalid relationship identity, endpoints, or score.");
            Text(h.Type, 100, "relationship type", true); Text(h.Label, 200, "relationship label", true); Text(h.Reason, 3000, "relationship reason");
            if (h.EvidenceEventId is { } ev && !w.Events.Any(e => e.Id == ev) || (h.KnownTo ?? []).Any(id => id != "director" && !ids.Contains(id))) throw new InvalidOperationException("Invalid relationship evidence or audience.");
        }
    }

    public static async Task<object> Read(StoryDb db, Guid branchId, Guid? checkpointId, bool author, CancellationToken ct)
    {
        var branch = await db.Branches.AsNoTracking().SingleOrDefaultAsync(b => b.Id == branchId, ct) ?? throw new KeyNotFoundException();
        var cp = await db.Checkpoints.AsNoTracking().SingleOrDefaultAsync(c => c.Id == (checkpointId ?? branch.HeadCheckpointId) && c.BranchId == branchId, ct) ?? throw new KeyNotFoundException();
        var w = StoryWorld.From(cp);
        var registered = await db.Characters.AsNoTracking().Where(c => c.CampaignId == branch.CampaignId).ToArrayAsync(ct);
        var legacy = await db.Relationships.AsNoTracking().Where(r => r.BranchId == branchId && r.EffectiveSequence <= cp.Sequence).ToArrayAsync(ct);
        Normalize(w, Json.Read<StoryState>(cp.StateJson), registered, legacy, cp.Sequence);
        await RecoverCheckpointChanges(db, branchId, cp.Sequence, w, ct);
        var known = KnownEntities(w);
        foreach (var entity in w.Entities.Where(e => e.LocationId == w.PlayerLocationId && Visible(e.KnownTo, "player"))) known.Add(entity.Id);
        var visibleEvents = w.Events.Where(e => author || e.PerceivedBy.Contains("player")).ToArray();
        var characters = w.Entities.Where(e => IsCharacter(e) && (author || known.Contains(e.Id))).Select(e => {
            var p = w.CharacterProfiles.Single(p => p.EntityId == e.Id);
            var current = w.Characters.SingleOrDefault(c => c.EntityId == e.Id) ?? (e.Id == "player" ? new CharacterState(e.Id, p.Goal, p.Belief, p.Emotion, p.Intention, "", new()) : null);
            var present = e.LocationId == w.PlayerLocationId;
            var relationships = w.RelationshipHistory.Where(h => (h.FromId == e.Id || h.ToId == e.Id) && (author || Visible(h.KnownTo, "player")))
                .Where(h => author || known.Contains(h.FromId) && known.Contains(h.ToId)).OrderBy(h => h.Sequence).ToArray();
            return new { entity = e with { LocationId = author || present ? e.LocationId : "" }, profile = p with { Guidance = author ? p.Guidance : "", CampaignCharacterId = author ? p.CampaignCharacterId : null,
                Goal = author ? p.Goal : "", Belief = author ? p.Belief : "", Emotion = author ? p.Emotion : "", Intention = author ? p.Intention : "" },
                state = author ? current : null, present, location = author || present ? w.Entities.FirstOrDefault(l => l.Id == e.LocationId)?.Name : null,
                capabilities = w.Capabilities.Where(c => c.OwnerId == e.Id && (author || Visible(c.KnownTo, "player"))).ToArray(),
                relationships, events = visibleEvents.Where(ev => ev.ActorId == e.Id || ev.EntityIds.Contains(e.Id)).ToArray(),
                memories = w.Memories.Where(m => (author && m.OwnerId == e.Id || !author && m.OwnerId == "player") && m.EvidenceEventIds.Any(id => visibleEvents.Any(ev => ev.Id == id && (ev.ActorId == e.Id || ev.EntityIds.Contains(e.Id))))).ToArray(),
                identityCandidates = author && p.CampaignCharacterId is null ? registered.Where(c => c.Name.Equals(e.Name, StringComparison.OrdinalIgnoreCase)).ToArray() : [] };
        }).ToArray();
        return new { checkpointId = cp.Id, historical = cp.Id != branch.HeadCheckpointId, characters,
            places = w.Entities.Where(e => e.Kind == "place" && (author || known.Contains(e.Id))).ToArray(),
            registeredCharacters = author ? registered : [], pending = cp.Id == branch.HeadCheckpointId && await db.GenerationRuns.AnyAsync(r => r.BranchId == branchId && (r.Status == "running" || r.Status == "paused"), ct) };
    }

    public static async Task Apply(StoryDb db, Guid branchId, Guid expected, string label, Action<StoryWorld, StoryState, int> change, CancellationToken ct)
    {
        var branch = await db.Branches.SingleOrDefaultAsync(b => b.Id == branchId, ct) ?? throw new KeyNotFoundException();
        var registered = (await db.Characters.Where(c => c.CampaignId == branch.CampaignId).ToArrayAsync(ct)).Concat(db.Characters.Local.Where(c => c.CampaignId == branch.CampaignId)).DistinctBy(c => c.Id).ToArray();
        var cp = await db.Checkpoints.SingleOrDefaultAsync(c => c.Id == expected && c.BranchId == branchId, ct) ?? throw new KeyNotFoundException();
        var legacy = await db.Relationships.Where(r => r.BranchId == branchId && r.EffectiveSequence <= cp.Sequence).ToArrayAsync(ct);
        var recovered = StoryWorld.From(cp); await RecoverCheckpointChanges(db, branchId, cp.Sequence, recovered, ct);
        await EngineApi.EditState(db, branchId, expected, label, (w, s) => {
            Normalize(w, s, registered, legacy, cp.Sequence);
            foreach (var h in recovered.RelationshipHistory.Where(h => !w.RelationshipHistory.Any(current => current.Id == h.Id))) w.RelationshipHistory.Add(h);
            var before = w.Entities.ToArray();
            change(w, s, cp.Sequence + 1); Validate(w, registered.Select(c => c.Id).ToHashSet()); SyncPlayerSkills(w, s);
            var names = s.Participants.Select(name => {
                var candidates = before.Where(e => IsCharacter(e) && e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
                var updated = candidates.Length == 1 ? w.Entities.SingleOrDefault(e => e.Id == candidates[0].Id) : null;
                return updated is null ? name : updated.LocationId == w.PlayerLocationId ? updated.Name : null;
            }).OfType<string>().Concat(w.Entities.Where(e => IsCharacter(e) && e.Id != "player" && e.LocationId == w.PlayerLocationId &&
                !before.Any(old => old.Id == e.Id && old.LocationId == e.LocationId)).Select(e => e.Name)).Distinct().ToArray();
            return s with { Participants = names, Location = w.Entities.FirstOrDefault(e => e.Id == w.PlayerLocationId)?.Name ?? s.Location };
        }, ct);
    }
    public static async Task SaveProfile(StoryDb db, Guid branch, string? id, CharacterEdit request, CancellationToken ct)
    {
        CampaignService.RequireText(request.Name, 100, "Character name");
        if (request.Description is null || request.Description.Length > 2000 || request.Profile is null) throw new InvalidOperationException("Invalid character description or profile.");
        foreach (var text in new[] { request.Goal, request.Belief, request.Intention }) if (text is null || text.Length > 1000) throw new InvalidOperationException("Character state is limited to 1000 characters.");
        if (request.Emotion is null || request.Emotion.Length > 200) throw new InvalidOperationException("Emotion is limited to 200 characters.");
        if (id is null && request.Profile.CampaignCharacterId is null)
        {
            var campaign = await db.Branches.Where(b => b.Id == branch).Select(b => b.CampaignId).SingleAsync(ct);
            if (!await db.Characters.AnyAsync(c => c.CampaignId == campaign && c.Name == request.Name.Trim(), ct))
            {
                var registration = new Character { CampaignId = campaign, Name = request.Name.Trim(), Description = request.Description.Trim(), Goals = request.Goal, Status = request.Profile.Status };
                db.Characters.Add(registration); request = request with { Profile = request.Profile with { CampaignCharacterId = registration.Id } };
            }
        }
        await Apply(db, branch, request.ExpectedCheckpointId, "Character profile updated", (w, s, _) => {
            var entity = id is null ? new WorldEntity(Guid.NewGuid().ToString("N"), request.Name.Trim(), "character", request.LocationId, request.Description.Trim(), ["player", "director"]) : w.Entities.SingleOrDefault(e => e.Id == id && IsCharacter(e)) ?? throw new KeyNotFoundException();
            if (request.LocationId != "" && !w.Entities.Any(e => e.Id == request.LocationId && e.Kind == "place")) throw new InvalidOperationException("Unknown character location.");
            if (entity.Id == "player" && string.IsNullOrWhiteSpace(request.LocationId)) throw new InvalidOperationException("The player requires a scene location.");
            var updated = entity with { Name = request.Name.Trim(), Description = request.Description.Trim(), LocationId = request.LocationId };
            if (id is null) w.Entities.Add(updated); else w.Entities[w.Entities.IndexOf(entity)] = updated;
            var profile = request.Profile with { EntityId = entity.Id, Goal = request.Goal, Belief = request.Belief, Emotion = request.Emotion, Intention = request.Intention, Aliases = (request.Profile.Aliases ?? []).Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim())
                .Concat(entity.Name != updated.Name ? [entity.Name] : []).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() };
            var pi = w.CharacterProfiles.FindIndex(p => p.EntityId == entity.Id); if (pi < 0) w.CharacterProfiles.Add(profile); else w.CharacterProfiles[pi] = profile;
            if (entity.Id != "player") {
                var old = w.Characters.SingleOrDefault(c => c.EntityId == entity.Id);
                var state = new CharacterState(entity.Id, request.Goal, request.Belief, request.Emotion, request.Intention, old?.DecisionSummary ?? "", old?.Stats ?? new());
                if (old is null) w.Characters.Add(state); else w.Characters[w.Characters.IndexOf(old)] = state;
            }
            if (entity.Id == "player" && request.LocationId != "") { w.PlayerLocationId = request.LocationId; }
        }, ct);
    }
    public static async Task SaveCapability(StoryDb db, Guid branch, string owner, CapabilityEdit request, CancellationToken ct)
        => await Apply(db, branch, request.ExpectedCheckpointId, "Character capability updated", (w, _, _) => {
            if (!w.Entities.Any(e => e.Id == owner && IsCharacter(e))) throw new KeyNotFoundException();
            var c = request.Capability ?? throw new InvalidOperationException("Capability is required.");
            CampaignService.RequireText(c.Name, 100, "Capability name");
            var value = c with { Id = string.IsNullOrWhiteSpace(c.Id) ? Guid.NewGuid().ToString("N") : c.Id, OwnerId = owner, Name = c.Name.Trim() };
            var old = w.Capabilities.SingleOrDefault(x => x.Id == value.Id);
            if (old is not null && old.OwnerId != owner) throw new InvalidOperationException("Capability belongs to another character.");
            if (old is null) w.Capabilities.Add(value); else w.Capabilities[w.Capabilities.IndexOf(old)] = value;
        }, ct);
    public static async Task SaveRelationship(StoryDb db, Guid branch, RelationshipEdit request, CancellationToken ct)
        => await Apply(db, branch, request.ExpectedCheckpointId, "Character relationship updated", (w, s, sequence) => {
            var r = request.Relationship ?? throw new InvalidOperationException("Relationship is required.");
            var previous = w.RelationshipHistory.LastOrDefault(h => h.RelationshipId == r.RelationshipId);
            if (previous is not null && (previous.FromId != r.FromId || previous.ToId != r.ToId || previous.Type != r.Type)) throw new InvalidOperationException("Relationship identity, direction, and type cannot change.");
            w.RelationshipHistory.Add(r with { Id = Guid.NewGuid().ToString("N"), RelationshipId = previous?.RelationshipId ?? Guid.NewGuid().ToString("N"),
                Sequence = sequence, Time = s.Time, PreviousLabel = previous?.Label, PreviousScore = previous?.Score, Baseline = false, EvidenceEventId = null });
        }, ct);

    public static async Task<Character> CreateLegacy(StoryDb db, Guid campaign, CreateCharacterRequest request, CancellationToken ct)
    {
        CampaignService.RequireText(request.Name, 100, "Character name");
        if (await db.Characters.AnyAsync(c => c.CampaignId == campaign && c.Name == request.Name.Trim(), ct)) throw new InvalidOperationException("A campaign character already has that name.");
        var branch = await db.Branches.Where(b => b.CampaignId == campaign && (request.BranchId == null || b.Id == request.BranchId)).OrderBy(b => b.CreatedAt).FirstOrDefaultAsync(ct) ?? throw new KeyNotFoundException();
        await SaveProfile(db, branch.Id, null, new(request.ExpectedCheckpointId ?? branch.HeadCheckpointId, request.Name, request.Description,
            new("", Status: request.Status), request.Goals), ct);
        return await db.Characters.SingleOrDefaultAsync(c => c.CampaignId == campaign && c.Name == request.Name.Trim(), ct) ?? throw new InvalidOperationException("Character identity is ambiguous.");
    }
    public static async Task<Relationship> SaveLegacyRelationship(StoryDb db, Guid branchId, UpsertRelationshipRequest request, CancellationToken ct)
    {
        CampaignService.RequireText(request.Label, 200, "Relationship label");
        if (request.Notes is null || request.Notes.Length > 3000) throw new InvalidOperationException("Relationship notes are limited to 3000 characters.");
        var branch = await db.Branches.SingleOrDefaultAsync(b => b.Id == branchId, ct) ?? throw new KeyNotFoundException();
        if (!await db.Characters.AnyAsync(c => c.Id == request.FromCharacterId && c.CampaignId == branch.CampaignId, ct) || !await db.Characters.AnyAsync(c => c.Id == request.ToCharacterId && c.CampaignId == branch.CampaignId, ct)) throw new InvalidOperationException("Both characters must belong to the campaign.");
        var row = await db.Relationships.SingleOrDefaultAsync(r => r.BranchId == branchId && r.FromCharacterId == request.FromCharacterId && r.ToCharacterId == request.ToCharacterId, ct);
        var isNew = row is null;
        row ??= new Relationship { BranchId = branchId, FromCharacterId = request.FromCharacterId, ToCharacterId = request.ToCharacterId };
        await Apply(db, branchId, branch.HeadCheckpointId, "Character relationship updated", (w, state, sequence) => {
            var from = w.CharacterProfiles.SingleOrDefault(p => p.CampaignCharacterId == request.FromCharacterId) ?? throw new InvalidOperationException("From identity needs an explicit character link.");
            var to = w.CharacterProfiles.SingleOrDefault(p => p.CampaignCharacterId == request.ToCharacterId) ?? throw new InvalidOperationException("To identity needs an explicit character link.");
            var previous = w.RelationshipHistory.LastOrDefault(h => h.RelationshipId == "legacy-" + row.Id);
            w.RelationshipHistory.Add(new(Guid.NewGuid().ToString("N"), "legacy-" + row.Id, from.EntityId, to.EntityId, previous?.Type ?? request.Label.Trim(), request.Label.Trim(),
                Math.Clamp(request.Score, -100, 100), request.Notes.Trim(), state.Time, sequence, PreviousLabel: previous?.Label, PreviousScore: previous?.Score, KnownTo: ["director"]));
            row.Label = request.Label.Trim(); row.Score = Math.Clamp(request.Score, -100, 100); row.Notes = request.Notes.Trim(); row.EffectiveSequence = sequence;
            if (isNew) db.Relationships.Add(row);
        }, ct);
        return row;
    }

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/branches/{id:guid}/characters", async (Guid id, Guid? checkpointId, bool? author, StoryDb db, CancellationToken ct) => Results.Ok(await Read(db, id, checkpointId, author == true, ct)));
        app.MapGet("/api/branches/{id:guid}/characters/{entityId}", async (Guid id, string entityId, Guid? checkpointId, bool? author, StoryDb db, CancellationToken ct) => {
            var roster = System.Text.Json.JsonSerializer.SerializeToElement(await Read(db, id, checkpointId, author == true, ct), Json.Options);
            var row = roster.GetProperty("characters").EnumerateArray().FirstOrDefault(c => c.GetProperty("entity").GetProperty("id").GetString() == entityId);
            return row.ValueKind == System.Text.Json.JsonValueKind.Undefined ? Results.NotFound() : Results.Ok(row);
        });
        app.MapPost("/api/branches/{id:guid}/characters", async (Guid id, CharacterEdit request, StoryDb db, CancellationToken ct) => { await SaveProfile(db, id, null, request, ct); return Results.NoContent(); });
        app.MapPut("/api/branches/{id:guid}/characters/{entityId}", async (Guid id, string entityId, CharacterEdit request, StoryDb db, CancellationToken ct) => { await SaveProfile(db, id, entityId, request, ct); return Results.NoContent(); });
        app.MapPut("/api/branches/{id:guid}/characters/{entityId}/capabilities", async (Guid id, string entityId, CapabilityEdit request, StoryDb db, CancellationToken ct) => { await SaveCapability(db, id, entityId, request, ct); return Results.NoContent(); });
        app.MapPut("/api/branches/{id:guid}/character-relationships", async (Guid id, RelationshipEdit request, StoryDb db, CancellationToken ct) => { await SaveRelationship(db, id, request, ct); return Results.NoContent(); });
    }
}
