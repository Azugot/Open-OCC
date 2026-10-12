using System.IO.Compression;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Story;
using Xunit;
using Xunit.Abstractions;

namespace StoryTests;

public sealed class ImportDocumentTests(ITestOutputHelper output)
{
    [Fact]
    public void ActualRootDocxCoalescesFormattingWithoutLosingText()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Story transcript.docx"))) root = root.Parent;
        Assert.NotNull(root);
        var path = Path.Combine(root.FullName, "Story transcript.docx");
        var entries = ImportParser.Parse("Story transcript.docx", File.ReadAllBytes(path));
        var parsed = ImportDocument.Build(entries);
        output.WriteLine($"Root DOCX: {entries.Count} paragraphs -> {parsed.Structure.BlockCount} blocks, {parsed.Text.Length} characters.");
        Assert.True(parsed.Structure.BlockCount < entries.Count / 10);
        Assert.Equal(string.Join("\n\n", entries.Select(x => x.Text)), parsed.Text);
        Assert.Equal(parsed.Text, string.Concat(parsed.Structure.Blocks.Select(b => ImportDocument.Slice(parsed.Text, b.Start, b.Length))));
        var form = ImportDocument.Slice(parsed.Text, parsed.Structure.Blocks[0].Start, parsed.Structure.Blocks[0].Length);
        Assert.Contains("Character Preview", form); Assert.Contains("2. Gender:", form); Assert.Contains("Male / Female", form);
        Assert.Contains("3. Background:", form); Assert.Contains("7. Starting Talent", form);
    }

    [Fact]
    public void TwentyThousandShortParagraphsHaveBoundedBlocksAndExactUnicodeRanges()
    {
        var entries = Enumerable.Range(0, 20000).Select(i => new SourceEntry("unknown", $"Line {i}: 🔮 Azugot waits.\tOption A / B."));
        var parsed = ImportDocument.Build(entries);
        Assert.Equal(20000, parsed.Structure.ParagraphCount); Assert.InRange(parsed.Structure.BlockCount, 100, 400);
        var restored = string.Concat(parsed.Structure.Blocks.Select(b => ImportDocument.Slice(parsed.Text, b.Start, b.Length)));
        Assert.Equal(parsed.Text, restored);
        Assert.All(parsed.Structure.Blocks, b => Assert.InRange(b.Length, 1, 6002));
        Assert.Equal(parsed.Text.Length, parsed.Structure.Blocks.Sum(b => b.Length));
    }

    [Fact]
    public void ExplicitJsonTurnsAndSpeakerChangesKeepTheirBoundariesAndTimestamps()
    {
        var parsed = ImportDocument.Build([new("user", "First", "message", "2026-09-30T10:00:00Z"),
            new("user", "Second", "message"), new("assistant", "Reply", "message")]);
        Assert.Equal(3, parsed.Structure.BlockCount);
        Assert.Equal("2026-09-30T10:00:00Z", parsed.Structure.Blocks[0].Timestamp);
        Assert.Equal(new[] { "user", "user", "assistant" }, parsed.Structure.Blocks.Select(x => x.Speaker));
    }

    [Fact]
    public void HugeParagraphsAndSurrogatePairsRemainComplete()
    {
        var text = string.Concat(Enumerable.Repeat("😀", 10001));
        var document = ImportDocument.Build([new("unknown", text)]);
        Assert.Equal(text, string.Concat(document.Structure.Blocks.Select(b => ImportDocument.Slice(document.Text, b.Start, b.Length))));
        Assert.All(document.Structure.Blocks, b => Assert.False(char.IsHighSurrogate(document.Text[b.Start + b.Length - 1])));
    }

    [Fact]
    public async Task PreparationPersistsOnlyOneTextCopyAndNeverInvokesTheProvider()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = new StoryDb(new DbContextOptionsBuilder<StoryDb>().UseSqlite(connection).Options); await db.Database.EnsureCreatedAsync();
        var branch = await CampaignService.Create(db, new("Large preparation", false), default);
        var text = string.Join("\n\n", Enumerable.Range(0, 20000).Select(i => $"List item {i}: A / B / C"));
        var source = new ImportedSource { CampaignId = branch.CampaignId, FileName = "large.txt", Bytes = Encoding.UTF8.GetBytes(text) };
        var job = new ImportJob { BranchId = branch.Id, SourceId = source.Id, ExpectedCheckpointId = branch.HeadCheckpointId, Method = Reconstruction.Method, Status = "preparing" };
        db.AddRange(source, job); await db.SaveChangesAsync();
        await Reconstruction.Step(db, job, null, default);
        Assert.Equal("prepared", job.Status); Assert.Equal(0, job.Calls); Assert.Equal(0, job.Processed);
        Assert.InRange(job.Total, 50, 500); Assert.Equal(text, source.NormalizedText);
        Assert.All(await db.Segments.ToListAsync(), s => { Assert.Empty(s.Text); Assert.True(s.Length > 0); });
        Assert.Single(await db.Messages.ToListAsync()); Assert.Empty(await db.ImportedHistory.ToListAsync());
        Assert.Equal(job.ExpectedCheckpointId, branch.HeadCheckpointId);
    }

    [Fact]
    public void EvidenceRangesRejectAdjacentOrOutsideSourceText()
    {
        var claim = new ImportClaim("fact:location", "fact", "A fact.", "", "", "", 0, "fact", "public", [], .9, [0], "current", [], EvidenceRanges: [new(100, 20)]);
        var blocks = new[] { new ImportSegment { Ordinal = 0, Start = 100, Length = 20 }, new ImportSegment { Ordinal = 1, Start = 120, Length = 20 } };
        Reconstruction.ValidateRanges(claim, blocks);
        Assert.Throws<InvalidOperationException>(() => Reconstruction.ValidateRanges(claim with { EvidenceRanges = [new(110, 20)] }, blocks));
        Assert.Throws<InvalidOperationException>(() => Reconstruction.ValidateRanges(claim with { EvidenceRanges = [new(120, 20)] }, blocks));
    }
}
