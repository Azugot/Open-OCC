using System.Text;
using Microsoft.EntityFrameworkCore;

namespace Story;

public record SourceRange(int Start, int Length);
public record SourceBlock(int Ordinal, int Start, int Length, string Speaker, string Section, bool Artifact, string? Timestamp = null);
public record DocumentStructure(string Version, int ParagraphCount, int ShortParagraphCount, int CharacterCount,
    int BlockCount, int KnownSpeakerBlocks, int UnknownSpeakerBlocks, SourceBlock[] Blocks);
public record ParsedDocument(string Text, DocumentStructure Structure);

// Word paragraphs and list rows are formatting, not conversation turns. The immutable
// normalized document is stored once; blocks and citations are indexes into it.
public static class ImportDocument
{
    public const string Version = "document-v3";
    public static ParsedDocument Build(IEnumerable<SourceEntry> entries, int target = 3500, int maximum = 6000)
    {
        if (target < 500 || maximum < target) throw new ArgumentOutOfRangeException(nameof(target));
        var text = new StringBuilder(); var parts = new List<(SourceEntry Entry, int Start, int End)>();
        foreach (var entry in entries)
        {
            if (text.Length > 0) text.Append("\n\n");
            var start = text.Length; text.Append(entry.Text);
            if (text.Length > ImportParser.MaxTextCharacters) throw new InvalidOperationException("Extracted document exceeds 16 MiB of text.");
            parts.Add((entry, start, text.Length));
        }
        if (parts.Count == 0) throw new InvalidOperationException("The document contains no readable text.");
        var blocks = new List<SourceBlock>(); var blockStart = 0; var currentEnd = 0;
        var speaker = parts[0].Entry.Speaker; var section = parts[0].Entry.Section; var timestamp = parts[0].Entry.Timestamp; var allArtifacts = true;
        void Flush(int end)
        {
            if (end <= blockStart) return;
            blocks.Add(new(blocks.Count, blockStart, end - blockStart, speaker, section, allArtifacts, timestamp));
            blockStart = end; currentEnd = end; allArtifacts = true; section = "";
        }
        foreach (var part in parts)
        {
            var knownChange = part.Entry.Speaker != speaker && (part.Entry.Speaker != "unknown" || speaker != "unknown");
            var heading = part.Entry.Section == "heading";
            // Keep headings with their following material and short question/option,
            // list and status rows together. Explicit speaker boundaries stay intact.
            if (currentEnd > blockStart && (knownChange || part.Entry.Section == "message" || heading && currentEnd - blockStart >= 1200 ||
                currentEnd - blockStart >= target && part.Entry.Text.Length >= 120 || part.End - blockStart > maximum))
                Flush(part.Start);
            if (currentEnd == blockStart) { speaker = part.Entry.Speaker; section = part.Entry.Section; timestamp = part.Entry.Timestamp; }
            else if (part.Entry.Section == "table") section = "table";
            var cursor = part.Start;
            foreach (var piece in ImportContext.Passages([part.Entry], maximum))
            {
                if (cursor > part.Start) { Flush(cursor); speaker = part.Entry.Speaker; section = part.Entry.Section; }
                allArtifacts &= part.Entry.Artifact;
                currentEnd = cursor + piece.Text.Length; cursor = currentEnd;
            }
        }
        Flush(text.Length);
        return new(text.ToString(), new(Version, parts.Count, parts.Count(x => x.Entry.Text.Length < 40), text.Length,
            blocks.Count, blocks.Count(x => x.Speaker != "unknown"), blocks.Count(x => x.Speaker == "unknown"), blocks.ToArray()));
    }

    public static ImportSegment[] Hydrate(IEnumerable<ImportSegment> rows, string document) => rows.Select(x => new ImportSegment
    {
        Id = x.Id, JobId = x.JobId, Ordinal = x.Ordinal, Speaker = x.Speaker, Section = x.Section,
        Timestamp = x.Timestamp, Artifact = x.Artifact, Start = x.Start, Length = x.Length,
        Text = x.Start >= 0 ? Slice(document, x.Start, x.Length) : x.Text
    }).ToArray();

    public static string Slice(string document, int start, int length)
    {
        if (start < 0 || length < 1 || (long)start + length > document.Length)
            throw new InvalidOperationException("The source block range is outside its preserved document.");
        return document.Substring(start, length);
    }

    public static async Task<ImportSegment[]> Load(StoryDb db, ImportJob job, IQueryable<ImportSegment> rows, CancellationToken ct)
    {
        var document = await db.Sources.Where(x => x.Id == job.SourceId).Select(x => x.NormalizedText).SingleAsync(ct);
        return Hydrate(await rows.AsNoTracking().ToArrayAsync(ct), document);
    }
}
