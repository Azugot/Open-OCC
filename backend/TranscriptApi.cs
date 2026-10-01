using Microsoft.EntityFrameworkCore;

namespace Story;

public static class TranscriptApi
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/sources/{id:guid}/structure", async (Guid id, StoryDb db, CancellationToken ct) =>
        {
            var stored = await db.Sources.Where(x => x.Id == id).Select(x => x.StructureJson).SingleOrDefaultAsync(ct) ?? throw new KeyNotFoundException();
            var structure = !string.IsNullOrEmpty(stored) && stored != "{}" ? Json.Read<DocumentStructure>(stored) : Document(await ReadSource(db, id, ct)).Structure;
            return Results.Ok(new { structure.Version, structure.BlockCount, structure.ParagraphCount, structure.ShortParagraphCount,
                structure.CharacterCount, structure.KnownSpeakerBlocks, structure.UnknownSpeakerBlocks });
        });
        app.MapGet("/api/sources/{id:guid}/blocks", async (Guid id, Guid? jobId, int? page, string? q, string? ordinals, StoryDb db, CancellationToken ct) =>
        {
            if (q?.Length > 200) throw new InvalidOperationException("Search is limited to 200 characters.");
            var source = await ReadSource(db, id, ct);
            var document = Document(source);
            var job = jobId is { } j ? await db.ImportJobs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == j && x.SourceId == id, ct) ?? throw new KeyNotFoundException() : null;
            int[]? selected = null;
            if (!string.IsNullOrWhiteSpace(ordinals))
            {
                var values = ordinals.Split(',');
                if (values.Length > 64 || values.Any(x => !int.TryParse(x, out var ordinal) || ordinal < 0)) throw new InvalidOperationException("Select at most 64 valid source block ordinals.");
                selected = values.Select(int.Parse).ToArray();
            }
            IEnumerable<ImportSegment> blocks;
            if (job is not null && (job.Method == Reconstruction.Method || selected is not null))
            {
                var query = db.Segments.AsNoTracking().Where(x => x.JobId == job.Id);
                if (selected is not null) query = query.Where(x => selected.Contains(x.Ordinal));
                blocks = await query.OrderBy(x => x.Ordinal).ToArrayAsync(ct);
            }
            else blocks = document.Structure.Blocks.Select(x => new ImportSegment { Ordinal = x.Ordinal, Start = x.Start, Length = x.Length,
                Speaker = x.Speaker, Section = x.Section, Artifact = x.Artifact });
            if (selected is not null) blocks = blocks.Where(x => selected.Contains(x.Ordinal));
            string Text(ImportSegment block) => block.Start >= 0 ? ImportDocument.Slice(document.Text, block.Start, block.Length) : block.Text;
            if (!string.IsNullOrWhiteSpace(q)) blocks = blocks.Where(x => Text(x).Contains(q, StringComparison.OrdinalIgnoreCase));
            var rows = blocks.ToArray(); var index = Math.Max(0, page ?? 0);
            return Results.Ok(new { source = new { source.Id, source.FileName, structureVersion = document.Structure.Version },
                total = rows.Length, page = index, pageSize = 20, items = rows.Skip(index * 20).Take(20).Select(x => new { x.Ordinal, text = Text(x), x.Speaker, x.Section, x.Start, x.Length }) });
        });
        app.MapGet("/api/branches/{id:guid}/transcripts", async (Guid id, Guid? checkpointId, int? page, StoryDb db, CancellationToken ct) =>
        {
            var branch = await db.Branches.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException();
            var checkpoint = await db.Checkpoints.AsNoTracking().SingleOrDefaultAsync(x => x.Id == (checkpointId ?? branch.HeadCheckpointId) && x.BranchId == id, ct) ?? throw new KeyNotFoundException();
            var histories = await db.ImportedHistory.AsNoTracking().Where(x => x.BranchId == id && x.EffectiveSequence <= checkpoint.Sequence).ToListAsync(ct);
            // Old approved imports retain their original evidence IDs. Group their
            // display from the document without rewriting or invalidating that evidence.
            var legacy = await db.ImportJobs.AsNoTracking().Where(x => x.BranchId == id && x.Status == "approved" && x.Method != Reconstruction.Method).ToListAsync(ct);
            foreach (var job in legacy.Where(x => histories.All(h => h.JobId != x.Id)))
            {
                var sequence = await db.Facts.Where(x => x.JobId == job.Id && x.BranchId == id).Select(x => (int?)x.EffectiveSequence).MinAsync(ct);
                if (sequence is { } established && established <= checkpoint.Sequence) histories.Add(new() { BranchId = id, SourceId = job.SourceId, JobId = job.Id, EffectiveSequence = established });
            }
            var total = histories.Count; var index = Math.Max(0, page ?? 0); var items = new List<object>();
            foreach (var history in histories.OrderByDescending(x => x.EffectiveSequence).Skip(index * 20).Take(20))
            {
                var source = await db.Sources.Where(x => x.Id == history.SourceId).Select(x => new { x.FileName, x.StructureJson }).SingleAsync(ct);
                var structure = !string.IsNullOrEmpty(source.StructureJson) && source.StructureJson != "{}" ? Json.Read<DocumentStructure>(source.StructureJson) : Document(await ReadSource(db, history.SourceId, ct)).Structure;
                items.Add(new { history.SourceId, history.JobId, source.FileName, history.EffectiveSequence, blockCount = structure.BlockCount, structureVersion = structure.Version });
            }
            return Results.Ok(new { total, page = index, pageSize = 20, items });
        });
    }
    static async Task<ImportedSource> ReadSource(StoryDb db, Guid id, CancellationToken ct)
    {
        var source = await db.Sources.Where(x => x.Id == id).Select(x => new ImportedSource { Id = x.Id,
            FileName = x.FileName, NormalizedText = x.NormalizedText, StructureJson = x.StructureJson }).SingleOrDefaultAsync(ct) ?? throw new KeyNotFoundException();
        if (source.NormalizedText.Length == 0) source.Bytes = await db.Sources.Where(x => x.Id == id).Select(x => x.Bytes).SingleAsync(ct);
        return source;
    }
    public static ParsedDocument Document(ImportedSource source) => source.NormalizedText.Length > 0 && source.StructureJson != "{}"
        ? new(source.NormalizedText, Json.Read<DocumentStructure>(source.StructureJson)) : ImportDocument.Build(ImportParser.Parse(source.FileName, source.Bytes));
}
