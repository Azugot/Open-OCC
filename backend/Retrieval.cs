using Microsoft.EntityFrameworkCore;

namespace Story;

public sealed record RetrievalHit(string Type, Guid Id, string Text, int Sequence, double Score, string? Source = null);

public static class RetrievalService
{
    public static async Task<IReadOnlyList<RetrievalHit>> Search(StoryDb db, Guid branchId, string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var words = query.ToLowerInvariant().Split([' ', '\n', '\r', '\t', '.', ',', ':', ';', '!', '?'], StringSplitOptions.RemoveEmptyEntries).Where(x => x.Length > 2).Distinct().ToArray();
        if (words.Length == 0) return [];
        var messages = await db.Messages.AsNoTracking().Where(x => x.BranchId == branchId).ToListAsync(ct);
        var facts = await db.Facts.AsNoTracking().Where(x => x.BranchId == branchId && x.ReviewStatus == "accepted").ToListAsync(ct);
        var threads = await db.NarrativeThreads.AsNoTracking().Where(x => x.BranchId == branchId).ToListAsync(ct);
        var hits = new List<RetrievalHit>();
        foreach (var x in messages)
        {
            var score = Score(x.Content, words) + x.Sequence / 100000d;
            if (score > 0) hits.Add(new("message", x.Id, x.Content, x.Sequence, score));
        }
        foreach (var x in facts)
        {
            var score = Score(x.Text, words) + x.Confidence + (x.Kind is "promise" or "deadline" ? 2 : 0);
            if (score > 0) hits.Add(new("fact", x.Id, x.Text, x.EffectiveSequence, score, x.Provenance));
        }
        foreach (var x in threads)
        {
            var score = Score(x.Title + " " + x.Details, words) + x.Importance;
            if (score > 0) hits.Add(new("thread", x.Id, $"{x.Title}: {x.Details}", x.EffectiveSequence, score));
        }
        return hits.OrderByDescending(x => x.Score).ThenByDescending(x => x.Sequence).Take(100).ToArray();
    }
    private static double Score(string text, string[] words) => words.Count(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));
}
