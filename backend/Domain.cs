using System.Text.Json;

namespace Story;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Read<T>(string value) => JsonSerializer.Deserialize<T>(value, Options)!;
}

public sealed record StoryState(string Location, string Time, string Objective, string Condition,
    Dictionary<string, string> Skills, Dictionary<string, int> Inventory, string[] Participants)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Location) || Location.Length > 200 || string.IsNullOrWhiteSpace(Time) || Time.Length > 200 ||
            string.IsNullOrWhiteSpace(Objective) || Objective.Length > 1000 || string.IsNullOrWhiteSpace(Condition) || Condition.Length > 200 ||
            Skills is null || Skills.Count > 50 || Skills.Any(x => string.IsNullOrWhiteSpace(x.Key) || x.Key.Length > 100 || x.Value is null || x.Value.Length > 100) ||
            Inventory is null || Inventory.Count > 100 || Inventory.Any(x => string.IsNullOrWhiteSpace(x.Key) || x.Key.Length > 100 || x.Value < 0 || x.Value > 1000000) ||
            Participants is null || Participants.Length > 30 || Participants.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 100))
            throw new InvalidOperationException("State is incomplete or exceeds the permitted field and inventory limits.");
    }
    public static StoryState Synthetic => new("Crownspire Academy · Registration hall", "Imperial Year 731 · Day 1 · 08:00",
        "Register for your first term", "Rested", new() { ["Sword practice"] = "F · 2/5", ["Aura circulation"] = "F · 1/5" },
        new() { ["Practice sword"] = 1, ["Uniform"] = 1, ["Crowns"] = 20 }, ["Lyra Fen", "Clerk Orin", "Guard Sera"]);
    public static StoryState Empty => new("Not yet established", "Not yet established", "Import and approve a resume checkpoint", "Unknown", new(), new(), []);
}

public class Campaign
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public Guid? WorldVersionId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
public class World
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
public class WorldVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorldId { get; set; }
    public int Version { get; set; } = 1;
    public string RulesJson { get; set; } = "{}";
    public string LocationsJson { get; set; } = "[]";
    public string FactionsJson { get; set; } = "[]";
    public string AuthorInstructions { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
public class Branch
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CampaignId { get; set; }
    public string Name { get; set; } = "Main timeline";
    public Guid? ParentBranchId { get; set; }
    public Guid? ForkCheckpointId { get; set; }
    public Guid HeadCheckpointId { get; set; }
    public int Revision { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
public class Checkpoint
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BranchId { get; set; }
    public int Sequence { get; set; }
    public string Label { get; set; } = "";
    public string StateJson { get; set; } = "{}";
    public string WorldJson { get; set; } = "{}";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
public class Message
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BranchId { get; set; }
    public int Sequence { get; set; }
    public string Role { get; set; } = "assistant";
    public string Content { get; set; } = "";
    public string Provider { get; set; } = "fixture";
    public string Model { get; set; } = "fixture-v1";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
public class GenerationRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BranchId { get; set; }
    public Guid ExpectedCheckpointId { get; set; }
    public string Action { get; set; } = "";
    public string Status { get; set; } = "running";
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public string ContextJson { get; set; } = "{}";
    public string? StateProposalJson { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public string? Error { get; set; }
    public string DraftJson { get; set; } = "{}";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
public class ImportedSource
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CampaignId { get; set; }
    public string FileName { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public byte[] Bytes { get; set; } = [];
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
public class ImportJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SourceId { get; set; }
    public Guid BranchId { get; set; }
    public Guid ExpectedCheckpointId { get; set; }
    public string Status { get; set; } = "queued";
    public int Processed { get; set; }
    public int Total { get; set; }
    public string? Error { get; set; }
    public string Method { get; set; } = "deterministic-v1";
    public string Provider { get; set; } = "fixture";
    public string Model { get; set; } = "deterministic-v1";
    public string? ProposedStateJson { get; set; }
    public string ProposedThreadsJson { get; set; } = "[]";
    public string? Summary { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTime? LeaseUntil { get; set; }
    public int AttemptCount { get; set; }
    public string Stage { get; set; } = "normalize";
    public int StageCursor { get; set; }
    public int ProposalRevision { get; set; }
    public int Calls { get; set; }
    public int MaxCalls { get; set; } = 100;
    public int InputCharacterLimit { get; set; } = 24000;
    public int OutputTokenLimit { get; set; } = 3000;
    public string? ResumeJson { get; set; }
    public bool ReviewCompleted { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
public class ImportSegment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid JobId { get; set; }
    public int Ordinal { get; set; }
    public string Speaker { get; set; } = "unknown";
    public string Text { get; set; } = "";
    public string Section { get; set; } = "";
    public string? Timestamp { get; set; }
    public bool Artifact { get; set; }
}
public class ImportSection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid JobId { get; set; }
    public int Ordinal { get; set; }
    public int Start { get; set; }
    public int End { get; set; }
}
public class ImportProposal
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid JobId { get; set; }
    public string Key { get; set; } = "";
    public string Category { get; set; } = "fact";
    public string ContentJson { get; set; } = "{}";
    public int Revision { get; set; }
    public bool Current { get; set; } = true;
    public bool Excluded { get; set; }
}
public class ImportIssue
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid JobId { get; set; }
    public string Code { get; set; } = "";
    public string Text { get; set; } = "";
    public string EvidenceJson { get; set; } = "[]";
    public bool Blocking { get; set; } = true;
    public string Resolution { get; set; } = "";
}
public class ImportStageResult
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid JobId { get; set; }
    public string Stage { get; set; } = "";
    public int Ordinal { get; set; }
    public string ResultJson { get; set; } = "{}";
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
}
public class Fact
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BranchId { get; set; }
    public Guid? JobId { get; set; }
    public int EffectiveSequence { get; set; }
    public string Text { get; set; } = "";
    public string Provenance { get; set; } = "imported";
    public string ReviewStatus { get; set; } = "pending";
    // Foundation narration receives only public accepted facts. Private facts are never sent to a provider.
    public string Visibility { get; set; } = "narrator";
    public string Kind { get; set; } = "fact";
    public double Confidence { get; set; } = 1;
    public string KnownByJson { get; set; } = "[]";
}
public class FactEvidence
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FactId { get; set; }
    public Guid SegmentId { get; set; }
}
public class FactCorrection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BranchId { get; set; }
    public Guid FactId { get; set; }
    public string PreviousText { get; set; } = "";
    public string NewText { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
public class ProviderProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Adapter { get; set; } = "";
    public string Model { get; set; } = "";
    public bool Enabled { get; set; }
}
public class NarrativeThread
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BranchId { get; set; }
    public string Title { get; set; } = "";
    public string Details { get; set; } = "";
    public string Kind { get; set; } = "thread";
    public string Status { get; set; } = "active";
    public int Importance { get; set; } = 3;
    public int EffectiveSequence { get; set; }
}
public class Character
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CampaignId { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Goals { get; set; } = "";
    public string Status { get; set; } = "active";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
public class Relationship
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BranchId { get; set; }
    public Guid FromCharacterId { get; set; }
    public Guid ToCharacterId { get; set; }
    public string Label { get; set; } = "knows";
    public int Score { get; set; }
    public string Notes { get; set; } = "";
    public int EffectiveSequence { get; set; }
}
public class KnowledgeRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BranchId { get; set; }
    public Guid CharacterId { get; set; }
    public Guid? FactId { get; set; }
    public string Subject { get; set; } = "";
    public string BeliefType { get; set; } = "fact";
    public double Confidence { get; set; } = 1;
    public int EffectiveSequence { get; set; }
    public string Source { get; set; } = "manual";
}
public class MechanicsLedgerEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BranchId { get; set; }
    public int Sequence { get; set; }
    public string Kind { get; set; } = "inventory";
    public string Subject { get; set; } = "";
    public int Delta { get; set; }
    public string Reason { get; set; } = "";
    public string Source { get; set; } = "manual";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
public class StoryEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BranchId { get; set; }
    public int Sequence { get; set; }
    public string Type { get; set; } = "turn";
    public string Summary { get; set; } = "";
    public Guid? MessageId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
public class CampaignSummary
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BranchId { get; set; }
    public int ThroughSequence { get; set; }
    public string Text { get; set; } = "";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
public class AppSetting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}
