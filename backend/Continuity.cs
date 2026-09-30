using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Story;

public record ContextFact(string Text, string Kind, string Visibility, string[] KnownBy, double Confidence);
public record ContextMessage(string Role, string Content);
public record ContextThread(string Title, string Details, string Kind, int Importance);
public record StoryContext(StoryState State, ContextMessage[] RecentMessages, ContextFact[] RelevantFacts, ContextThread[] ActiveThreads, ContextMessage[] RetrievedMessages = null!, string? Summary = null);
public record TransitionFact(string Text, string Kind, string Visibility, string[] KnownBy, double Confidence);
public record TransitionThread(string Title, string Details, string Kind, string Status, int Importance);
public record StateTransition(StoryState? State, TransitionFact[] Facts, TransitionThread[] Threads);
public record ReconstructionFact(string Text, string Kind, string Visibility, string[] KnownBy, double Confidence, int[] EvidenceOrdinals);
public record ReconstructionResult(ReconstructionFact[] Facts, StoryState? ResumeState, TransitionThread[] Threads, string Summary);

public sealed class ContinuityService
{
    private static readonly HashSet<string> FactKinds = ["fact", "rumor", "belief", "secret", "correction"];
    private static readonly HashSet<string> ThreadKinds = ["goal", "promise", "deadline", "mystery", "conflict", "thread"];
    public static readonly JsonElement TransitionSchema = ParseSchema("""
    {"type":"object","additionalProperties":false,"properties":{"state":{"anyOf":[{"type":"null"},{"type":"object","additionalProperties":false,"properties":{"location":{"type":"string"},"time":{"type":"string"},"objective":{"type":"string"},"condition":{"type":"string"},"skills":{"type":"object","additionalProperties":{"type":"string"}},"inventory":{"type":"object","additionalProperties":{"type":"integer"}},"participants":{"type":"array","items":{"type":"string"}}},"required":["location","time","objective","condition","skills","inventory","participants"]}]},"facts":{"type":"array","maxItems":10,"items":{"type":"object","additionalProperties":false,"properties":{"text":{"type":"string"},"kind":{"type":"string","enum":["fact","rumor","belief","secret","correction"]},"visibility":{"type":"string","enum":["public","narrator"]},"knownBy":{"type":"array","items":{"type":"string"}},"confidence":{"type":"number","minimum":0,"maximum":1}},"required":["text","kind","visibility","knownBy","confidence"]}},"threads":{"type":"array","maxItems":10,"items":{"type":"object","additionalProperties":false,"properties":{"title":{"type":"string"},"details":{"type":"string"},"kind":{"type":"string","enum":["goal","promise","deadline","mystery","conflict","thread"]},"status":{"type":"string","enum":["active","resolved"]},"importance":{"type":"integer","minimum":1,"maximum":5}},"required":["title","details","kind","status","importance"]}}},"required":["state","facts","threads"]}
    """);
    public static readonly JsonElement ReconstructionSchema = ParseSchema("""
    {"type":"object","additionalProperties":false,"properties":{"facts":{"type":"array","maxItems":40,"items":{"type":"object","additionalProperties":false,"properties":{"text":{"type":"string"},"kind":{"type":"string","enum":["fact","rumor","belief","secret","correction"]},"visibility":{"type":"string","enum":["public","narrator"]},"knownBy":{"type":"array","items":{"type":"string"}},"confidence":{"type":"number","minimum":0,"maximum":1},"evidenceOrdinals":{"type":"array","minItems":1,"items":{"type":"integer","minimum":0}}},"required":["text","kind","visibility","knownBy","confidence","evidenceOrdinals"]}},"resumeState":{"anyOf":[{"type":"null"},{"type":"object","additionalProperties":false,"properties":{"location":{"type":"string"},"time":{"type":"string"},"objective":{"type":"string"},"condition":{"type":"string"},"skills":{"type":"object","additionalProperties":{"type":"string"}},"inventory":{"type":"object","additionalProperties":{"type":"integer"}},"participants":{"type":"array","items":{"type":"string"}}},"required":["location","time","objective","condition","skills","inventory","participants"]}]},"threads":{"type":"array","maxItems":20,"items":{"type":"object","additionalProperties":false,"properties":{"title":{"type":"string"},"details":{"type":"string"},"kind":{"type":"string","enum":["goal","promise","deadline","mystery","conflict","thread"]},"status":{"type":"string","enum":["active","resolved"]},"importance":{"type":"integer","minimum":1,"maximum":5}},"required":["title","details","kind","status","importance"]}},"summary":{"type":"string"}},"required":["facts","resumeState","threads","summary"]}
    """);

    public async Task<StoryContext> Assemble(StoryDb db, Guid branchId, StoryState state, string action, CancellationToken ct)
    {
        var recentRows = await db.Messages.Where(x => x.BranchId == branchId).OrderByDescending(x => x.Sequence).Take(16).OrderBy(x => x.Sequence).ToArrayAsync(ct);
        var recent = recentRows.Select(x => new ContextMessage(x.Role, x.Content)).ToArray();
        var facts = await db.Facts.Where(x => x.BranchId == branchId && x.ReviewStatus == "accepted").OrderByDescending(x => x.EffectiveSequence).Take(120).ToListAsync(ct);
        var words = Keywords(action + " " + state.Objective + " " + string.Join(' ', state.Participants));
        var relevant = facts.OrderByDescending(x => Score(x.Text, words) + (x.Kind is "promise" or "deadline" ? 10 : 0)).Take(35)
            .Select(x => new ContextFact(x.Text, x.Kind, x.Visibility, SafeKnownBy(x.KnownByJson), x.Confidence)).ToArray();
        var recentStart = recentRows.Length == 0 ? int.MaxValue : recentRows[0].Sequence;
        var historicalRows = await db.Messages.Where(x => x.BranchId == branchId && x.Sequence < recentStart).OrderByDescending(x => x.Sequence).Take(200).ToArrayAsync(ct);
        var retrieved = historicalRows.OrderByDescending(x => Score(x.Content, words)).ThenByDescending(x => x.Sequence).Where(x => Score(x.Content, words) > 0).Take(8).Select(x => new ContextMessage(x.Role, x.Content)).ToArray();
        var threads = await db.NarrativeThreads.Where(x => x.BranchId == branchId && x.Status == "active").OrderByDescending(x => x.Importance).ThenByDescending(x => x.EffectiveSequence).Take(20)
            .Select(x => new ContextThread(x.Title, x.Details, x.Kind, x.Importance)).ToArrayAsync(ct);
        var summary = await db.Summaries.Where(x => x.BranchId == branchId).Select(x => x.Text).SingleOrDefaultAsync(ct);
        return new(state, recent, relevant, threads, retrieved, summary);
    }

    public static ProviderPrompt NarrationPrompt(StoryContext context, string action) => new(
        "You are the narrator and game master of a persistent interactive story. Preserve player agency: never invent the player's actions, speech, thoughts, feelings, or decisions. Continue from the exact current state. Treat rumors and beliefs as uncertain. A fact marked narrator is secret; do not reveal it through an NPC unless that NPC appears in knownBy. Keep promises, deadlines, objectives, inventory, time, location, and character knowledge coherent. Write a vivid scene and stop at a meaningful decision. Do not output JSON or a state panel.",
        "CONTINUITY CONTEXT:\n" + Json.Write(context) + "\n\nPLAYER ACTION:\n" + action, 1800);

    public static string ValidateNarrative(string narrative)
    {
        if (string.IsNullOrWhiteSpace(narrative)) throw new InvalidOperationException("Provider returned no narrative text.");
        if (narrative.Length > 200_000) throw new InvalidOperationException("Provider narrative exceeded the 200,000 character safety limit.");
        if (narrative.Contains("<script", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Provider narrative contained executable markup.");
        return narrative.Trim();
    }

    public async Task<StateTransition> AnalyzeTransition(IStoryProvider provider, StoryContext context, string action, string narrative, CancellationToken ct)
    {
        if (provider is FixtureProvider) return new(null, [], []);
        var prompt = new ProviderPrompt(
            "You are a continuity validator. Return JSON only. Propose the complete post-turn state, new durable facts, and thread updates supported by the player action and narrative. Keep the old value when evidence is absent. Rumors and beliefs are not facts. Secrets are narrator-only and list only characters that actually learned them. Inventory/skill changes require explicit support in the action or narrative.",
            "BEFORE:\n" + Json.Write(context) + "\n\nPLAYER ACTION:\n" + action + "\n\nNARRATIVE:\n" + narrative, 1800);
        return (await CompleteValidatedWithUsage<StateTransition>(provider, prompt, TransitionSchema, value =>
        {
            var validated = ValidateTransition(value);
            if (validated.State is not null && !MechanicsSupported(context.State, validated.State, action, narrative))
                throw new InvalidOperationException("A proposed inventory or skill change lacks narrative support.");
            return validated;
        }, ct)).Value;
    }

    public static StateTransition ValidateTransition(StateTransition result)
    {
        if (result.Facts is null || result.Threads is null) throw new InvalidOperationException("Continuity output omitted required arrays.");
        result.State?.Validate();
        if (result.Facts.Length > 10 || result.Threads.Length > 10) throw new InvalidOperationException("Continuity output exceeds update limits.");
        foreach (var f in result.Facts)
            if (string.IsNullOrWhiteSpace(f.Text) || f.Text.Length > 1000 || !FactKinds.Contains(f.Kind) || f.Visibility is not ("public" or "narrator") || f.Confidence is < 0 or > 1 || f.KnownBy is null || f.KnownBy.Length > 30 || f.KnownBy.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 100)) throw new InvalidOperationException("Continuity output contains an invalid fact.");
        foreach (var t in result.Threads)
            if (string.IsNullOrWhiteSpace(t.Title) || t.Title.Length > 200 || t.Details.Length > 1000 || !ThreadKinds.Contains(t.Kind) || t.Status is not ("active" or "resolved") || t.Importance is < 1 or > 5) throw new InvalidOperationException("Continuity output contains an invalid thread.");
        return result;
    }

    public static ReconstructionResult ValidateReconstruction(ReconstructionResult result, int segmentCount)
    {
        if (result.Facts is null || result.Threads is null || result.Summary is null || result.Summary.Length > 4000 || result.Facts.Length > 40 || result.Threads.Length > 20) throw new InvalidOperationException("Reconstruction output exceeds limits.");
        result.ResumeState?.Validate();
        foreach (var f in result.Facts)
            if (string.IsNullOrWhiteSpace(f.Text) || f.Text.Length > 1000 || !FactKinds.Contains(f.Kind) || f.Visibility is not ("public" or "narrator") || f.Confidence is < 0 or > 1 || f.EvidenceOrdinals is null || f.EvidenceOrdinals.Length is 0 or > 10 || f.EvidenceOrdinals.Any(i => i < 0 || i >= segmentCount) || f.KnownBy is null || f.KnownBy.Length > 30 || f.KnownBy.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 100)) throw new InvalidOperationException("Reconstruction returned an invalid or unsupported fact.");
        ValidateTransition(new(null, [], result.Threads));
        return result;
    }

    public static async Task<T> CompleteValidated<T>(IStoryProvider provider, ProviderPrompt prompt, JsonElement schema, Func<T, T> validate, CancellationToken ct)
        => (await CompleteValidatedWithUsage(provider, prompt, schema, validate, ct)).Value;

    public static async Task<(T Value, ProviderCompletion Completion)> CompleteValidatedWithUsage<T>(IStoryProvider provider, ProviderPrompt prompt, JsonElement schema, Func<T, T> validate, CancellationToken ct)
    {
        Exception? first = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var completion = await provider.Complete(attempt == 0 ? prompt : prompt with { Instructions = prompt.Instructions + "\nYour previous response was invalid. Return one complete JSON object matching the schema exactly." }, schema, ct);
                var value = JsonSerializer.Deserialize<T>(completion.Text, Json.Options) ?? throw new JsonException("Empty JSON result.");
                return (validate(value), completion);
            }
            catch (Exception e) when ((e is JsonException or InvalidOperationException) && attempt == 0) { first = e; }
        }
        throw new InvalidOperationException("Provider returned invalid structured output after one repair attempt.", first);
    }

    public static bool MechanicsSupported(StoryState before, StoryState after, string action, string narrative)
    {
        var evidence = (action + " " + narrative).ToLowerInvariant();
        foreach (var key in before.Inventory.Keys.Union(after.Inventory.Keys))
            if (before.Inventory.GetValueOrDefault(key) != after.Inventory.GetValueOrDefault(key) && !evidence.Contains(key.ToLowerInvariant())) return false;
        foreach (var key in before.Skills.Keys.Union(after.Skills.Keys))
            if (before.Skills.GetValueOrDefault(key) != after.Skills.GetValueOrDefault(key) && !evidence.Contains(key.ToLowerInvariant())) return false;
        return true;
    }
    private static JsonElement ParseSchema(string text) { using var doc = JsonDocument.Parse(text); return doc.RootElement.Clone(); }
    private static string[] SafeKnownBy(string value) { try { return Json.Read<string[]>(value) ?? []; } catch { return []; } }
    private static HashSet<string> Keywords(string value) => value.ToLowerInvariant().Split([' ', '\n', '\r', '\t', '.', ',', ':', ';', '!', '?'], StringSplitOptions.RemoveEmptyEntries).Where(x => x.Length > 3).ToHashSet();
    private static int Score(string value, HashSet<string> words) => Keywords(value).Count(words.Contains);
}
