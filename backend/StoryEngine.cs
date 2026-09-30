using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;

namespace Story;

public sealed class FixtureAgentProvider : IAgentProvider
{
    public async Task<T> Generate<T>(AgentRequest request, CancellationToken ct)
    {
        await Task.Delay(80, ct);
        return Json.Read<T>(Json.Write(request.Example));
    }
}

public static class StoryEngine
{
    // This deployment has one backend instance. Locks reject overlapping executions; DB revision checks protect commits.
    private static readonly ConcurrentDictionary<Guid, byte> Executing = new();
    private static readonly ConcurrentDictionary<Guid, CancellationTokenSource> ActiveRuns = new();
    public static bool IsExecuting(Guid branchId) => Executing.ContainsKey(branchId);
    public static async Task Recover(StoryDb db, CancellationToken ct = default)
    {
        foreach (var run in await db.GenerationRuns.Where(x => x.Status == "running").ToListAsync(ct))
        {
            run.Status = "paused";
            run.Error = "Application restarted. Retry to resume saved steps and rolls; no partial turn was committed.";
        }
        await db.SaveChangesAsync(ct);
    }
    public static void Cancel(Guid runId)
    {
        if (ActiveRuns.TryGetValue(runId, out var source))
            try { source.Cancel(); } catch (ObjectDisposedException) { /* Execution already finished. */ }
    }

    public static DiceCheck Roll(string id, string actor, Attempt attempt, Func<int>? dice = null)
    {
        ValidateAttempt(attempt);
        var roll = dice?.Invoke() ?? RandomNumberGenerator.GetInt32(1, 21);
        if (roll is < 1 or > 20) throw new InvalidOperationException("Invalid d20 result.");
        var total = roll + attempt.Modifiers.Sum(x => x.Value);
        return new(id, actor, attempt.Text, attempt.Difficulty, attempt.Modifiers, roll, total, total >= attempt.Difficulty, attempt.Explanation);
    }

    public static void ValidateAttempt(Attempt a)
    {
        if (a is null || a.Modifiers is null || a.Modifiers.Length > 12 || a.Difficulty is < 1 or > 40 ||
            a.Modifiers.Any(x => x is null || x.Value is < -20 or > 20 || string.IsNullOrWhiteSpace(x.Label) || x.Label.Length > 200))
            throw new InvalidOperationException("Invalid action difficulty or modifiers.");
        Text(a.Text, 2000); Text(a.Explanation, 2000);
    }

    public static object CharacterContext(StoryWorld w, string actorId, string[] publicFacts, string[]? privateKnowledge = null)
    {
        var actor = w.Entities.Single(x => x.Id == actorId);
        var events = w.Events.Where(x => x.PerceivedBy.Contains(actorId)).TakeLast(24).ToArray();
        var known = events.SelectMany(x => x.EntityIds).ToHashSet();
        var scene = w.Entities.Where(x => ((x.LocationId == actor.LocationId && (x.KnownTo is null || x.KnownTo.Contains(actorId))) || known.Contains(x.Id) || x.Id == actor.LocationId || x.Id == actorId)).Take(40)
            .Select(x => x.LocationId == actor.LocationId || x.Id == actor.LocationId ? x : x with { LocationId = "", Description = "Previously encountered; current whereabouts unknown." }).ToArray();
        return new { actor, character = w.Characters.Single(x => x.EntityId == actorId), entities = scene, recentEvents = events,
            memories = RetrieveMemories(w, actorId, string.Join(" ", events.TakeLast(3).Select(x => x.Text))),
            commonFacts = publicFacts.Take(20).Select(x => x[..Math.Min(x.Length, 1000)]),
            privateKnowledge = (privateKnowledge ?? []).Take(20).Select(x => x[..Math.Min(x.Length, 1000)]), timeMinutes = w.TimeMinutes };
    }

    public static string[] PerceivedFacts(IEnumerable<Fact> facts, string actorName) => facts.Where(f =>
    {
        var owners = Json.Read<string[]>(f.KnownByJson);
        return owners.Contains(actorName, StringComparer.OrdinalIgnoreCase) ||
            (owners.Length == 0 && f.Visibility == "public" && f.Kind is not ("secret" or "belief"));
    }).Select(f => $"[{f.Kind}; confidence {f.Confidence}] {f.Text}").ToArray();

    public static MemoryEntry[] RetrieveMemories(StoryWorld w, string owner, string query)
    {
        var words = query.Split([' ', ',', '.', '\n'], StringSplitOptions.RemoveEmptyEntries).Where(x => x.Length > 3).Take(50).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var own = w.Memories.Where(x => x.OwnerId == owner).ToArray();
        var chosen = own.Where(x => x.Pinned).Take(20)
            .Concat(own.Where(x => !x.Pinned && x.Term == "short").TakeLast(16))
            .Concat(own.Where(x => !x.Pinned && x.Term == "long").OrderByDescending(x => words.Count(word => x.Text.Contains(word, StringComparison.OrdinalIgnoreCase))).Take(16));
        var budget = 32000;
        return chosen.Where(x => { budget -= x.Text.Length; return budget >= 0; }).ToArray();
    }

    private static object DirectorContext(StoryWorld w, string[] facts, StoryState? playerState = null, object? canon = null) => new
    {
        entities = w.Entities.Take(80).Select(x => x with { Description = x.Description[..Math.Min(x.Description.Length, 300)] }),
        characters = w.Characters.Take(30), w.PlayerLocationId, w.TimeMinutes, playerState,
        recentEvents = w.Events.TakeLast(24), memories = RetrieveMemories(w, "director", string.Join(" ", w.Events.TakeLast(5).Select(x => x.Text))),
        precedents = w.Checks.TakeLast(12), facts = facts.Take(40).Select(x => x[..Math.Min(x.Length, 1000)]), canon
    };

    public static async Task Execute(StoryDb db, GenerationRun run, IConfiguration config, CancellationToken ct,
        IAgentProvider? characterOverride = null, IAgentProvider? directorOverride = null)
    {
        if (!Executing.TryAdd(run.BranchId, 0)) throw new InvalidOperationException("A turn is already executing on this branch.");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ct = cancellation.Token;
        ActiveRuns.TryAdd(run.Id, cancellation);
        try
        {
            if (run.Status is not ("running" or "paused")) throw new InvalidOperationException("Only active or paused turns can execute.");
            var branch = await db.Branches.SingleAsync(x => x.Id == run.BranchId, ct);
            if (branch.HeadCheckpointId != run.ExpectedCheckpointId) throw new DbUpdateConcurrencyException();
            var cp = await db.Checkpoints.SingleAsync(x => x.Id == run.ExpectedCheckpointId, ct);
            var draft = run.DraftJson == "{}" ? new TurnDraft { World = StoryWorld.From(cp), State = Json.Read<StoryState>(cp.StateJson) } : Json.Read<TurnDraft>(run.DraftJson);
            var w = draft.World;
            w.Settings.Validate();
            run.Status = "running"; run.Error = null;
            await Save();
            var character = characterOverride ?? await Provider(w.Settings.CharacterProfile);
            var director = directorOverride ?? await Provider(w.Settings.DirectorProfile);
            var facts = await db.Facts.Where(x => x.BranchId == run.BranchId && x.ReviewStatus == "accepted" && x.EffectiveSequence <= cp.Sequence)
                .OrderByDescending(x => x.EffectiveSequence).ToListAsync(ct);
            var allFacts = facts.Select(x => $"[{x.Kind}; confidence {x.Confidence}; known by {x.KnownByJson}] {x.Text}").ToArray();
            var campaign = await db.Campaigns.SingleAsync(x => x.Id == branch.CampaignId, ct);
            var version = campaign.WorldVersionId is { } versionId ? await db.WorldVersions.SingleAsync(x => x.Id == versionId, ct) : null;
            var people = await db.Characters.Where(x => x.CampaignId == branch.CampaignId).ToListAsync(ct);
            var knowledge = await db.Knowledge.Where(x => x.BranchId == branch.Id && x.EffectiveSequence <= cp.Sequence).ToListAsync(ct);
            var threads = await db.NarrativeThreads.Where(x => x.BranchId == branch.Id && x.EffectiveSequence <= cp.Sequence && x.Status == "active")
                .OrderByDescending(x => x.Importance).Take(20).Select(x => new ContextThread(x.Title, x.Details, x.Kind, x.Importance)).ToArrayAsync(ct);
            var canon = new { worldVersion = version, activeThreads = threads };

            var advance = System.Text.RegularExpressions.Regex.IsMatch(run.Action, "\\b(wait|rest|sleep|advance)\\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ? 10 : 0;
            var selected = w.Characters.Where(c => advance > 0 || w.Entities.Single(e => e.Id == c.EntityId).LocationId == w.PlayerLocationId).Take(w.Settings.MaxNpcs).Select(x => x.EntityId).ToArray();
            var uncertain = System.Text.RegularExpressions.Regex.IsMatch(run.Action, "\\b(force|attack|persuade|convince|break|sneak)\\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var plan = await Step("plan", "plan", director, new { action = run.Action, world = DirectorContext(w, allFacts, draft.State, canon), limits = w.Settings },
                new TurnPlan(selected, advance, new(run.Action, uncertain, 14, [], uncertain ? "A contested attempt against academy precautions." : "An ordinary action needs no roll.")),
                p => {
                    if (p.ActorIds is null || p.ActorIds.Length > w.Settings.MaxNpcs || p.ActorIds.Distinct().Count() != p.ActorIds.Length || p.AdvanceMinutes is < 0 or > 1440 ||
                        p.ActorIds.Any(id => !w.Characters.Any(x => x.EntityId == id) || (p.AdvanceMinutes == 0 && w.Entities.Single(x => x.Id == id).LocationId != w.PlayerLocationId)))
                        throw new InvalidOperationException("Director selected invalid actors or time advancement.");
                    ValidateAttempt(p.PlayerAttempt);
                });
            if (draft.Applied.Add("clock")) { w.TimeMinutes += plan.AdvanceMinutes; await Save(); }
            var playerCheck = await Check("player-check", "player", plan.PlayerAttempt);
            var playerResult = await Step("player-result", "resolve", director,
                new { actorId = "player", action = run.Action, attempt = plan.PlayerAttempt, check = playerCheck, world = DirectorContext(w, allFacts, draft.State, canon) },
                new Resolution($"You attempt: {run.Action}" + (playerCheck is null ? "" : playerCheck.Success ? " The attempt succeeds." : " The attempt fails."),
                    "scene", [], [], null, [], [], [], false), r => { ValidateResolution(w, "player", r); ValidateEffects(r, playerCheck); });
            if (draft.Applied.Add("player-result")) { Apply(w, "player", 0, playerResult, playerCheck); await Save(); }

            draft.Handoff |= playerResult.NeedsPlayerDecision;
            if (draft.OffscreenActors is null)
            {
                draft.OffscreenActors = plan.ActorIds.Where(id => w.Entities.Single(x => x.Id == id).LocationId != w.PlayerLocationId).ToArray();
                await Save();
            }
            for (var beat = 1; beat <= w.Settings.MaxBeats; beat++)
            {
                foreach (var actorId in plan.ActorIds)
                {
                    var entity = w.Entities.Single(x => x.Id == actorId);
                    var key = $"beat-{beat}-{actorId}";
                    if (draft.Applied.Contains(key + "-result")) continue;
                    var offscreen = draft.OffscreenActors.Contains(actorId);
                    if (draft.Handoff && !offscreen) continue;
                    // Off-screen actors update once per story time advance, not on each scene beat.
                    if (offscreen && (plan.AdvanceMinutes == 0 || beat > 1)) continue;
                    if (!offscreen && entity.LocationId != w.PlayerLocationId) continue;
                    var current = w.Characters.Single(x => x.EntityId == actorId);
                    var person = people.SingleOrDefault(x => x.Name.Equals(entity.Name, StringComparison.OrdinalIgnoreCase));
                    var ownKnowledge = knowledge.Where(x => x.CharacterId == person?.Id).Select(x => $"[{x.BeliefType}; confidence {x.Confidence}] {x.Subject}").ToArray();
                    var proposal = await Step(key + "-proposal", "character", character, CharacterContext(w, actorId, PerceivedFacts(facts, entity.Name), ownKnowledge),
                        new CharacterProposal(entity.Kind == "being" ? "" : actorId == "clerk" ? "Please present your application." : actorId == "guard" ? "The sealed wing requires permission." : "What would you like to do next?",
                            current.Intention, "scene", [], current.Goal, current.Belief, current.Emotion, current.Intention,
                            "Respond to the observed situation while pursuing my goal."), p => ValidateProposal(w, actorId, p));
                    var attempt = await Step(key + "-attempt", "assess", director, new { actorId, proposal, world = DirectorContext(w, allFacts, draft.State, canon) },
                        new Attempt(proposal.Action, false, 10, [], "Routine behavior in the current scene."), ValidateAttempt);
                    var check = await Check(key + "-check", actorId, attempt);
                    var result = await Step(key + "-result", "resolve", director, new { actorId, proposal, attempt, check, world = DirectorContext(w, allFacts, draft.State, canon) },
                        new Resolution($"{entity.Name}: {proposal.Action}." + (string.IsNullOrEmpty(proposal.Speech) ? "" : $" “{proposal.Speech}”"),
                            proposal.Visibility, proposal.Recipients, [entity.Id, entity.LocationId], null, [], [],
                            [new("long", "belief", proposal.Belief)], entity.LocationId == w.PlayerLocationId && beat == 1 && actorId == plan.ActorIds.LastOrDefault(id => w.Entities.Single(x => x.Id == id).LocationId == w.PlayerLocationId)),
                        r => {
                            ValidateResolution(w, actorId, r);
                            ValidateEffects(r, check);
                            if (proposal.Visibility == "private" && r.Visibility != "private") throw new InvalidOperationException("A private proposal cannot be broadcast without discovery.");
                            if (proposal.Visibility == "direct" && (r.Visibility == "scene" || r.Recipients.Except(proposal.Recipients).Any())) throw new InvalidOperationException("A directed proposal cannot gain extra recipients.");
                        });
                    if (draft.Applied.Add(key + "-result"))
                    {
                        w.Characters[w.Characters.FindIndex(x => x.EntityId == actorId)] = current with { Goal = proposal.Goal, Belief = proposal.Belief, Emotion = proposal.Emotion, Intention = proposal.Intention, DecisionSummary = proposal.DecisionSummary };
                        Apply(w, actorId, beat, result, check);
                        draft.Handoff |= result.NeedsPlayerDecision;
                        await Save();
                    }
                    // Finish scheduled off-screen updates before handing back; stop further on-screen reactions now.
                }
            }
            var newEvents = w.Events.Where(e => !StoryWorld.From(cp).Events.Any(old => old.Id == e.Id) && e.PerceivedBy.Contains("player")).ToArray();
            var narration = await Step("narration", "narrate", director,
                new { action = run.Action, playerEvents = newEvents, state = draft.State, instruction = "Narrate only these observed events. End at the player's next decision. Never expose off-screen activity or private facts." },
                new NarrativeReply("[SIMULATED TURN — deterministic agents, no AI provider used]\n\n" + string.Join("\n\n", newEvents.Select(e => e.Text)) + "\n\nWhat do you do?"), n => Text(n.Narrative, 16000));
            draft.Narrative = narration.Narrative;
            var location = w.Entities.Single(x => x.Id == w.PlayerLocationId);
            draft.State = draft.State with { Location = location.Name, Time = $"{Json.Read<StoryState>(cp.StateJson).Time.Split(" · elapsed")[0]} · elapsed {w.TimeMinutes} minutes",
                Participants = w.Characters.Where(c => w.Entities.Single(e => e.Id == c.EntityId).LocationId == w.PlayerLocationId).Select(c => w.Entities.Single(e => e.Id == c.EntityId).Name).Take(30).ToArray() };
            draft.State.Validate();
            await Save();
            await db.Entry(branch).ReloadAsync(ct);
            await db.Entry(run).ReloadAsync(ct);
            if (run.Status != "running") throw new OperationCanceledException("Turn was cancelled.");
            await CampaignService.CommitTurn(db, run, narration.Narrative, ct, draft.State, w);

            async Task<IAgentProvider> Provider(string id)
            {
                var profile = await db.Providers.SingleOrDefaultAsync(x => x.Id == id, ct);
                if (id == "fixture") return new FixtureAgentProvider();
                if (profile is null || !profile.Enabled) throw new InvalidOperationException("The selected provider is disabled or missing.");
                if (profile.Adapter == "fixture") return new FixtureAgentProvider();
                return AgentProviderFactory.Create(profile, config);
            }
            async Task Save()
            {
                ct.ThrowIfCancellationRequested();
                if (await db.GenerationRuns.AsNoTracking().AnyAsync(x => x.Id == run.Id && x.Status == "cancelled", ct)) throw new OperationCanceledException();
                run.DraftJson = Json.Write(draft);
                await db.SaveChangesAsync(ct);
            }
            async Task<T> Step<T>(string key, string task, IAgentProvider provider, object context, T example, Action<T> validate)
            {
                var existing = draft.Steps.SingleOrDefault(x => x.Key == key);
                if (existing is not null) return Json.Read<T>(existing.ResultJson);
                var value = await provider.Generate<T>(new(task, context, example!, Instructions(task)), ct);
                if (value is null) throw new InvalidOperationException("Model returned an empty result.");
                validate(value);
                draft.Steps.Add(new(key, task, Json.Write(value), DateTime.UtcNow)); await Save();
                return value;
            }
            async Task<DiceCheck?> Check(string key, string actor, Attempt attempt)
            {
                if (!attempt.Uncertain) return null;
                if (!draft.Rolls.TryGetValue(key, out var check))
                {
                    check = Roll(key, actor, attempt); draft.Rolls.Add(key, check); w.Checks.Add(check); await Save();
                }
                return check;
            }
        }
        catch (Exception ex)
        {
            db.ChangeTracker.Clear();
            var saved = await db.GenerationRuns.SingleAsync(x => x.Id == run.Id, CancellationToken.None);
            if (saved.Status != "completed")
            {
                if (saved.Status != "cancelled") saved.Status = ex is OperationCanceledException ? "cancelled" : "paused";
                saved.Error = saved.Status == "cancelled" ? "Cancelled. No partial turn was committed."
                    : ex is DbUpdateConcurrencyException ? "The branch changed. Cancel this draft and start from the new checkpoint."
                    : ex is OperationCanceledException ? "Cancelled. No partial turn was committed." : "Generation paused. Check provider settings and retry; validated steps and rolls are preserved.";
                await db.SaveChangesAsync(CancellationToken.None);
            }
            // Cancellation may win between the pre-save check and EF's concurrency-token update.
            if (saved.Status != "cancelled" && ex is not OperationCanceledException) throw new InvalidOperationException(saved.Error, ex);
        }
        finally { ActiveRuns.TryRemove(run.Id, out _); Executing.TryRemove(run.BranchId, out _); }
    }

    private static void Text(string? value, int max, bool emptyAllowed = false)
    {
        if (value is null || value.Length > max || (!emptyAllowed && string.IsNullOrWhiteSpace(value))) throw new InvalidOperationException("Model text is missing or exceeds its limit.");
    }

    private static string Instructions(string task) => task switch
    {
        "plan" => "You are the master director. Select relevant actorIds within limits, prioritizing the current scene. Select off-screen actors only when advancing story time. Advance minutes only when the player's action warrants it. Assess the player's stated attempt without inventing player decisions. Routine actions need no roll; uncertain or contested attempts use a difficulty 1–40 with named stat/power, action and environment modifiers (-20..20). Use established abilities and relevant precedents. Fix these values before any roll.",
        "character" => "Act as this character with autonomy. Read only your provided perceptions and memories. Do not assume knowledge of unobserved events. Author-edited memories take priority over outdated character summaries. Propose speech and an attempted action pursuing your goal; never declare contested success or choose player actions. Visibility is scene, private (only you), or direct (named present recipients). Keep secrets out of public speech unless you intentionally reveal them. Return short goals, beliefs, emotion, intention and a concise decision summary, no chain of thought.",
        "assess" => "You are the director. Preserve the character's proposed motives and assess the attempt. Resolve routine actions without dice. For uncertain or contested actions, establish difficulty 1–40 and named modifiers for established stats/power, actions and environment (-20..20). modifiers is an array of {label:string,value:integer}. Explain the ruling using relevant precedents. No roll has happened yet.",
        "resolve" => "You are the director resolving the stated attempt. Preserve autonomous character motives; do not choose any further player actions. A supplied check is authoritative: total >= difficulty succeeds; natural 1/20 add flavor only. Never change the roll, modifiers or difficulty. Failed checks cannot move the actor, create entities or establish successful relationships. Narrate the actual outcome. Scene visibility reaches only present observers, private only the actor, direct only named present recipients. Never broadcast secrets or private proposals. MoveToId moves only this actor to an established place. New entities and relationships must be supported by this occurrence. connections is an array of {sourceId:string,targetId:string,kind:string}; newEntities is an array of {id:string,name:string,kind:character|being|object|place,locationId:string,description:string}; descriptions contain observable traits only. memories is an array of {term:short|long,kind:fact|belief|rumor,text:string}. Memory suggestions concern this actor's own beliefs or rumors, not unobserved facts. NeedsPlayerDecision stops scene reactions at a meaningful player choice.",
        "narrate" => "Narrate only the supplied player-observed events and their consequences. Never expose secrets, unobserved occurrences, or choose the player's next actions. End with a meaningful player decision. No extra world changes may be introduced in narration.",
        _ => throw new InvalidOperationException("Unknown agent task.")
    };

    public static string[] Audience(StoryWorld w, string actor, string visibility, string[] recipients)
    {
        var location = actor == "player" ? w.PlayerLocationId : w.Entities.Single(x => x.Id == actor).LocationId;
        var present = w.Characters.Where(c => w.Entities.Single(e => e.Id == c.EntityId).LocationId == location).Select(c => c.EntityId).ToList();
        if (location == w.PlayerLocationId) present.Add("player");
        if (recipients is null || recipients.Length > 10 || recipients.Any(x => !present.Contains(x))) throw new InvalidOperationException("Recipients must be present and able to perceive the event.");
        return visibility switch
        {
            "private" => [actor], "scene" => present.Append(actor).Distinct().ToArray(),
            "direct" => recipients.Append(actor).Distinct().ToArray(),
            _ => throw new InvalidOperationException("Visibility must be scene, private, or direct.")
        };
    }

    private static void ValidateProposal(StoryWorld w, string actor, CharacterProposal p)
    {
        Text(p.Speech, 2000, true); Text(p.Action, 2000); Text(p.Goal, 1000); Text(p.Belief, 1000); Text(p.Emotion, 200); Text(p.Intention, 1000); Text(p.DecisionSummary, 1000);
        Audience(w, actor, p.Visibility, p.Recipients);
    }

    private static void ValidateResolution(StoryWorld w, string actor, Resolution r)
    {
        Text(r.Text, 3000); Audience(w, actor, r.Visibility, r.Recipients);
        if (r.EntityIds is null || r.Connections is null || r.NewEntities is null || r.Memories is null || r.EntityIds.Length > 30 || r.Connections.Length > 12 || r.NewEntities.Length > 8 || r.Memories.Length > 4)
            throw new InvalidOperationException("Invalid outcome arrays.");
        if (w.Entities.Count + r.NewEntities.Length > 500) throw new InvalidOperationException("This slice supports up to 500 world entities.");
        var ids = w.Entities.Select(x => x.Id).ToHashSet();
        foreach (var e in r.NewEntities)
        {
            Text(e.Id, 100); Text(e.Name, 200); Text(e.Description, 1000, true);
            if (e.Kind is not ("character" or "being" or "object" or "place") || !ids.Add(e.Id) || e.Id is "player" or "director") throw new InvalidOperationException("Invalid new entity.");
        }
        bool Place(string id) => w.Entities.Concat(r.NewEntities).Any(x => x.Id == id && x.Kind == "place");
        if (r.NewEntities.Any(x => !Place(x.LocationId)) || r.EntityIds.Any(x => !ids.Contains(x)) || (r.MoveToId is not null && !Place(r.MoveToId))) throw new InvalidOperationException("Unknown entity or destination.");
        foreach (var c in r.Connections) { Text(c.Kind, 100); if (!ids.Contains(c.SourceId) || !ids.Contains(c.TargetId)) throw new InvalidOperationException("Unknown relationship endpoint."); }
        foreach (var m in r.Memories) { Text(m.Text, 1000); if (m.Term is not ("short" or "long") || m.Kind is not ("fact" or "belief" or "rumor")) throw new InvalidOperationException("Invalid memory classification."); }
    }

    private static void Apply(StoryWorld w, string actor, int beat, Resolution r, DiceCheck? check)
    {
        var audience = Audience(w, actor, r.Visibility, r.Recipients);
        var location = actor == "player" ? w.PlayerLocationId : w.Entities.Single(x => x.Id == actor).LocationId;
        // A failed attempt cannot assert state effects, relationships or new world entities.
        ValidateEffects(r, check);
        var ev = new WorldEvent(Guid.NewGuid().ToString("N"), w.Events.Count, beat, actor, location, r.Text, audience,
            r.EntityIds.Concat(r.NewEntities.Select(x => x.Id)).Concat(actor == "director" ? [] : new[] { actor }).Append(location).Distinct().ToArray(), $"{w.TimeMinutes} minutes", r.Visibility);
        w.Events.Add(ev);
        w.Entities.AddRange(r.NewEntities.Select(e => e with { KnownTo = audience }));
        foreach (var entity in r.NewEntities.Where(x => x.Kind is "character" or "being"))
            w.Characters.Add(new(entity.Id, "Explore personal interests", "Recently encountered", "attentive", "Observe", "", new()));
        foreach (var c in r.Connections) w.Connections.Add(new(Guid.NewGuid().ToString("N"), c.SourceId, c.TargetId, c.Kind, ev.Id));
        if (r.MoveToId is not null)
        {
            if (actor == "player")
            {
                w.PlayerLocationId = r.MoveToId;
                var playerIndex = w.Entities.FindIndex(x => x.Id == "player");
                if (playerIndex >= 0) w.Entities[playerIndex] = w.Entities[playerIndex] with { LocationId = r.MoveToId };
            }
            else { var i = w.Entities.FindIndex(x => x.Id == actor); w.Entities[i] = w.Entities[i] with { LocationId = r.MoveToId }; }
            w.Connections.Add(new(Guid.NewGuid().ToString("N"), actor, r.MoveToId, "arrived at", ev.Id));
        }
        foreach (var owner in audience.Append("director").Distinct())
        {
            w.Memories.Add(new(Guid.NewGuid().ToString("N"), owner, "short", "fact", ev.Text[..Math.Min(ev.Text.Length, 1000)], false, [ev.Id]));
            // Preserve a durable episode while the short-term window rolls forward.
            if (!w.Memories.Any(m => m.OwnerId == owner && m.Term == "long" && m.Text == ev.Text))
                w.Memories.Add(new(Guid.NewGuid().ToString("N"), owner, "long", "fact", ev.Text[..Math.Min(ev.Text.Length, 1000)], false, [ev.Id]));
        }
        foreach (var m in r.Memories)
        {
            // Suggested beliefs are private to the actor; director tracks them as beliefs, never world truth.
            foreach (var owner in new[] { actor, "director" })
            {
                var text = owner == "director" ? $"{actor} believes: {m.Text}" : m.Text;
                text = text[..Math.Min(text.Length, 1000)];
                if (!w.Memories.Any(x => x.OwnerId == owner && x.Term == m.Term && x.Text == text))
                    w.Memories.Add(new(Guid.NewGuid().ToString("N"), owner, m.Term, m.Kind == "fact" ? "belief" : m.Kind, text, false, [ev.Id]));
            }
        }
        foreach (var owner in w.Memories.Select(x => x.OwnerId).Distinct().ToArray())
        {
            var expired = w.Memories.Where(x => x.OwnerId == owner && x.Term == "short" && !x.Pinned).Reverse().Skip(24).Select(x => x.Id).ToHashSet();
            w.Memories.RemoveAll(x => expired.Contains(x.Id));
        }
    }

    private static void ValidateEffects(Resolution result, DiceCheck? check)
    {
        if (check is { Success: false } && (result.MoveToId is not null || result.NewEntities.Length > 0 || result.Connections.Length > 0))
            throw new InvalidOperationException("A failed check cannot apply successful action effects.");
    }
}
