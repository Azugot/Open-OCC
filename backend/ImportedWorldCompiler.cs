using System.Security.Cryptography;
using System.Text;

namespace Story;

// Compile only reviewed, cited data. The document is never replayed as if it were a live turn.
public static class ImportedWorldCompiler
{
    const string Unknown = "Not established in the source.";
    static bool Canonical(ImportClaim claim) => claim.Kind is not ("belief" or "rumor");
    public static StoryWorld Compile(StoryWorld prior, StoryState priorState, ImportJob job, ResumeProposal resume,
        IReadOnlyList<ImportClaim> claims, IReadOnlyDictionary<int, ImportSegment> blocks)
    {
        var state = resume.State ?? throw new InvalidOperationException("A resume state is required.");
        var world = priorState == StoryState.Empty || priorState.Location == StoryState.Empty.Location
            ? new StoryWorld { Settings = prior.Settings } : Json.Read<StoryWorld>(Json.Write(prior));
        // A reviewed identity can correct an older import that registered the player as an NPC.
        var formerPlayer = world.Entities.SingleOrDefault(e => e.Kind == "character" && e.Id != "player" && e.Name.Equals(resume.PlayerCharacterName, StringComparison.OrdinalIgnoreCase));
        if (formerPlayer is not null)
        {
            string Remap(string id) => id == formerPlayer.Id ? "player" : id;
            world.Entities.Remove(formerPlayer);
            var existingPlayer = world.Entities.FindIndex(e => e.Id == "player");
            var corrected = formerPlayer with { Id = "player" };
            if (existingPlayer >= 0) world.Entities[existingPlayer] = corrected; else world.Entities.Add(corrected);
            world.Entities = world.Entities.Select(e => e with { KnownTo = e.KnownTo?.Select(Remap).Distinct().ToArray() }).ToList();
            world.Characters.RemoveAll(c => c.EntityId == formerPlayer.Id || c.EntityId == "player");
            world.Events = world.Events.Select(e => e with { ActorId = Remap(e.ActorId), PerceivedBy = e.PerceivedBy.Select(Remap).Distinct().ToArray(), EntityIds = e.EntityIds.Select(Remap).Distinct().ToArray() }).ToList();
            world.Connections = world.Connections.Select(c => c with { SourceId = Remap(c.SourceId), TargetId = Remap(c.TargetId) }).ToList();
            world.Memories = world.Memories.Select(m => m with { OwnerId = Remap(m.OwnerId) }).ToList();
        }
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entity in world.Entities) aliases.TryAdd(entity.Name, entity.Id);
        aliases["director"] = "director";
        var namedEntities = world.Entities.ToDictionary(e => e.Kind + ":" + e.Name, StringComparer.OrdinalIgnoreCase);
        var entityIndices = world.Entities.Select((entity, index) => (entity.Id, index)).ToDictionary(e => e.Id, e => e.index);
        var ambiguousNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? Resolve(string? reference)
        {
            if (reference is not null && ambiguousNames.Contains(reference)) throw new InvalidOperationException($"Character name {reference} is ambiguous. Use its full reviewed name or entity key.");
            return reference is not null && aliases.TryGetValue(reference, out var id) ? id : null;
        }
        string Upsert(string key, string name, string kind, string description, string[]? knownTo, bool update)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException($"{key} needs a nonempty entity name before approval.");
            var found = namedEntities.GetValueOrDefault(kind + ":" + name);
            var id = found?.Id ?? (kind == "character" && name.Equals(resume.PlayerCharacterName, StringComparison.OrdinalIgnoreCase) ? "player" : Id("entity", key));
            if (id == "player" && entityIndices.TryGetValue(id, out var playerIndex)) found = world.Entities[playerIndex];
            if (found is null)
            {
                entityIndices[id] = world.Entities.Count;
                world.Entities.Add(new(id, name, kind, kind == "place" ? id : "", description, knownTo));
            }
            else if (update) world.Entities[entityIndices[id]] = found with { Name = name, Description = description, KnownTo = knownTo };
            namedEntities[kind + ":" + name] = world.Entities[entityIndices[id]];
            aliases[key] = id; aliases[name] = id;
            return id;
        }
        // Locations and entities must exist before resolving claims that refer to them.
        foreach (var claim in claims.Where(c => Canonical(c) && c.Category == "world" && c.Subject == "location"))
            Upsert(claim.Key, claim.Value, "place", claim.Text, claim.Visibility == "public" ? null : [], claim.Disposition == "current");
        foreach (var claim in claims.Where(c => Canonical(c) && c.Category is "character" or "entity"))
            Upsert(claim.Key, claim.Subject, claim.Category == "character" ? "character" : claim.EntityKind!, claim.Text,
                claim.Visibility == "public" ? null : [], claim.Disposition == "current");
        foreach (var claim in claims.Where(c => Canonical(c) && !string.IsNullOrWhiteSpace(c.Location)))
            if (Resolve(claim.Location) is null && !claim.Location!.Contains(':'))
                Upsert("place:" + claim.Location, claim.Location, "place", "Location named in reviewed evidence.", claim.Visibility == "public" ? null : [], false);
        // Dialogue often uses a given name after a fuller introduction. Resolve
        // it only when unique, so it cannot create a duplicate or guess identity.
        var fullNames = world.Entities.Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var entity in world.Entities.Where(e => e.Kind == "character" && e.Name.Contains(' ')))
        {
            var shortName = entity.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            if (fullNames.Contains(shortName) || ambiguousNames.Contains(shortName)) continue;
            if (aliases.TryGetValue(shortName, out var existing) && existing != entity.Id) { aliases.Remove(shortName); ambiguousNames.Add(shortName); }
            else aliases[shortName] = entity.Id;
        }
        // A named knower or relationship endpoint establishes identity, but not a biography or location.
        var namedCharacters = claims.SelectMany(c => c.KnownBy)
            .Concat(claims.Where(c => c.Category == "knowledge").Select(c => c.Subject))
            .Concat(claims.Where(c => Canonical(c) && c.Category == "relationship").SelectMany(c => new[] { c.Subject, c.Target }));
        foreach (var name in namedCharacters.Distinct(StringComparer.OrdinalIgnoreCase))
            if (!name.Equals("player", StringComparison.OrdinalIgnoreCase) && !name.Equals("director", StringComparison.OrdinalIgnoreCase) && Resolve(name) is null)
                Upsert("character:" + name, name, "character", "Identity referenced in reviewed evidence; biography not established.", [], false);
        foreach (var claim in claims.Where(c => Canonical(c) && !string.IsNullOrWhiteSpace(c.Actor) && !c.Actor!.Contains(':')))
            if (Resolve(claim.Actor) is null)
                Upsert("character:" + claim.Actor, claim.Actor!, "character", "Actor identified in a reviewed occurrence; biography not established.",
                    claim.Visibility == "public" ? null : claim.KnownBy.Select(Resolve).OfType<string>().ToArray(), false);
        var sceneId = Resolve(state.Location) ?? Upsert("place:" + state.Location, state.Location, "place", "Reviewed resume location.", null, true);
        var player = world.Entities.FindIndex(x => x.Id == "player");
        if (player < 0) world.Entities.Add(new("player", resume.PlayerCharacterName ?? "Player", "character", sceneId, "The player-controlled character."));
        else world.Entities[player] = world.Entities[player] with { LocationId = sceneId, Name = resume.PlayerCharacterName ?? world.Entities[player].Name };
        var playerIndex = world.Entities.FindIndex(x => x.Id == "player");
        if (claims.Any(c => c.Category is "character" or "entity" && c.Subject.Equals(world.Entities[playerIndex].Name, StringComparison.OrdinalIgnoreCase) && c.Visibility != "public"))
            world.Entities[playerIndex] = world.Entities[playerIndex] with { Description = "The player-controlled character.", KnownTo = null };
        aliases["player"] = "player";
        aliases[world.Entities.Single(x => x.Id == "player").Name] = "player";
        if (!string.IsNullOrWhiteSpace(resume.PlayerCharacterName)) aliases[resume.PlayerCharacterName] = "player";
        world.PlayerLocationId = sceneId;
        foreach (var name in state.Participants)
        {
            if (name.Equals(resume.PlayerCharacterName, StringComparison.OrdinalIgnoreCase)) continue;
            var id = Resolve(name) ?? Upsert("character:" + name, name, "character", "Listed in the reviewed resume scene.", null, false);
            var index = world.Entities.FindIndex(x => x.Id == id);
            world.Entities[index] = world.Entities[index] with { LocationId = sceneId };
        }
        foreach (var claim in claims.Where(c => Canonical(c) && c.Category == "inventory" && c.Disposition == "current" && c.Amount > 0))
        {
            var id = Resolve(claim.Subject) ?? Upsert(claim.Key, claim.Subject, "object", claim.Text, null, true);
            var index = world.Entities.FindIndex(x => x.Id == id);
            world.Entities[index] = world.Entities[index] with { LocationId = sceneId };
        }
        // The reviewed ending is itself cited evidence. Items need not have a
        // redundant separate claim to become objects in the live world.
        foreach (var item in state.Inventory.Where(x => x.Value > 0))
        {
            var id = Resolve(item.Key) ?? Upsert("inventory:" + item.Key, item.Key, "object", $"Reviewed ending inventory: {item.Value}.", null, true);
            var index = world.Entities.FindIndex(x => x.Id == id);
            if (world.Entities[index].Kind != "object") throw new InvalidOperationException($"Inventory item {item.Key} conflicts with another entity type. Correct the reviewed identity before approval.");
            world.Entities[index] = world.Entities[index] with { LocationId = sceneId };
        }
        foreach (var claim in claims.Where(c => Canonical(c) && c.Category is "character" or "entity"))
        {
            var id = Resolve(claim.Key)!;
            var index = world.Entities.FindIndex(x => x.Id == id);
            var knowers = claim.KnownBy.Select(Resolve).OfType<string>().Distinct().ToArray();
            var entity = world.Entities[index];
            world.Entities[index] = entity with { KnownTo = entity.Id == "player" || claim.Visibility == "public" ? null : knowers };
            if (claim.Disposition == "current" && !string.IsNullOrWhiteSpace(claim.Location))
            {
                var location = Resolve(claim.Location);
                if (location is null || !world.Entities.Any(x => x.Id == location && x.Kind == "place"))
                    throw new InvalidOperationException($"{claim.Key} references an unresolved location. Edit or exclude it first.");
                world.Entities[index] = world.Entities[index] with { LocationId = location };
            }
        }
        // Absence of a recorded goal/emotion is explicit; no stock NPC motive is invented.
        foreach (var entity in world.Entities.Where(e => e.Id != "player" && e.Kind is "character" or "being"))
        {
            var claim = claims.LastOrDefault(c => Canonical(c) && c.Disposition == "current" && Resolve(c.Key) == entity.Id);
            var old = world.Characters.SingleOrDefault(c => c.EntityId == entity.Id);
            var character = old ?? new CharacterState(entity.Id, Unknown, Unknown, Unknown, Unknown, "", new());
            if (claim is { Category: "character" } && !string.IsNullOrWhiteSpace(claim.Value)) character = character with { Goal = claim.Value };
            if (old is null) world.Characters.Add(character); else world.Characters[world.Characters.IndexOf(old)] = character;
        }
        world.Characters.RemoveAll(x => x.EntityId == "player");
        SourceEvidence[] Evidence(int[] ordinals, SourceRange[]? ranges = null)
        {
            return ordinals.Distinct().SelectMany(ordinal =>
            {
                var block = blocks[ordinal];
                var selected = (ranges ?? []).Where(r => r.Start >= block.Start && (long)r.Start + r.Length <= (long)block.Start + block.Length).ToArray();
                return (selected.Length == 0 ? [new SourceRange(block.Start, block.Length > 0 ? block.Length : block.Text.Length)] : selected)
                    .Select(r => new SourceEvidence(job.SourceId, job.Id, block.Id, ordinal, r.Start, r.Start + r.Length));
            }).ToArray();
        }
        var eventLookup = world.Events.ToDictionary(e => e.Id);
        var memoryIds = world.Memories.Select(m => m.Id).ToHashSet();
        var connectionIds = world.Connections.Select(c => c.Id).ToHashSet();
        void Memory(string owner, string term, string kind, string text, WorldEvent ev)
        {
            var id = Id("memory", owner + ":" + term + ":" + kind + ":" + ev.Id + ":" + text);
            if (memoryIds.Add(id)) world.Memories.Add(new(id, owner, term, kind, text, false, [ev.Id]));
        }
        void Link(string source, string target, string kind, WorldEvent evidence)
        {
            var id = Id("connection", source + ":" + target + ":" + kind + ":" + evidence.Id);
            if (connectionIds.Add(id)) world.Connections.Add(new(id, source, target, kind, evidence.Id));
        }
        foreach (var claim in claims.OrderBy(c => c.EvidenceOrdinals.Min()).ThenBy(c => c.Key, StringComparer.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(claim.Actor) && Resolve(claim.Actor) is null)
                throw new InvalidOperationException($"{claim.Key} references an unresolved actor. Edit or exclude it first.");
            if (!string.IsNullOrWhiteSpace(claim.Location) && !world.Entities.Any(e => e.Id == Resolve(claim.Location) && e.Kind == "place"))
                throw new InvalidOperationException($"{claim.Key} references an unresolved place. Edit or exclude it first.");
            if ((claim.RelatedEntityKeys ?? []).Any(reference => Resolve(reference) is null))
                throw new InvalidOperationException($"{claim.Key} references an unresolved related entity. Edit or exclude it first.");
            var knowers = claim.KnownBy.Select(name => Resolve(name) ?? throw new InvalidOperationException($"{claim.Key} names an unregistered knower: {name}. Edit or exclude it first.")).ToHashSet();
            if (claim.Disposition == "current" && claim.Category is "character" or "entity" && Resolve(claim.Key) == "player") knowers.Add("player");
            if (claim.Category == "knowledge") knowers.Add(Resolve(claim.Subject) ?? throw new InvalidOperationException($"{claim.Key} references an unresolved character."));
            if (claim.Disposition == "current" && claim.Category is "inventory" or "skill" or "condition") knowers.Add("player");
            var entities = new[] { Resolve(claim.Key), Resolve(claim.Subject), Resolve(claim.Target), Resolve(claim.Actor), Resolve(claim.Location) }
                .Concat((claim.RelatedEntityKeys ?? []).Select(Resolve)).OfType<string>().Distinct().ToArray();
            var locationId = Resolve(claim.Location) ?? "";
            var eventId = Id("source-event", job.SourceId + ":" + claim.Key + ":" + claim.Text);
            var ev = eventLookup.GetValueOrDefault(eventId);
            if (ev is null)
            {
                ev = new(eventId, world.Events.Count, 0, Resolve(claim.Actor) ?? "director", locationId, claim.Text,
                    knowers.ToArray(), entities, blocks[claim.EvidenceOrdinals.Min()].Timestamp ?? "Imported history",
                    "imported-" + claim.Category + "-" + claim.Kind + (claim.Disposition == "historical" ? "-historical" : ""), Evidence(claim.EvidenceOrdinals, claim.EvidenceRanges));
                world.Events.Add(ev);
                eventLookup[eventId] = ev;
            }
            var memoryKind = claim.Kind is "belief" or "rumor" ? claim.Kind : "fact";
            var rememberedText = claim.Disposition == "historical" ? "[Historical source record] " + claim.Text : claim.Text;
            var directorText = claim.Category == "knowledge" ? $"{claim.Subject} [{claim.Kind}]: {rememberedText}" : rememberedText;
            Memory("director", "long", memoryKind, directorText, ev);
            foreach (var owner in knowers) Memory(owner, "long", memoryKind, rememberedText, ev);
            if (Canonical(claim) && claim.Category is "relationship" or "connection")
            {
                var source = Resolve(claim.Subject); var target = Resolve(claim.Target);
                if (source is null || target is null) throw new InvalidOperationException($"{claim.Key} references unresolved graph endpoints. Edit or exclude it first.");
                Link(source, target, claim.Value, ev);
            }
            if (Canonical(claim) && claim.Disposition == "current" && claim.Category == "inventory" && claim.Amount > 0) Link("player", Resolve(claim.Subject)!, "carries", ev);
            if (Canonical(claim) && claim.Disposition == "current" && claim.Category is "character" or "entity" && locationId != "") Link(Resolve(claim.Key)!, locationId, "located at", ev);
            if (claim.Category == "knowledge" && claim.Disposition == "current")
            {
                var index = world.Characters.FindIndex(c => c.EntityId == Resolve(claim.Subject));
                if (index >= 0 && claim.Kind is "belief" or "rumor") world.Characters[index] = world.Characters[index] with { Belief = claim.Text };
            }
        }
        var resumeEvent = new WorldEvent(Id("resume", job.Id.ToString()), world.Events.Count, 0, "director", sceneId,
            $"Reviewed resume scene: {state.Location}. Latest player action: {resume.LatestAction}. Objective: {state.Objective}",
            ["player"],
            state.Participants.Select(Resolve).OfType<string>().Append("player").Append(sceneId).Distinct().ToArray(), state.Time,
            "imported-resume", Evidence(resume.EvidenceOrdinals));
        world.Events.Add(resumeEvent);
        foreach (var item in state.Inventory.Where(x => x.Value > 0))
        {
            Link("player", Resolve(item.Key)!, "carries", resumeEvent);
            foreach (var owner in new[] { "player", "director" }) Memory(owner, "long", "fact", $"Ending inventory: {item.Key} × {item.Value}.", resumeEvent);
        }
        // Only final-scene context is short-term; historical public events do not imply attendance.
        foreach (var owner in state.Participants.Select(Resolve).OfType<string>().Append("player").Append("director").Distinct()) Memory(owner, "short", "fact", $"Current scene: {state.Location}; time: {state.Time}.", resumeEvent);
        Memory("director", "short", "fact", resumeEvent.Text, resumeEvent);
        Memory("player", "short", "fact", resumeEvent.Text, resumeEvent);
        var expired = world.Memories.Where(x => x.Term == "short" && !x.Pinned).GroupBy(x => x.OwnerId)
            .SelectMany(group => group.Reverse().Skip(24)).Select(x => x.Id).ToHashSet();
        world.Memories.RemoveAll(x => expired.Contains(x.Id));
        return world;
    }
    static string Id(string prefix, string key) => prefix + "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant()[..24];
}
