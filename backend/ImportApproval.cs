using Microsoft.EntityFrameworkCore;

namespace Story;

public record ApprovalBlocker(string Code, string Message, Guid? ProposalId = null, Guid? IssueId = null, string? Field = null);
public record ApprovalReadiness(bool CanApprove, ApprovalBlocker[] Blockers);

public static class ImportApproval
{
    public static bool Structural(string code) => code.StartsWith("state:") || code is "resume:player" or "resume:scene" or "world:references";

    public static async Task<ApprovalReadiness> Evaluate(StoryDb db, ImportJob job, CancellationToken ct)
    {
        var blockers = new List<ApprovalBlocker>();
        if (job.Status != "review" || !job.ReviewCompleted) blockers.Add(new("review", "Complete reconstruction before approval."));
        if (job.Total == 0 || job.Processed != job.Total) blockers.Add(new("coverage", "Source coverage is incomplete."));
        var branch = await db.Branches.SingleAsync(x => x.Id == job.BranchId, ct);
        if (branch.HeadCheckpointId != job.ExpectedCheckpointId) blockers.Add(new("timeline", "This timeline advanced. Reconstruct on a new timeline from the original checkpoint."));
        if (StoryEngine.IsExecuting(branch.Id) || await db.GenerationRuns.AnyAsync(x => x.BranchId == branch.Id && (x.Status == "running" || x.Status == "paused"), ct))
            blockers.Add(new("run", "Finish, retry or cancel the existing story draft before approval."));
        foreach (var issue in await db.ImportIssues.Where(x => x.JobId == job.Id && x.Blocking && x.Resolution == "").ToListAsync(ct))
            if (!Structural(issue.Code)) blockers.Add(new(issue.Code, issue.Text, IssueId: issue.Id));
        var resume = job.ResumeJson is null ? null : Json.Read<ResumeProposal>(job.ResumeJson);
        if (resume?.State is null || !Reconstruction.Usable(resume.State)) blockers.Add(new("resume:scene", "Set a supported final location and immediate objective.", Field: "scene"));
        if (job.Method == Reconstruction.Method && string.IsNullOrWhiteSpace(resume?.PlayerCharacterName))
            blockers.Add(new("resume:player", "Identify the actual player character from the source.", Field: "playerCharacterName"));
        var rows = await db.ImportProposals.Where(x => x.JobId == job.Id && x.Current && !x.Excluded).ToListAsync(ct);
        var blocks = await db.Segments.Where(x => x.JobId == job.Id).ToDictionaryAsync(x => x.Ordinal, ct);
        var keys = (await db.ImportProposals.Where(x => x.JobId == job.Id).Select(x => x.Key).ToArrayAsync(ct)).ToHashSet();
        var allowed = blocks.Keys.ToHashSet();
        foreach (var row in rows)
        {
            try
            {
                var claim = Json.Read<ImportClaim>(row.ContentJson);
                Reconstruction.ValidateClaim(claim, allowed, keys, stored: true);
                Reconstruction.ValidateRanges(claim, blocks.Values.ToArray());
            }
            catch (InvalidOperationException error) { blockers.Add(new("claim:" + row.Key, error.Message, row.Id)); }
        }
        if (resume is not null)
        {
            try { Reconstruction.ValidateResume(resume, allowed); }
            catch (InvalidOperationException error) { blockers.Add(new("resume:evidence", error.Message, Field: "scene")); }
        }
        foreach (var conflict in ImportReview.StateConflicts(rows, resume)) blockers.Add(new("state:" + conflict.Key, conflict.Text, conflict.ProposalId));
        if (resume?.State is not null && !blockers.Any(x => x.Code.StartsWith("claim:") || x.Code == "resume:evidence"))
        {
            try
            {
                var head = await db.Checkpoints.SingleAsync(x => x.Id == job.ExpectedCheckpointId, ct);
                var claims = rows.Select(x => Json.Read<ImportClaim>(x.ContentJson)).ToArray();
                var world = ImportedWorldCompiler.Compile(StoryWorld.From(head), Json.Read<StoryState>(head.StateJson), job, resume, claims, blocks);
                string Name(string reference) => claims.FirstOrDefault(x => x.Key == reference && (x.Category == "character" || x.EntityKind is "character" or "being"))?.Subject ?? (reference == "player" ? resume.PlayerCharacterName ?? reference : reference);
                bool Character(string reference)
                {
                    var name = Name(reference);
                    var names = world.Entities.Where(x => x.Kind is "character" or "being").Select(x => x.Name).ToArray();
                    return names.Any(x => x.Equals(name, StringComparison.OrdinalIgnoreCase)) || names.Count(x => x.StartsWith(name + " ", StringComparison.OrdinalIgnoreCase)) == 1;
                }
                foreach (var row in rows)
                {
                    var claim = Json.Read<ImportClaim>(row.ContentJson);
                    if (claim.Disposition != "current") continue;
                    if (claim.Category == "relationship" && claim.Kind is not ("belief" or "rumor") && (!Character(claim.Subject) || !Character(claim.Target)))
                        blockers.Add(new("reference:" + claim.Key, "Relationship references an unresolved character. Correct or exclude it.", row.Id));
                    if (claim.Category == "knowledge" && (!Character(claim.Subject) || claim.Target != "" && !claims.Any(x => x.Key == claim.Target)))
                        blockers.Add(new("reference:" + claim.Key, "Knowledge references an unresolved character or excluded fact. Correct or exclude it.", row.Id));
                }
            }
            catch (InvalidOperationException error) { blockers.Add(new("world:references", error.Message, Field: "proposals")); }
        }
        return new(blockers.Count == 0, blockers.ToArray());
    }
}
