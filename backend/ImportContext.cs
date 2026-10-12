using System.Text.Encodings.Web;
using System.Text.Json;

namespace Story;

// Citation passages are storage units; reading windows keep adjacent prose together.
public static class ImportContext
{
    public static ImportSegment? Tail(ImportSegment row, int budget)
    {
        var length = Math.Min(row.Text.Length, Math.Max(0, budget - 300));
        while (length > 0)
        {
            if (char.IsLowSurrogate(row.Text[row.Text.Length - length])) length--;
            if (length == 0) break;
            var slice = new ImportSegment { Id = row.Id, JobId = row.JobId, Ordinal = row.Ordinal, Speaker = row.Speaker,
                Section = row.Section, Timestamp = row.Timestamp, Artifact = row.Artifact,
                Start = row.Start < 0 ? -1 : row.Start + row.Text.Length - length, Length = length, Text = row.Text[^length..] };
            if (Evidence([slice]).Length <= budget) return slice;
            length = length * 3 / 4;
        }
        return null;
    }
    private static readonly JsonSerializerOptions EvidenceOptions = new(Json.Options) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static string Evidence(IEnumerable<ImportSegment> rows) => JsonSerializer.Serialize(rows.Select(x => new
        { x.Ordinal, x.Speaker, x.Text, x.Section, x.Timestamp, x.Artifact, x.Start, x.Length }), EvidenceOptions);

    public static IEnumerable<SourceEntry> Passages(IEnumerable<SourceEntry> entries, int max)
    {
        foreach (var entry in entries)
        {
            var offset = 0;
            while (offset < entry.Text.Length)
            {
                var length = Math.Min(max, entry.Text.Length - offset);
                if (offset + length < entry.Text.Length)
                {
                    // Prefer a paragraph, then a line or sentence, then whitespace.
                    var text = entry.Text.Substring(offset, length);
                    var boundary = text.LastIndexOf("\n\n", StringComparison.Ordinal);
                    if (boundary >= length / 2) length = boundary + 2;
                    else
                    {
                        boundary = text.LastIndexOf('\n');
                        if (boundary >= length / 2) length = boundary + 1;
                        else
                        {
                            boundary = text.LastIndexOf(". ", StringComparison.Ordinal);
                            if (boundary < length / 2) boundary = text.LastIndexOf(' ');
                            if (boundary >= length / 2) length = boundary + 1;
                        }
                    }
                    if (char.IsHighSurrogate(entry.Text[offset + length - 1])) length--;
                }
                yield return entry with { Text = entry.Text.Substring(offset, length) };
                offset += length;
            }
        }
    }
}
