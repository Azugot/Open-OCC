using System.Data;
using Microsoft.EntityFrameworkCore;

namespace Story;

public static class CampaignDeletion
{
    public static async Task Delete(StoryDb db, Guid id, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        if (!await db.Campaigns.AnyAsync(x => x.Id == id, ct)) throw new KeyNotFoundException();
        var branches = db.Branches.Where(x => x.CampaignId == id).Select(x => x.Id);
        var sources = db.Sources.Where(x => x.CampaignId == id).Select(x => x.Id);
        var jobs = db.ImportJobs.Where(x => branches.Contains(x.BranchId) || sources.Contains(x.SourceId)).Select(x => x.Id);
        if (await db.GenerationRuns.AnyAsync(x => branches.Contains(x.BranchId) && x.Status == "running", ct)
            || await db.ImportJobs.AnyAsync(x => jobs.Contains(x.Id) && (x.Status == "queued" || x.Status == "processing") && x.LeaseUntil > DateTime.UtcNow, ct))
            throw new DbUpdateConcurrencyException("Wait for active story generation or import processing to finish before deleting this story.");
        var facts = db.Facts.Where(x => branches.Contains(x.BranchId)).Select(x => x.Id);
        var segments = db.Segments.Where(x => jobs.Contains(x.JobId)).Select(x => x.Id);
        // Evidence on forks may refer to the same imported source. Remove both sides before sources.
        await db.Evidence.Where(x => facts.Contains(x.FactId) || segments.Contains(x.SegmentId)).ExecuteDeleteAsync(ct);
        await db.FactCorrections.Where(x => branches.Contains(x.BranchId) || facts.Contains(x.FactId)).ExecuteDeleteAsync(ct);
        await db.Knowledge.Where(x => branches.Contains(x.BranchId)).ExecuteDeleteAsync(ct);
        await db.Relationships.Where(x => branches.Contains(x.BranchId)).ExecuteDeleteAsync(ct);
        await db.ImportIssues.Where(x => jobs.Contains(x.JobId)).ExecuteDeleteAsync(ct);
        await db.ImportProposals.Where(x => jobs.Contains(x.JobId)).ExecuteDeleteAsync(ct);
        await db.ImportResults.Where(x => jobs.Contains(x.JobId)).ExecuteDeleteAsync(ct);
        await db.ImportSections.Where(x => jobs.Contains(x.JobId)).ExecuteDeleteAsync(ct);
        await db.Segments.Where(x => jobs.Contains(x.JobId)).ExecuteDeleteAsync(ct);
        await db.Facts.Where(x => branches.Contains(x.BranchId)).ExecuteDeleteAsync(ct);
        await db.ImportJobs.Where(x => jobs.Contains(x.Id)).ExecuteDeleteAsync(ct);
        await db.Sources.Where(x => sources.Contains(x.Id)).ExecuteDeleteAsync(ct);
        await db.NarrativeThreads.Where(x => branches.Contains(x.BranchId)).ExecuteDeleteAsync(ct);
        await db.Mechanics.Where(x => branches.Contains(x.BranchId)).ExecuteDeleteAsync(ct);
        await db.Events.Where(x => branches.Contains(x.BranchId)).ExecuteDeleteAsync(ct);
        await db.Summaries.Where(x => branches.Contains(x.BranchId)).ExecuteDeleteAsync(ct);
        await db.GenerationRuns.Where(x => branches.Contains(x.BranchId)).ExecuteDeleteAsync(ct);
        await db.Messages.Where(x => branches.Contains(x.BranchId)).ExecuteDeleteAsync(ct);
        await db.Checkpoints.Where(x => branches.Contains(x.BranchId)).ExecuteDeleteAsync(ct);
        await db.Characters.Where(x => x.CampaignId == id).ExecuteDeleteAsync(ct);
        await db.Branches.Where(x => x.CampaignId == id).ExecuteDeleteAsync(ct);
        await db.Campaigns.Where(x => x.Id == id).ExecuteDeleteAsync(ct);
        await transaction.CommitAsync(ct);
    }
}
