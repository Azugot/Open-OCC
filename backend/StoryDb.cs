using Microsoft.EntityFrameworkCore;

namespace Story;

public class StoryDb(DbContextOptions<StoryDb> options) : DbContext(options)
{
    public DbSet<Campaign> Campaigns => Set<Campaign>();
    public DbSet<Branch> Branches => Set<Branch>();
    public DbSet<Checkpoint> Checkpoints => Set<Checkpoint>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<GenerationRun> GenerationRuns => Set<GenerationRun>();
    public DbSet<ImportedSource> Sources => Set<ImportedSource>();
    public DbSet<ImportJob> ImportJobs => Set<ImportJob>();
    public DbSet<ImportSegment> Segments => Set<ImportSegment>();
    public DbSet<Fact> Facts => Set<Fact>();
    public DbSet<FactEvidence> Evidence => Set<FactEvidence>();
    public DbSet<ProviderProfile> Providers => Set<ProviderProfile>();
    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Branch>().Property(x => x.Revision).IsConcurrencyToken();
        b.Entity<ImportJob>().Property(x => x.Status).IsConcurrencyToken();
        b.Entity<GenerationRun>().Property(x => x.Status).IsConcurrencyToken();
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
        b.Entity<Message>().HasIndex(x => new { x.BranchId, x.Sequence }).IsUnique();
        b.Entity<Checkpoint>().HasIndex(x => new { x.BranchId, x.Sequence }).IsUnique();
        b.Entity<ImportSegment>().HasIndex(x => new { x.JobId, x.Ordinal }).IsUnique();
        b.Entity<FactEvidence>().HasIndex(x => new { x.FactId, x.SegmentId }).IsUnique();
    }
}
