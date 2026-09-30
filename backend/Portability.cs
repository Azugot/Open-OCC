using Microsoft.EntityFrameworkCore;

namespace Story;

public sealed record PortableExport(
    int Version,
    Campaign Campaign,
    List<Branch> Branches,
    List<Checkpoint> Checkpoints,
    List<Message> Messages,
    List<GenerationRun> Runs,
    List<ImportedSource> Sources,
    List<ImportJob> Jobs,
    List<ImportSegment> Segments,
    List<Fact> Facts,
    List<FactEvidence> Evidence,
    List<FactCorrection> Corrections,
    List<NarrativeThread> Threads,
    List<World>? Worlds = null,
    List<WorldVersion>? WorldVersions = null,
    List<Character>? Characters = null,
    List<Relationship>? Relationships = null,
    List<KnowledgeRecord>? Knowledge = null,
    List<MechanicsLedgerEntry>? Mechanics = null,
    List<StoryEvent>? Events = null,
    List<CampaignSummary>? Summaries = null,
    List<ImportSection>? ImportSections = null,
    List<ImportProposal>? ImportProposals = null,
    List<ImportIssue>? ImportIssues = null,
    List<ImportStageResult>? ImportResults = null);
public sealed record PortableImportRequest(PortableExport Export, string? Name = null);

public static class PortabilityService
{
    public static async Task<PortableExport> Export(StoryDb db, Guid campaignId, CancellationToken ct)
    {
        var campaign = await db.Campaigns.AsNoTracking().SingleOrDefaultAsync(x => x.Id == campaignId, ct) ?? throw new KeyNotFoundException();
        var branches = await db.Branches.AsNoTracking().Where(x => x.CampaignId == campaignId).ToListAsync(ct);
        var branchIds = branches.Select(x => x.Id).ToArray();
        var checkpoints = await db.Checkpoints.AsNoTracking().Where(x => branchIds.Contains(x.BranchId)).ToListAsync(ct);
        var messages = await db.Messages.AsNoTracking().Where(x => branchIds.Contains(x.BranchId)).ToListAsync(ct);
        var runs = await db.GenerationRuns.AsNoTracking().Where(x => branchIds.Contains(x.BranchId)).ToListAsync(ct);
        var sources = await db.Sources.AsNoTracking().Where(x => x.CampaignId == campaignId).ToListAsync(ct);
        var sourceIds = sources.Select(x => x.Id).ToArray();
        var jobs = await db.ImportJobs.AsNoTracking().Where(x => sourceIds.Contains(x.SourceId)).ToListAsync(ct);
        var jobIds = jobs.Select(x => x.Id).ToArray();
        var segments = await db.Segments.AsNoTracking().Where(x => jobIds.Contains(x.JobId)).ToListAsync(ct);
        var facts = await db.Facts.AsNoTracking().Where(x => branchIds.Contains(x.BranchId)).ToListAsync(ct);
        var factIds = facts.Select(x => x.Id).ToArray();
        var evidence = await db.Evidence.AsNoTracking().Where(x => factIds.Contains(x.FactId)).ToListAsync(ct);
        var corrections = await db.FactCorrections.AsNoTracking().Where(x => branchIds.Contains(x.BranchId)).ToListAsync(ct);
        var threads = await db.NarrativeThreads.AsNoTracking().Where(x => branchIds.Contains(x.BranchId)).ToListAsync(ct);
        var worldVersion = campaign.WorldVersionId is { } selectedVersion ? await db.WorldVersions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == selectedVersion, ct) : null;
        var worlds = worldVersion is null ? [] : await db.Worlds.AsNoTracking().Where(x => x.Id == worldVersion.WorldId).ToListAsync(ct);
        var worldVersions = worlds.Count == 0 ? [] : await db.WorldVersions.AsNoTracking().Where(x => x.WorldId == worlds[0].Id).ToListAsync(ct);
        var characters = await db.Characters.AsNoTracking().Where(x => x.CampaignId == campaignId).ToListAsync(ct);
        var characterIds = characters.Select(x => x.Id).ToArray();
        var relationships = await db.Relationships.AsNoTracking().Where(x => branchIds.Contains(x.BranchId) && characterIds.Contains(x.FromCharacterId) && characterIds.Contains(x.ToCharacterId)).ToListAsync(ct);
        var knowledge = await db.Knowledge.AsNoTracking().Where(x => branchIds.Contains(x.BranchId) && characterIds.Contains(x.CharacterId)).ToListAsync(ct);
        var mechanics = await db.Mechanics.AsNoTracking().Where(x => branchIds.Contains(x.BranchId)).ToListAsync(ct);
        var events = await db.Events.AsNoTracking().Where(x => branchIds.Contains(x.BranchId)).ToListAsync(ct);
        var summaries = await db.Summaries.AsNoTracking().Where(x => branchIds.Contains(x.BranchId)).ToListAsync(ct);
        return new(3, campaign, branches, checkpoints, messages, runs, sources, jobs, segments, facts, evidence, corrections, threads, worlds, worldVersions, characters, relationships, knowledge, mechanics, events, summaries,
            await db.ImportSections.AsNoTracking().Where(x => jobIds.Contains(x.JobId)).ToListAsync(ct),
            await db.ImportProposals.AsNoTracking().Where(x => jobIds.Contains(x.JobId)).ToListAsync(ct),
            await db.ImportIssues.AsNoTracking().Where(x => jobIds.Contains(x.JobId)).ToListAsync(ct),
            await db.ImportResults.AsNoTracking().Where(x => jobIds.Contains(x.JobId)).ToListAsync(ct));
    }

    public static async Task<Branch> Import(StoryDb db, PortableExport portable, string? nameOverride, CancellationToken ct)
    {
        if (portable.Version is not (1 or 2 or 3) || portable.Campaign is null || portable.Branches is null || portable.Checkpoints is null)
            throw new InvalidOperationException("Unsupported or incomplete Open-OCC export.");
        var campaign = new Campaign { Name = string.IsNullOrWhiteSpace(nameOverride) ? portable.Campaign.Name : nameOverride.Trim() };
        CampaignService.RequireText(campaign.Name, 100, "Campaign name");
        var branchMap = portable.Branches.ToDictionary(x => x.Id, _ => Guid.NewGuid());
        var checkpointMap = portable.Checkpoints.ToDictionary(x => x.Id, _ => Guid.NewGuid());
        var sourceMap = portable.Sources.ToDictionary(x => x.Id, _ => Guid.NewGuid());
        var jobMap = portable.Jobs.ToDictionary(x => x.Id, _ => Guid.NewGuid());
        var segmentMap = portable.Segments.ToDictionary(x => x.Id, _ => Guid.NewGuid());
        var factMap = portable.Facts.ToDictionary(x => x.Id, _ => Guid.NewGuid());
        var threadMap = portable.Threads.ToDictionary(x => x.Id, _ => Guid.NewGuid());
        var worlds = (portable.Worlds ?? []).Select(x => new World { Id = Guid.NewGuid(), Name = x.Name, Description = x.Description, CreatedAt = x.CreatedAt }).ToList();
        var worldMap = (portable.Worlds ?? []).Zip(worlds).ToDictionary(x => x.First.Id, x => x.Second.Id);
        var worldVersions = (portable.WorldVersions ?? []).Where(x => worldMap.ContainsKey(x.WorldId)).Select(x => new WorldVersion { Id = Guid.NewGuid(), WorldId = worldMap[x.WorldId], Version = x.Version, RulesJson = x.RulesJson, LocationsJson = x.LocationsJson, FactionsJson = x.FactionsJson, AuthorInstructions = x.AuthorInstructions, CreatedAt = x.CreatedAt }).ToList();
        var worldVersionMap = (portable.WorldVersions ?? []).Zip(worldVersions).ToDictionary(x => x.First.Id, x => x.Second.Id);
        campaign.WorldVersionId = portable.Campaign.WorldVersionId is { } selected && worldVersionMap.TryGetValue(selected, out var mappedVersion) ? mappedVersion : null;
        var branches = portable.Branches.Select(x => new Branch { Id = branchMap[x.Id], CampaignId = campaign.Id, Name = x.Name, ParentBranchId = x.ParentBranchId is { } p && branchMap.TryGetValue(p, out var mappedParent) ? mappedParent : null, ForkCheckpointId = x.ForkCheckpointId is { } f && checkpointMap.TryGetValue(f, out var mappedFork) ? mappedFork : null, HeadCheckpointId = checkpointMap[x.HeadCheckpointId], Revision = x.Revision, CreatedAt = x.CreatedAt }).ToList();
        var checkpoints = portable.Checkpoints.Select(x => new Checkpoint { Id = checkpointMap[x.Id], BranchId = branchMap[x.BranchId], Sequence = x.Sequence, Label = x.Label, StateJson = x.StateJson, CreatedAt = x.CreatedAt }).ToList();
        var sourceRows = portable.Sources.Select(x => new ImportedSource { Id = sourceMap[x.Id], CampaignId = campaign.Id, FileName = x.FileName, Sha256 = x.Sha256, Bytes = x.Bytes, CreatedAt = x.CreatedAt }).ToList();
        var jobs = portable.Jobs.Where(x => sourceMap.ContainsKey(x.SourceId) && branchMap.ContainsKey(x.BranchId)).Select(x => new ImportJob { Id = jobMap[x.Id], SourceId = sourceMap[x.SourceId], BranchId = branchMap[x.BranchId], ExpectedCheckpointId = checkpointMap.GetValueOrDefault(x.ExpectedCheckpointId), Status = x.Status is "processing" or "queued" ? "paused" : x.Status, Processed = x.Processed, Total = x.Total, Error = x.Error, Method = x.Method, Provider = x.Provider, Model = x.Model, ProposedStateJson = x.ProposedStateJson, ProposedThreadsJson = x.ProposedThreadsJson, Summary = x.Summary, InputTokens = x.InputTokens, OutputTokens = x.OutputTokens, AttemptCount = x.AttemptCount, CreatedAt = x.CreatedAt,
            Stage = x.Stage, StageCursor = x.StageCursor, ProposalRevision = x.ProposalRevision, Calls = x.Calls, MaxCalls = x.MaxCalls, InputCharacterLimit = x.InputCharacterLimit, OutputTokenLimit = x.OutputTokenLimit, ResumeJson = x.ResumeJson, ReviewCompleted = x.ReviewCompleted }).ToList();
        var segments = portable.Segments.Where(x => jobMap.ContainsKey(x.JobId)).Select(x => new ImportSegment { Id = segmentMap[x.Id], JobId = jobMap[x.JobId], Ordinal = x.Ordinal, Speaker = x.Speaker, Text = x.Text, Section = x.Section, Timestamp = x.Timestamp, Artifact = x.Artifact }).ToList();
        var proposalMap = (portable.ImportProposals ?? []).ToDictionary(x => x.Id, _ => Guid.NewGuid());
        var importSections = (portable.ImportSections ?? []).Where(x => jobMap.ContainsKey(x.JobId)).Select(x => new ImportSection { JobId = jobMap[x.JobId], Ordinal = x.Ordinal, Start = x.Start, End = x.End }).ToList();
        var proposals = (portable.ImportProposals ?? []).Where(x => jobMap.ContainsKey(x.JobId)).Select(x => new ImportProposal { Id = proposalMap[x.Id], JobId = jobMap[x.JobId], Key = x.Key, Category = x.Category, ContentJson = x.ContentJson, Revision = x.Revision, Current = x.Current, Excluded = x.Excluded }).ToList();
        var issues = (portable.ImportIssues ?? []).Where(x => jobMap.ContainsKey(x.JobId)).Select(x => new ImportIssue { JobId = jobMap[x.JobId], Code = x.Code, Text = x.Text, EvidenceJson = x.EvidenceJson, Blocking = x.Blocking, Resolution = x.Resolution }).ToList();
        var importResults = (portable.ImportResults ?? []).Where(x => jobMap.ContainsKey(x.JobId)).Select(x => new ImportStageResult { JobId = jobMap[x.JobId], Stage = x.Stage, Ordinal = x.Ordinal,
            ResultJson = x.Stage.EndsWith("-plan") ? Json.Write(Json.Read<Guid[]>(x.ResultJson).Select(oldId => proposalMap[oldId]).ToArray()) : x.ResultJson,
            Provider = x.Provider, Model = x.Model, InputTokens = x.InputTokens, OutputTokens = x.OutputTokens }).ToList();
        var facts = portable.Facts.Where(x => branchMap.ContainsKey(x.BranchId)).Select(x => new Fact { Id = factMap[x.Id], BranchId = branchMap[x.BranchId], JobId = x.JobId is { } j && jobMap.TryGetValue(j, out var mappedJob) ? mappedJob : null, EffectiveSequence = x.EffectiveSequence, Text = x.Text, Provenance = x.Provenance, ReviewStatus = x.ReviewStatus, Visibility = x.Visibility, Kind = x.Kind, Confidence = x.Confidence, KnownByJson = x.KnownByJson }).ToList();
        var evidence = portable.Evidence.Where(x => factMap.ContainsKey(x.FactId) && segmentMap.ContainsKey(x.SegmentId)).Select(x => new FactEvidence { Id = Guid.NewGuid(), FactId = factMap[x.FactId], SegmentId = segmentMap[x.SegmentId] }).ToList();
        var corrections = portable.Corrections.Where(x => branchMap.ContainsKey(x.BranchId) && factMap.ContainsKey(x.FactId)).Select(x => new FactCorrection { Id = Guid.NewGuid(), BranchId = branchMap[x.BranchId], FactId = factMap[x.FactId], PreviousText = x.PreviousText, NewText = x.NewText, Reason = x.Reason, CreatedAt = x.CreatedAt }).ToList();
        var threads = portable.Threads.Where(x => branchMap.ContainsKey(x.BranchId)).Select(x => new NarrativeThread { Id = threadMap[x.Id], BranchId = branchMap[x.BranchId], Title = x.Title, Details = x.Details, Kind = x.Kind, Status = x.Status, Importance = x.Importance, EffectiveSequence = x.EffectiveSequence }).ToList();
        var characterMap = (portable.Characters ?? []).ToDictionary(x => x.Id, _ => Guid.NewGuid());
        var characters = (portable.Characters ?? []).Select(x => new Character { Id = characterMap[x.Id], CampaignId = campaign.Id, Name = x.Name, Description = x.Description, Goals = x.Goals, Status = x.Status, CreatedAt = x.CreatedAt }).ToList();
        var relationships = (portable.Relationships ?? []).Where(x => branchMap.ContainsKey(x.BranchId) && characterMap.ContainsKey(x.FromCharacterId) && characterMap.ContainsKey(x.ToCharacterId)).Select(x => new Relationship { Id = Guid.NewGuid(), BranchId = branchMap[x.BranchId], FromCharacterId = characterMap[x.FromCharacterId], ToCharacterId = characterMap[x.ToCharacterId], Label = x.Label, Score = x.Score, Notes = x.Notes, EffectiveSequence = x.EffectiveSequence }).ToList();
        var knowledge = (portable.Knowledge ?? []).Where(x => branchMap.ContainsKey(x.BranchId) && characterMap.ContainsKey(x.CharacterId)).Select(x => new KnowledgeRecord { Id = Guid.NewGuid(), BranchId = branchMap[x.BranchId], CharacterId = characterMap[x.CharacterId], FactId = x.FactId is { } f && factMap.TryGetValue(f, out var mappedFact) ? mappedFact : null, Subject = x.Subject, BeliefType = x.BeliefType, Confidence = x.Confidence, EffectiveSequence = x.EffectiveSequence, Source = x.Source }).ToList();
        var mechanics = (portable.Mechanics ?? []).Where(x => branchMap.ContainsKey(x.BranchId)).Select(x => new MechanicsLedgerEntry { Id = Guid.NewGuid(), BranchId = branchMap[x.BranchId], Sequence = x.Sequence, Kind = x.Kind, Subject = x.Subject, Delta = x.Delta, Reason = x.Reason, Source = x.Source, CreatedAt = x.CreatedAt }).ToList();
        var events = (portable.Events ?? []).Where(x => branchMap.ContainsKey(x.BranchId)).Select(x => new StoryEvent { Id = Guid.NewGuid(), BranchId = branchMap[x.BranchId], Sequence = x.Sequence, Type = x.Type, Summary = x.Summary, MessageId = null, CreatedAt = x.CreatedAt }).ToList();
        var summaries = (portable.Summaries ?? []).Where(x => branchMap.ContainsKey(x.BranchId)).Select(x => new CampaignSummary { Id = Guid.NewGuid(), BranchId = branchMap[x.BranchId], ThroughSequence = x.ThroughSequence, Text = x.Text, UpdatedAt = x.UpdatedAt }).ToList();
        var messages = portable.Messages.Where(x => branchMap.ContainsKey(x.BranchId)).Select(x => new Message { Id = Guid.NewGuid(), BranchId = branchMap[x.BranchId], Sequence = x.Sequence, Role = x.Role, Content = x.Content, Provider = x.Provider, Model = x.Model, CreatedAt = x.CreatedAt }).ToList();
        var runs = portable.Runs.Where(x => branchMap.ContainsKey(x.BranchId)).Select(x => new GenerationRun { Id = Guid.NewGuid(), BranchId = branchMap[x.BranchId], ExpectedCheckpointId = checkpointMap.GetValueOrDefault(x.ExpectedCheckpointId), Action = x.Action, Status = x.Status, Provider = x.Provider, Model = x.Model, ContextJson = x.ContextJson, StateProposalJson = x.StateProposalJson, InputTokens = x.InputTokens, OutputTokens = x.OutputTokens, Error = x.Error, CreatedAt = x.CreatedAt }).ToList();
        db.AddRange(campaign); db.AddRange(worlds); db.AddRange(worldVersions); db.AddRange(branches); db.AddRange(checkpoints); db.AddRange(messages); db.AddRange(runs); db.AddRange(sourceRows); db.AddRange(jobs); db.AddRange(segments); db.AddRange(facts); db.AddRange(evidence); db.AddRange(corrections); db.AddRange(threads); db.AddRange(characters); db.AddRange(relationships); db.AddRange(knowledge); db.AddRange(mechanics); db.AddRange(events); db.AddRange(summaries);
        db.AddRange(importSections); db.AddRange(proposals); db.AddRange(issues); db.AddRange(importResults);
        await db.SaveChangesAsync(ct);
        return branches.OrderBy(x => x.CreatedAt).First();
    }
}
