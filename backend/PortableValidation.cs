using System.Collections;
using System.Text.Json;

namespace Story;

public static class PortableValidation
{
    public static void Validate(PortableExport portable)
    {
        if (portable is null || portable.Version is < 1 or > 5 || portable.Campaign is null)
            throw new InvalidOperationException("Unsupported or incomplete Open-OCC export.");
        if (portable.Campaign.Id == Guid.Empty) throw new InvalidOperationException("Export campaign ID is empty.");
        var required = new[] { "Branches", "Checkpoints", "Messages", "Runs", "Sources", "Jobs", "Segments", "Facts", "Evidence", "Corrections", "Threads" };
        var groups = new Dictionary<string, object[]>();
        foreach (var property in typeof(PortableExport).GetProperties().Where(x => typeof(IEnumerable).IsAssignableFrom(x.PropertyType)))
        {
            var value = property.GetValue(portable);
            if (value is null && required.Contains(property.Name)) throw new InvalidOperationException($"Export is missing {property.Name}.");
            var rows = value is IEnumerable items ? items.Cast<object>().ToArray() : [];
            if (rows.Any(x => x is null)) throw new InvalidOperationException($"Export {property.Name} contains a null row.");
            var ids = rows.Select(x => x.GetType().GetProperty("Id")?.GetValue(x)).OfType<Guid>().ToArray();
            if (ids.Any(x => x == Guid.Empty) || ids.Distinct().Count() != ids.Length) throw new InvalidOperationException($"Export {property.Name} contains duplicate or empty IDs.");
            groups[property.Name] = rows;
        }
        if (groups["Branches"].Length == 0 || groups["Checkpoints"].Length == 0) throw new InvalidOperationException("Export needs at least one timeline and checkpoint.");
        var maps = groups.ToDictionary(x => x.Key, x => x.Value.Select(row => row.GetType().GetProperty("Id")?.GetValue(row)).OfType<Guid>().ToHashSet());
        maps["Campaign"] = [portable.Campaign.Id];
        var references = new Dictionary<string, string> { ["CampaignId"] = "Campaign", ["BranchId"] = "Branches", ["ParentBranchId"] = "Branches",
            ["CheckpointId"] = "Checkpoints", ["HeadCheckpointId"] = "Checkpoints", ["ForkCheckpointId"] = "Checkpoints", ["ExpectedCheckpointId"] = "Checkpoints",
            ["SourceId"] = "Sources", ["JobId"] = "Jobs", ["SegmentId"] = "Segments", ["FactId"] = "Facts", ["WorldId"] = "Worlds", ["WorldVersionId"] = "WorldVersions",
            ["CharacterId"] = "Characters", ["FromCharacterId"] = "Characters", ["ToCharacterId"] = "Characters", ["MessageId"] = "Messages" };
        void CheckJson(JsonElement node, string label)
        {
            if (node.ValueKind == JsonValueKind.Array) { foreach (var child in node.EnumerateArray()) CheckJson(child, label); }
            if (node.ValueKind != JsonValueKind.Object) return;
            foreach (var field in node.EnumerateObject())
            {
                var collection = field.Name switch { "sourceId" => "Sources", "jobId" => "Jobs", "segmentId" => "Segments", _ => null };
                if (collection is not null && field.Value.ValueKind == JsonValueKind.String && field.Value.TryGetGuid(out var id) && !maps[collection].Contains(id))
                    throw new InvalidOperationException($"Export {label} references missing {field.Name}.");
                CheckJson(field.Value, label);
            }
        }
        foreach (var row in groups.Values.SelectMany(x => x).Append(portable.Campaign))
        foreach (var property in row.GetType().GetProperties())
        {
            var value = property.GetValue(row);
            if (references.TryGetValue(property.Name, out var collection) && value is Guid id && !maps[collection].Contains(id))
                throw new InvalidOperationException($"Export {row.GetType().Name}.{property.Name} references a missing record.");
            if (!property.Name.EndsWith("Json")) continue;
            if (value is null)
            {
                if (new System.Reflection.NullabilityInfoContext().Create(property).ReadState == System.Reflection.NullabilityState.Nullable) continue;
                throw new InvalidOperationException($"Export is missing {row.GetType().Name}.{property.Name}.");
            }
            if (value is not string json) continue;
            try { using var document = JsonDocument.Parse(json); CheckJson(document.RootElement, property.Name); }
            catch (JsonException) { throw new InvalidOperationException($"Export contains invalid JSON in {row.GetType().Name}.{property.Name}."); }
        }
        foreach (var branch in portable.Branches)
            if (!portable.Checkpoints.Any(x => x.Id == branch.HeadCheckpointId && x.BranchId == branch.Id))
                throw new InvalidOperationException("A timeline head must belong to that timeline.");
        foreach (var source in portable.Sources)
            if (source.Bytes is null || string.IsNullOrWhiteSpace(source.FileName) || source.FileName.Length > 200)
                throw new InvalidOperationException("Export source needs original bytes and a filename of at most 200 characters.");
        foreach (var job in portable.Jobs)
            if (!portable.Checkpoints.Any(x => x.Id == job.ExpectedCheckpointId && x.BranchId == job.BranchId))
                throw new InvalidOperationException("An import checkpoint must belong to its destination timeline.");
        foreach (var result in portable.ImportResults ?? [])
        {
            var ids = result.Stage.EndsWith("-plan") ? Json.Read<Guid[]>(result.ResultJson) : result.Stage.EndsWith("-windows")
                ? Json.Read<ReviewWindow[]>(result.ResultJson).SelectMany(x => x.ProposalIds).ToArray() : [];
            if (ids.Any(id => !maps["ImportProposals"].Contains(id))) throw new InvalidOperationException("A reconstruction plan references a missing proposal.");
        }
    }
}
