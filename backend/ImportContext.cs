using System.Text.Encodings.Web;
using System.Text.Json;

namespace Story;

// Citation passages are storage units; reading windows keep adjacent prose together.
public static class ImportContext
{
    private static readonly JsonSerializerOptions EvidenceOptions = new(Json.Options) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static string Evidence(IEnumerable<ImportSegment> rows) => JsonSerializer.Serialize(rows.Select(x => new
        { x.Ordinal, x.Speaker, x.Text, x.Section, x.Timestamp, x.Artifact }), EvidenceOptions);

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
