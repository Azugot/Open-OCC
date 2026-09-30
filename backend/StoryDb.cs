using Microsoft.EntityFrameworkCore;

namespace Story;

public class StoryDb(DbContextOptions<StoryDb> options) : DbContext(options)
{
    public DbSet<Campaign> Campaigns => Set<Campaign>();
    public DbSet<World> Worlds => Set<World>();
    public DbSet<WorldVersion> WorldVersions => Set<WorldVersion>();
    public DbSet<Branch> Branches => Set<Branch>();
    public DbSet<Checkpoint> Checkpoints => Set<Checkpoint>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<GenerationRun> GenerationRuns => Set<GenerationRun>();
    public DbSet<ImportedSource> Sources => Set<ImportedSource>();
    public DbSet<ImportJob> ImportJobs => Set<ImportJob>();
    public DbSet<ImportSegment> Segments => Set<ImportSegment>();
    public DbSet<ImportSection> ImportSections => Set<ImportSection>();
    public DbSet<ImportProposal> ImportProposals => Set<ImportProposal>();
    public DbSet<ImportIssue> ImportIssues => Set<ImportIssue>();
    public DbSet<ImportStageResult> ImportResults => Set<ImportStageResult>();
    public DbSet<Fact> Facts => Set<Fact>();
    public DbSet<FactEvidence> Evidence => Set<FactEvidence>();
    public DbSet<FactCorrection> FactCorrections => Set<FactCorrection>();
    public DbSet<ProviderProfile> Providers => Set<ProviderProfile>();
    public DbSet<NarrativeThread> NarrativeThreads => Set<NarrativeThread>();
    public DbSet<Character> Characters => Set<Character>();
    public DbSet<Relationship> Relationships => Set<Relationship>();
    public DbSet<KnowledgeRecord> Knowledge => Set<KnowledgeRecord>();
    public DbSet<MechanicsLedgerEntry> Mechanics => Set<MechanicsLedgerEntry>();
    public DbSet<StoryEvent> Events => Set<StoryEvent>();
    public DbSet<CampaignSummary> Summaries => Set<CampaignSummary>();
    public DbSet<AppSetting> Settings => Set<AppSetting>();
    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<AppSetting>().HasKey(x => x.Key);
        b.Entity<Branch>().Property(x => x.Revision).IsConcurrencyToken();
        b.Entity<ImportJob>().Property(x => x.Status).IsConcurrencyToken();
        b.Entity<GenerationRun>().Property(x => x.Status).IsConcurrencyToken();
        b.Entity<ImportJob>().Property(x => x.ProposalRevision).IsConcurrencyToken();
        b.Entity<ImportJob>().Property(x => x.LeaseOwner).IsConcurrencyToken();
        b.Entity<ImportSection>().HasOne<ImportJob>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ImportProposal>().HasOne<ImportJob>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ImportIssue>().HasOne<ImportJob>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ImportStageResult>().HasOne<ImportJob>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ImportSection>().HasIndex(x => new { x.JobId, x.Ordinal }).IsUnique();
        b.Entity<ImportProposal>().HasIndex(x => new { x.JobId, x.Key, x.Revision }).IsUnique();
        b.Entity<ImportIssue>().HasIndex(x => new { x.JobId, x.Code }).IsUnique();
        b.Entity<ImportStageResult>().HasIndex(x => new { x.JobId, x.Stage, x.Ordinal }).IsUnique();
        b.Entity<Campaign>().HasOne<WorldVersion>().WithMany().HasForeignKey(x => x.WorldVersionId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<WorldVersion>().HasOne<World>().WithMany().HasForeignKey(x => x.WorldId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Branch>().HasOne<Campaign>().WithMany().HasForeignKey(x => x.CampaignId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Checkpoint>().HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Message>().HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<GenerationRun>().HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ImportedSource>().HasOne<Campaign>().WithMany().HasForeignKey(x => x.CampaignId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ImportJob>().HasOne<ImportedSource>().WithMany().HasForeignKey(x => x.SourceId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ImportJob>().HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ImportSegment>().HasOne<ImportJob>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Fact>().HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<FactEvidence>().HasOne<Fact>().WithMany().HasForeignKey(x => x.FactId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<FactEvidence>().HasOne<ImportSegment>().WithMany().HasForeignKey(x => x.SegmentId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<FactCorrection>().HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<FactCorrection>().HasOne<Fact>().WithMany().HasForeignKey(x => x.FactId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<NarrativeThread>().HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Character>().HasOne<Campaign>().WithMany().HasForeignKey(x => x.CampaignId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Relationship>().HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Relationship>().HasOne<Character>().WithMany().HasForeignKey(x => x.FromCharacterId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Relationship>().HasOne<Character>().WithMany().HasForeignKey(x => x.ToCharacterId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<KnowledgeRecord>().HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<KnowledgeRecord>().HasOne<Character>().WithMany().HasForeignKey(x => x.CharacterId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<KnowledgeRecord>().HasOne<Fact>().WithMany().HasForeignKey(x => x.FactId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<MechanicsLedgerEntry>().HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<StoryEvent>().HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<CampaignSummary>().HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Message>().HasIndex(x => new { x.BranchId, x.Sequence }).IsUnique();
        b.Entity<Checkpoint>().HasIndex(x => new { x.BranchId, x.Sequence }).IsUnique();
        b.Entity<ImportSegment>().HasIndex(x => new { x.JobId, x.Ordinal }).IsUnique();
        b.Entity<FactEvidence>().HasIndex(x => new { x.FactId, x.SegmentId }).IsUnique();
        b.Entity<FactCorrection>().HasIndex(x => new { x.BranchId, x.CreatedAt });
        b.Entity<NarrativeThread>().HasIndex(x => new { x.BranchId, x.Status, x.Importance });
        b.Entity<WorldVersion>().HasIndex(x => new { x.WorldId, x.Version }).IsUnique();
        b.Entity<Character>().HasIndex(x => new { x.CampaignId, x.Name }).IsUnique();
        b.Entity<Relationship>().HasIndex(x => new { x.BranchId, x.FromCharacterId, x.ToCharacterId }).IsUnique();
        b.Entity<KnowledgeRecord>().HasIndex(x => new { x.BranchId, x.CharacterId, x.Subject }).IsUnique();
        b.Entity<MechanicsLedgerEntry>().HasIndex(x => new { x.BranchId, x.Sequence });
        b.Entity<StoryEvent>().HasIndex(x => new { x.BranchId, x.Sequence });
        b.Entity<CampaignSummary>().HasIndex(x => x.BranchId).IsUnique();
    }
}
