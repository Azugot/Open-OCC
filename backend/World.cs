namespace Story;

public record EngineSettings(string CharacterProfile = "fixture", string DirectorProfile = "fixture", int MaxBeats = 3, int MaxNpcs = 6)
{
    public void Validate()
    {
        if (MaxBeats is < 1 or > 5 || MaxNpcs is < 1 or > 10 || string.IsNullOrWhiteSpace(CharacterProfile) || string.IsNullOrWhiteSpace(DirectorProfile))
            throw new InvalidOperationException("Choose two profiles, 1–5 beats and 1–10 NPCs.");
    }
}
public record WorldEntity(string Id, string Name, string Kind, string LocationId, string Description, string[]? KnownTo = null);
public record CharacterState(string EntityId, string Goal, string Belief, string Emotion, string Intention, string DecisionSummary, Dictionary<string, int> Stats);
public record WorldEvent(string Id, int Order, int Beat, string ActorId, string LocationId, string Text, string[] PerceivedBy, string[] EntityIds, string Time, string Kind);
public record Connection(string Id, string SourceId, string TargetId, string Kind, string EvidenceEventId);
public record MemoryEntry(string Id, string OwnerId, string Term, string Kind, string Text, bool Pinned, string[] EvidenceEventIds);
public record Modifier(string Label, int Value);
public record DiceCheck(string Id, string ActorId, string Attempt, int Difficulty, Modifier[] Modifiers, int Roll, int Total, bool Success, string Explanation);

// Entire immutable world version lives alongside its checkpoint. Forks copy only past versions.
public sealed class StoryWorld
{
    public List<WorldEntity> Entities { get; set; } = [];
    public List<CharacterState> Characters { get; set; } = [];
    public List<WorldEvent> Events { get; set; } = [];
    public List<Connection> Connections { get; set; } = [];
    public List<MemoryEntry> Memories { get; set; } = [];
    public List<DiceCheck> Checks { get; set; } = [];
    public EngineSettings Settings { get; set; } = new();
    public int TimeMinutes { get; set; }
    public string PlayerLocationId { get; set; } = "hall";

    public static StoryWorld From(Checkpoint checkpoint) => checkpoint.WorldJson == "{}"
        ? FromState(Json.Read<StoryState>(checkpoint.StateJson)) : Json.Read<StoryWorld>(checkpoint.WorldJson);

    public static StoryWorld FromState(StoryState state)
    {
        var world = new StoryWorld();
        world.Entities.Add(new("hall", state.Location, "place", "hall", "The current scene."));
        world.Entities.Add(new("player", "Player", "character", "hall", "Your character in this story."));
        foreach (var name in state.Participants)
        {
            var id = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name))).ToLowerInvariant()[..24];
            world.Entities.Add(new(id, name, "character", "hall", "A participant in this scene."));
            world.Characters.Add(new(id, "Pursue personal interests", "The present scene is unfolding", "attentive", "Observe", "", new()));
        }
        return world;
    }

    // Narrator-only continuity can advance scenes while retaining established graph and memory history.
    public void SynchronizeScene(StoryState state)
    {
        var place = Entities.FirstOrDefault(x => x.Kind == "place" && x.Name == state.Location);
        if (place is null)
        {
            place = new("place-" + StableId(state.Location), state.Location, "place", "", "Established current scene.");
            place = place with { LocationId = place.Id };
            Entities.Add(place);
        }
        PlayerLocationId = place.Id;
        var player = Entities.FindIndex(x => x.Id == "player");
        if (player >= 0) Entities[player] = Entities[player] with { LocationId = place.Id };
        foreach (var name in state.Participants.Distinct())
        {
            var index = Entities.FindIndex(x => x.Kind == "character" && x.Id != "player" && x.Name == name);
            if (index >= 0) Entities[index] = Entities[index] with { LocationId = place.Id };
            else
            {
                var id = StableId(name);
                Entities.Add(new(id, name, "character", place.Id, "A participant in this scene."));
                Characters.Add(new(id, "Pursue personal interests", "The present scene is unfolding", "attentive", "Observe", "", new()));
            }
        }
    }

    private static string StableId(string name) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name))).ToLowerInvariant()[..24];

    public static StoryWorld Crownspire()
    {
        var w = new StoryWorld
        {
            Entities = [
                new("hall", StoryState.Synthetic.Location, "place", "hall", "Forms, a notice board and a guarded academy door."),
                new("courtyard", "Academy courtyard", "place", "courtyard", "Training paths beyond the hall."),
                new("player", "Player", "character", "hall", "Your character in this story."),
                new("lyra", "Lyra Fen", "character", "hall", "A fellow student carrying a practice sword."),
                new("clerk", "Clerk Orin", "character", "hall", "A meticulous registrar sorting applications."),
                new("guard", "Guard Sera", "character", "hall", "An academy guard watching the sealed door."),
                new("keeper", "Keeper Vale", "character", "courtyard", "A groundskeeper tending the courtyard."),
                new("sword", "Practice sword", "object", "hall", "Lyra's worn training sword."),
                new("door", "Sealed academy door", "object", "hall", "Requires permission from the registrar."),
                new("fox", "Silver fox", "being", "courtyard", "A curious creature watching the paths.")
            ],
            Characters = [
                new("lyra", "Enroll and find a trustworthy friend", "The academy is a chance to start again", "hopeful", "Help a fellow applicant", "", new() { ["Agility"] = 3 }),
                new("clerk", "Register eligible students", "Rules prevent dangerous admissions", "busy", "Check the next application", "", new() { ["Insight"] = 2 }),
                new("guard", "Keep the sealed wing secure", "Unapproved visitors must wait", "watchful", "Guard the door", "", new() { ["Strength"] = 4 }),
                new("keeper", "Keep the fox away from the gate", "The fox may be looking for someone", "curious", "Inspect its tracks", "", new() { ["Awareness"] = 2 }),
                new("fox", "Find food and a safe path", "The keeper is safe", "alert", "Watch the courtyard", "", new() { ["Agility"] = 4 })
            ]
        };
        var opening = new WorldEvent("opening", 0, 0, "director", "hall", "Lyra waits near Orin's forms while Sera guards the sealed door.", ["player", "lyra", "clerk", "guard"], ["hall", "lyra", "clerk", "guard", "door", "sword"], "0 minutes", "scene");
        var hidden = new WorldEvent("secret", 1, 0, "lyra", "hall", "Lyra secretly carries a letter from an expelled mentor.", ["lyra"], ["lyra"], "0 minutes", "private");
        var outside = new WorldEvent("outside", 2, 0, "keeper", "courtyard", "Vale observes the silver fox beside the courtyard path.", ["keeper", "fox"], ["keeper", "fox", "courtyard"], "0 minutes", "scene");
        w.Events.AddRange([opening, hidden, outside]);
        w.Connections.AddRange([
            new("path", "hall", "courtyard", "connects to", "opening"),
            new("owns", "lyra", "sword", "carries", "opening"),
            new("guards", "guard", "door", "guards", "opening"),
            new("watches", "keeper", "fox", "observes", "outside")
        ]);
        w.Memories.AddRange([
            new("lyra-secret", "lyra", "long", "fact", hidden.Text, true, [hidden.Id]),
            new("director-secret", "director", "long", "fact", hidden.Text, true, [hidden.Id]),
            new("guard-belief", "guard", "long", "belief", "All applicants may be trying to enter the sealed wing.", false, [opening.Id])
        ]);
        foreach (var e in w.Events)
            foreach (var owner in e.PerceivedBy.Append("director").Distinct())
                w.Memories.Add(new(Guid.NewGuid().ToString("N"), owner, "short", "fact", e.Text, false, [e.Id]));
        return w;
    }
}

public record Attempt(string Text, bool Uncertain, int Difficulty, Modifier[] Modifiers, string Explanation);
public record TurnPlan(string[] ActorIds, int AdvanceMinutes, Attempt PlayerAttempt);
public record CharacterProposal(string Speech, string Action, string Visibility, string[] Recipients, string Goal, string Belief, string Emotion, string Intention, string DecisionSummary);
public record ConnectionChange(string SourceId, string TargetId, string Kind);
public record MemorySuggestion(string Term, string Kind, string Text);
public record Resolution(string Text, string Visibility, string[] Recipients, string[] EntityIds, string? MoveToId,
    ConnectionChange[] Connections, WorldEntity[] NewEntities, MemorySuggestion[] Memories, bool NeedsPlayerDecision);
public record NarrativeReply(string Narrative);
public record DraftStep(string Key, string Task, string ResultJson, DateTime CompletedAt);
public sealed class TurnDraft
{
    public StoryWorld World { get; set; } = new();
    public List<DraftStep> Steps { get; set; } = [];
    public Dictionary<string, DiceCheck> Rolls { get; set; } = new();
    public HashSet<string> Applied { get; set; } = [];
    public StoryState State { get; set; } = StoryState.Empty;
    public string? Narrative { get; set; }
    public bool Handoff { get; set; }
    public string[]? OffscreenActors { get; set; }
}
