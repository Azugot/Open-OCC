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
        new() { ["Practice sword"] = 1, ["Uniform"] = 1, ["Crowns"] = 20 }, ["Lyra Fen"]);
    public static StoryState Empty => new("Not yet established", "Not yet established", "Import and approve a resume checkpoint", "Unknown", new(), new(), []);
}

public class Campaign
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
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
    public string? Error { get; set; }
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
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
public class ImportSegment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid JobId { get; set; }
    public int Ordinal { get; set; }
    public string Speaker { get; set; } = "unknown";
    public string Text { get; set; } = "";
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
}
public class FactEvidence
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FactId { get; set; }
    public Guid SegmentId { get; set; }
}
public class ProviderProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Adapter { get; set; } = "";
    public string Model { get; set; } = "";
    public bool Enabled { get; set; }
}
