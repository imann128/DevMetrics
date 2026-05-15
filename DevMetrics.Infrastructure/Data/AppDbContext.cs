using DevMetrics.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace DevMetrics.Infrastructure.Data;

/// <summary>
/// The EF Core database context for DevMetrics.
/// Acts as both the <c>DbContext</c> and the implementation of
/// <see cref="Core.Interfaces.IUnitOfWork"/> (via <see cref="UnitOfWork"/>).
/// All schema configuration lives in <see cref="OnModelCreating"/> —
/// data annotations are deliberately avoided to keep entities in Core clean.
/// </summary>
public sealed class AppDbContext : DbContext
{
    /// <summary>
    /// Initialises the context with the given EF Core options.
    /// Called by the DI container after <see cref="Extensions.InfrastructureServiceExtensions.AddInfrastructure"/>
    /// registers the context with <c>AddDbContext</c>.
    /// </summary>
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    // ── DbSets ────────────────────────────────────────────────────────────────

    /// <summary>Gets the queryable set of tracked Git repositories.</summary>
    public DbSet<Repository> Repositories => Set<Repository>();

    /// <summary>Gets the queryable set of individual commit records.</summary>
    public DbSet<CommitRecord> Commits => Set<CommitRecord>();

    /// <summary>Gets the queryable set of pre-aggregated daily productivity summaries.</summary>
    public DbSet<DailySummary> DailySummaries => Set<DailySummary>();

    // ── Model configuration ───────────────────────────────────────────────────

    /// <inheritdoc/>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        ConfigureRepository(modelBuilder);
        ConfigureCommitRecord(modelBuilder);
        ConfigureDailySummary(modelBuilder);
    }

    // ── Private configuration helpers ─────────────────────────────────────────

    private static void ConfigureRepository(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Repository>(entity =>
        {
            entity.HasKey(r => r.Id);

            entity.Property(r => r.Path)
                  .IsRequired()
                  .HasMaxLength(1_000);

            entity.Property(r => r.Name)
                  .HasMaxLength(255)
                  .HasDefaultValue(string.Empty);

            // Enforce one row per file system path.
            // GetByPathAsync and the registration flow rely on this constraint.
            entity.HasIndex(r => r.Path)
                  .IsUnique()
                  .HasDatabaseName("IX_Repositories_Path");

            // Index LastScannedUtc so GetNeedsScanAsync (WHERE LastScannedUtc < threshold)
            // does a fast range scan instead of a full table scan.
            entity.HasIndex(r => r.LastScannedUtc)
                  .HasDatabaseName("IX_Repositories_LastScannedUtc");

            // Cascade-delete commits and summaries when a repository is removed.
            entity.HasMany(r => r.CommitRecords)
                  .WithOne(c => c.Repository)
                  .HasForeignKey(c => c.RepositoryId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(r => r.DailySummaries)
                  .WithOne(d => d.Repository)
                  .HasForeignKey(d => d.RepositoryId)
                  .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureCommitRecord(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CommitRecord>(entity =>
        {
            entity.HasKey(c => c.Id);

            entity.Property(c => c.Hash)
                  .IsRequired()
                  .HasMaxLength(40);  // SHA-1 is exactly 40 hex chars

            entity.Property(c => c.Author)
                  .HasMaxLength(255);

            // CommitExistsAsync uses this index for O(log n) duplicate detection
            // during ingestion. Must be unique across all repositories since SHA-1
            // hashes are globally unique within Git's object graph.
            entity.HasIndex(c => c.Hash)
                  .IsUnique()
                  .HasDatabaseName("IX_Commits_Hash");

            // GetByRepositoryAndDateRangeAsync filters on both columns;
            // a composite index covers both predicates in one pass.
            entity.HasIndex(c => new { c.RepositoryId, c.DateUtc })
                  .HasDatabaseName("IX_Commits_RepositoryId_DateUtc");

            // Standalone DateUtc index supports global date-range queries
            // that are not scoped to a single repository.
            entity.HasIndex(c => c.DateUtc)
                  .HasDatabaseName("IX_Commits_DateUtc");
        });
    }

    private static void ConfigureDailySummary(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DailySummary>(entity =>
        {
            entity.HasKey(d => d.Id);

            // Enforce exactly one summary row per (repository, calendar date).
            // UpsertAsync checks for this combination before deciding insert vs update.
            entity.HasIndex(d => new { d.RepositoryId, d.Date })
                  .IsUnique()
                  .HasDatabaseName("IX_DailySummaries_RepositoryId_Date");

            // GetByDateRangeAsync without a repoId filter scans by date only.
            entity.HasIndex(d => d.Date)
                  .HasDatabaseName("IX_DailySummaries_Date");
        });
    }
}
