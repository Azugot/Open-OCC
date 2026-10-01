using Microsoft.EntityFrameworkCore;

namespace Story;

public static class ImportReview
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/imports/{id:guid}/draft", async (Guid id, StoryDb db, CancellationToken ct) =>
        {
            var job = await Job(db, id, ct);
            var source = await db.Sources.Where(x => x.Id == job.SourceId).Select(x => new { x.Id, x.FileName, x.Sha256, size = x.Bytes.Length }).SingleAsync(ct);
            return Results.Ok(new { job, source, resume = job.ResumeJson is null ? null : Json.Read<ResumeProposal>(job.ResumeJson),
                issues = await db.ImportIssues.Where(x => x.JobId == id).OrderByDescending(x => x.Blocking).ThenBy(x => x.Code).ToListAsync(ct),
                groups = await db.ImportProposals.Where(x => x.JobId == id && x.Current).GroupBy(x => x.Category).Select(x => new { category = x.Key, count = x.Count() }).ToListAsync(ct),
                sections = await db.ImportSections.CountAsync(x => x.JobId == id, ct) });
        });
        app.MapGet("/api/imports/{id:guid}/proposals", async (Guid id, string? category, string? q, int? page, bool? history, StoryDb db, CancellationToken ct) =>
        {
            await Job(db, id, ct);
            var rows = db.ImportProposals.Where(x => x.JobId == id && (history == true || x.Current));
            if (!string.IsNullOrWhiteSpace(category)) rows = rows.Where(x => x.Category == category);
            if (!string.IsNullOrWhiteSpace(q)) rows = rows.Where(x => x.ContentJson.ToLower().Contains(q.ToLower()));
            var total = await rows.CountAsync(ct); var index = Math.Max(0, page ?? 0);
            var items = await rows.OrderBy(x => x.Key).ThenByDescending(x => x.Revision).Skip(index * 20).Take(20).ToListAsync(ct);
            return Results.Ok(new { total, page = index, items = items.Select(x => new { proposal = x, claim = Json.Read<ImportClaim>(x.ContentJson) }) });
        });
        app.MapGet("/api/imports/{id:guid}/evidence", async (Guid id, string? ordinals, int? page, StoryDb db, CancellationToken ct) =>
        {
            await Job(db, id, ct);
            var rows = db.Segments.Where(x => x.JobId == id);
            if (!string.IsNullOrWhiteSpace(ordinals))
            {
                var parts = ordinals.Split(',');
                if (parts.Length > 64 || parts.Any(x => !int.TryParse(x, out _))) throw new InvalidOperationException("Provide up to 64 passage ordinals.");
                var ids = parts.Select(int.Parse).ToArray(); rows = rows.Where(x => ids.Contains(x.Ordinal));
            }
            return Results.Ok(new { total = await rows.CountAsync(ct), items = await ImportDocument.Load(db, await Job(db, id, ct), rows.OrderBy(x => x.Ordinal).Skip(Math.Max(0, page ?? 0) * 20).Take(20), ct) });
        });
        app.MapPut("/api/imports/{id:guid}/proposals/{proposalId:guid}", async (Guid id, Guid proposalId, EditProposal request, StoryDb db, CancellationToken ct) =>
        {
            var job = await Editable(db, id, request.Revision, ct);
            var row = await db.ImportProposals.SingleOrDefaultAsync(x => x.Id == proposalId && x.JobId == id && x.Current, ct) ?? throw new KeyNotFoundException();
            if (request.Claim.Key != row.Key || request.Claim.Category != row.Category) throw new InvalidOperationException("A claim's stable key and category cannot be changed.");
            var ordinals = (await db.Segments.Where(x => x.JobId == id).Select(x => x.Ordinal).ToArrayAsync(ct)).ToHashSet();
            Reconstruction.ValidateClaim(request.Claim, ordinals, (await db.ImportProposals.Where(x => x.JobId == id).Select(x => x.Key).ToArrayAsync(ct)).ToHashSet(), stored: true);
            row.Current = false; job.ProposalRevision++;
            db.ImportProposals.Add(new ImportProposal { JobId = id, Key = row.Key, Category = row.Category, Revision = job.ProposalRevision, ContentJson = Json.Write(request.Claim), Excluded = request.Excluded });
            await db.SaveChangesAsync(ct); return Results.Ok(new { revision = job.ProposalRevision });
        });
        app.MapPut("/api/imports/{id:guid}/issues/{issueId:guid}", async (Guid id, Guid issueId, ResolveImportIssue request, StoryDb db, CancellationToken ct) =>
        {
            var job = await Editable(db, id, request.Revision, ct);
            CampaignService.RequireText(request.Resolution, 2000, "Resolution or acknowledgment");
            var row = await db.ImportIssues.SingleOrDefaultAsync(x => x.Id == issueId && x.JobId == id, ct) ?? throw new KeyNotFoundException();
            row.Resolution = request.Resolution.Trim(); job.ProposalRevision++;
            await db.SaveChangesAsync(ct); return Results.Ok(new { revision = job.ProposalRevision });
        });
        app.MapPut("/api/imports/{id:guid}/resume", async (Guid id, EditResume request, StoryDb db, CancellationToken ct) =>
        {
            var job = await Editable(db, id, request.Revision, ct);
            Reconstruction.ValidateResume(request.Resume, (await db.Segments.Where(x => x.JobId == id).Select(x => x.Ordinal).ToArrayAsync(ct)).ToHashSet());
            job.ResumeJson = Json.Write(request.Resume); job.ProposedStateJson = request.Resume.State is null ? null : Json.Write(request.Resume.State); job.ProposalRevision++;
            await db.SaveChangesAsync(ct); return Results.Ok(new { revision = job.ProposalRevision });
        });
        app.MapPost("/api/imports/{id:guid}/approval", async (Guid id, ApproveReconstruction request, StoryDb db, CancellationToken ct) => { await Approve(db, id, request, ct); return Results.NoContent(); });
        app.MapPost("/api/imports/{id:guid}/reanalyze", async (Guid id, ImportOptions request, StoryDb db, ProviderRouting routing, CancellationToken ct) =>
        {
            Reconstruction.ValidateOptions(request);
            var old = await db.ImportJobs.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException();
            var branch = await db.Branches.SingleAsync(x => x.Id == old.BranchId, ct);
            var routed = await routing.Resolve(db, "reconstruction", ct);
            var job = new ImportJob { SourceId = old.SourceId, BranchId = old.BranchId, ExpectedCheckpointId = branch.HeadCheckpointId,
                Method = Reconstruction.Method, Provider = routed.Profile.Id, Model = routed.Profile.Model,
                InputCharacterLimit = request.InputCharacterLimit, OutputTokenLimit = request.OutputTokenLimit, MaxCalls = request.MaxCalls };
            db.ImportJobs.Add(job); await db.SaveChangesAsync(ct); return Results.Ok(job);
        });
        app.MapPost("/api/imports/{id:guid}/resume-processing", async (Guid id, RetryImport request, StoryDb db, ProviderRouting routing, CancellationToken ct) =>
        {
            var job = await Job(db, id, ct);
            if (job.Status is not ("paused" or "cancelled" or "failed" or "prepared")) throw new InvalidOperationException("Only stopped or prepared imports can start or resume.");
            if (request.Provider is not null)
            {
                var routed = await routing.ResolveCaptured(db, request.Provider, request.Model ?? "", ct);
                job.Provider = routed.Profile.Id; job.Model = routed.Profile.Model;
            }
            if (request.MaxCalls is { } max) { Reconstruction.ValidateOptions(new(job.InputCharacterLimit, job.OutputTokenLimit, max)); if (max <= job.Calls) throw new InvalidOperationException("Call budget must exceed the calls already used."); job.MaxCalls = max; }
            var limits = new ImportOptions(request.InputCharacterLimit ?? job.InputCharacterLimit, request.OutputTokenLimit ?? job.OutputTokenLimit, job.MaxCalls);
            Reconstruction.ValidateOptions(limits); job.InputCharacterLimit = limits.InputCharacterLimit; job.OutputTokenLimit = limits.OutputTokenLimit;
            job.Status = "queued"; job.Error = null; job.LeaseOwner = null; job.LeaseUntil = null; job.ProposalRevision++;
            await db.SaveChangesAsync(ct); return Results.Ok(job);
        });
    }
    public static async Task<ImportJob> Job(StoryDb db, Guid id, CancellationToken ct)
    {
        var job = await db.ImportJobs.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException();
        if (!Reconstruction.IsAgent(job.Method)) throw new InvalidOperationException("This is a legacy import. Reanalyze its preserved source to use agent review.");
        return job;
    }
    private static async Task<ImportJob> Editable(StoryDb db, Guid id, int revision, CancellationToken ct)
    {
        var job = await Job(db, id, ct);
        if (job.Status != "review" || !job.ReviewCompleted) throw new InvalidOperationException("Complete reconstruction and agent review before editing.");
        if (revision != job.ProposalRevision) throw new DbUpdateConcurrencyException("This proposal changed. Reload before saving.");
        return job;
    }
    public static async Task Approve(StoryDb db, Guid id, ApproveReconstruction request, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        var job = await Editable(db, id, request.Revision, ct);
        if (job.Processed != job.Total) throw new InvalidOperationException("Source coverage is incomplete.");
        if (await db.ImportIssues.AnyAsync(x => x.JobId == id && x.Blocking && x.Resolution == "", ct)) throw new InvalidOperationException("Resolve or acknowledge all blocking review issues first.");
        var resume = job.ResumeJson is null ? null : Json.Read<ResumeProposal>(job.ResumeJson);
        if (resume?.State is null || !Reconstruction.Usable(resume.State)) throw new InvalidOperationException("Provide a usable final scene and objective. An empty starting state cannot be approved.");
        if (job.Method == Reconstruction.Method && string.IsNullOrWhiteSpace(resume.PlayerCharacterName))
            throw new InvalidOperationException("Identify the player-controlled character before approving this import. Do not guess from the participant list.");
        resume.State.Validate();
        var branch = await db.Branches.SingleAsync(x => x.Id == job.BranchId, ct);
        if (StoryEngine.IsExecuting(branch.Id) || await db.GenerationRuns.AnyAsync(x => x.BranchId == branch.Id && (x.Status == "running" || x.Status == "paused"), ct))
            throw new InvalidOperationException("Finish, retry or cancel the existing story draft before approving an import.");
        if (branch.HeadCheckpointId != request.ExpectedCheckpointId || branch.HeadCheckpointId != job.ExpectedCheckpointId) throw new DbUpdateConcurrencyException("This branch advanced. Reanalyze on a fork before approval.");
        var head = await db.Checkpoints.SingleAsync(x => x.Id == branch.HeadCheckpointId, ct);
        var rows = await db.ImportProposals.Where(x => x.JobId == id && x.Current && !x.Excluded).ToListAsync(ct);
        var claims = rows.Select(x => Json.Read<ImportClaim>(x.ContentJson)).ToArray();
        var segments = await db.Segments.Where(x => x.JobId == id).OrderBy(x => x.Ordinal).ToListAsync(ct);
        var byOrdinal = segments.ToDictionary(x => x.Ordinal);
        var stableKeys = (await db.ImportProposals.Where(x => x.JobId == id).Select(x => x.Key).ToArrayAsync(ct)).ToHashSet();
        foreach (var c in claims)
        {
            Reconstruction.ValidateClaim(c, byOrdinal.Keys.ToHashSet(), stableKeys, stored: true);
            Reconstruction.ValidateRanges(c, segments.ToArray());
        }
        Reconstruction.ValidateResume(resume, byOrdinal.Keys.ToHashSet());
        foreach (var c in claims.Where(x => x.Disposition == "current"))
            if (c.Category == "inventory" && resume.State.Inventory.GetValueOrDefault(c.Subject) != c.Amount || c.Category == "skill" && resume.State.Skills.GetValueOrDefault(c.Subject) != c.Value)
                throw new InvalidOperationException($"The resume state disagrees with {c.Key}. Correct or exclude the claim before approval.");
        var importedWorld = ImportedWorldCompiler.Compile(StoryWorld.From(head), Json.Read<StoryState>(head.StateJson), job, resume, claims, byOrdinal);
        var checkpoint = new Checkpoint { BranchId = branch.Id, Sequence = head.Sequence + 1, Label = "Agent-reviewed import", StateJson = Json.Write(resume.State), WorldJson = Json.Write(importedWorld) };
        db.ImportedHistory.Add(new ImportedHistory { BranchId = branch.Id, SourceId = job.SourceId, JobId = job.Id, EffectiveSequence = checkpoint.Sequence });
        var characters = await db.Characters.Where(x => x.CampaignId == branch.CampaignId).ToListAsync(ct);
        // Campaign registrations are identities; branch-specific descriptions and motives live in the checkpoint.
        foreach (var entity in importedWorld.Entities.Where(x => x.Kind is "character" or "being"))
            if (!characters.Any(x => x.Name.Equals(entity.Name, StringComparison.OrdinalIgnoreCase)))
            {
                var character = new Character { CampaignId = branch.CampaignId, Name = entity.Name, Description = entity.Description,
                    Goals = importedWorld.Characters.SingleOrDefault(x => x.EntityId == entity.Id)?.Goal ?? "" };
                characters.Add(character); db.Characters.Add(character);
            }
        Character? RegisteredCharacter(string reference)
        {
            var named = claims.FirstOrDefault(c => c.Key == reference && (c.Category == "character" || c.EntityKind is "character" or "being"))?.Subject ?? reference;
            if (named.Equals("player", StringComparison.OrdinalIgnoreCase)) named = resume.PlayerCharacterName ?? named;
            var visible = importedWorld.Entities.Where(e => e.Kind is "character" or "being").Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var identities = characters.Where(c => visible.Contains(c.Name)).ToArray();
            var exact = identities.SingleOrDefault(c => c.Name.Equals(named, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;
            var candidates = identities.Where(c => c.Name.StartsWith(named + " ", StringComparison.OrdinalIgnoreCase)).ToArray();
            return candidates.Length == 1 ? candidates[0] : null;
        }
        var facts = new Dictionary<string, Fact>();
        foreach (var c in claims)
        {
            var fact = new Fact { BranchId = branch.Id, JobId = id, EffectiveSequence = checkpoint.Sequence, Text = c.Text, Kind = c.Kind, Visibility = c.Visibility,
                Confidence = c.Confidence, KnownByJson = Json.Write(c.KnownBy.Select(name => RegisteredCharacter(name)?.Name ?? name).Distinct()), Provenance = "user-confirmed", ReviewStatus = c.Disposition == "current" ? "accepted" : "historical" };
            db.Facts.Add(fact); facts[c.Key] = fact;
            foreach (var ordinal in c.EvidenceOrdinals.Distinct()) db.Evidence.Add(new FactEvidence { FactId = fact.Id, SegmentId = byOrdinal[ordinal].Id });
            if (c.Category == "event" || c.Disposition == "historical") db.Events.Add(new StoryEvent { BranchId = branch.Id, Sequence = checkpoint.Sequence, Type = "imported", Summary = $"Source passage {c.EvidenceOrdinals.Min() + 1}: {c.Text}" });
        }
        foreach (var c in claims.Where(x => x.Disposition == "current"))
        {
            if (c.Category == "relationship" && c.Kind is not ("belief" or "rumor"))
            {
                var from = RegisteredCharacter(c.Subject);
                var to = RegisteredCharacter(c.Target);
                if (from is null || to is null) throw new InvalidOperationException($"Relationship references an unresolved character: {c.Subject} / {c.Target}. Edit or exclude it first.");
                var row = db.Relationships.Local.SingleOrDefault(x => x.BranchId == branch.Id && x.FromCharacterId == from.Id && x.ToCharacterId == to.Id)
                    ?? await db.Relationships.SingleOrDefaultAsync(x => x.BranchId == branch.Id && x.FromCharacterId == from.Id && x.ToCharacterId == to.Id, ct);
                if (row is null)
                {
                    row = new Relationship { BranchId = branch.Id, FromCharacterId = from.Id, ToCharacterId = to.Id,
                        Label = c.Value, Score = c.Amount, Notes = c.Text, EffectiveSequence = checkpoint.Sequence };
                    db.Relationships.Add(row);
                }
            }
            if (c.Category == "knowledge")
            {
                var character = RegisteredCharacter(c.Subject) ?? throw new InvalidOperationException("Knowledge references an unresolved character. Edit or exclude it first.");
                var row = db.Knowledge.Local.SingleOrDefault(x => x.BranchId == branch.Id && x.CharacterId == character.Id && x.Subject == c.Text)
                    ?? await db.Knowledge.SingleOrDefaultAsync(x => x.BranchId == branch.Id && x.CharacterId == character.Id && x.Subject == c.Text, ct);
                if (c.Target != "" && !facts.ContainsKey(c.Target)) throw new InvalidOperationException("Knowledge references an excluded or unresolved fact. Edit or exclude it first.");
                if (row is null)
                {
                    row = new KnowledgeRecord { BranchId = branch.Id, CharacterId = character.Id, Subject = c.Text,
                        BeliefType = c.Kind, Confidence = c.Confidence, FactId = facts.GetValueOrDefault(c.Target)?.Id, Source = "imported", EffectiveSequence = checkpoint.Sequence };
                    db.Knowledge.Add(row);
                }
            }
            if (c.Category == "thread" && c.Kind is not ("belief" or "rumor")) db.NarrativeThreads.Add(new NarrativeThread { BranchId = branch.Id, Title = c.Subject, Details = c.Text, Kind = c.Value, Status = c.Target, Importance = c.Amount, EffectiveSequence = checkpoint.Sequence });
        }
        // Imported rules and places are checkpoint-owned facts/entities. Changing a shared campaign
        // world template here would retroactively change sibling branches' director context.
        var summary = await db.Summaries.SingleOrDefaultAsync(x => x.BranchId == branch.Id, ct);
        var text = (job.Summary ?? "Reviewed campaign reconstruction.") + $"\nLatest player action: {resume.LatestAction}\nResponse pending: {resume.ReplyPending}";
        if (summary is null) db.Summaries.Add(new CampaignSummary { BranchId = branch.Id, ThroughSequence = checkpoint.Sequence, Text = text });
        else { summary.Text = text; summary.ThroughSequence = checkpoint.Sequence; summary.UpdatedAt = DateTime.UtcNow; }
        db.Checkpoints.Add(checkpoint);
        db.Messages.Add(new Message { BranchId = branch.Id, Sequence = checkpoint.Sequence, Role = "system", Content = $"Reviewed import approved. Resume at {resume.State.Location}. Objective: {resume.State.Objective}. Open Live world for reconstructed characters, graph, events and memories.", Provider = "manual-review", Model = "agent-v3" });
        branch.HeadCheckpointId = checkpoint.Id; branch.Revision++; job.Status = "approved"; job.ProposalRevision++;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
}
