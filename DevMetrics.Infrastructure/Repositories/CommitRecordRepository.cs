using DevMetrics.Core.Entities;
using DevMetrics.Core.Interfaces;
using DevMetrics.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DevMetrics.Infrastructure.Repositories;

/// <summary>
/// EF Core implementation of <see cref="ICommitRecordRepository"/>.
/// Commit records are append-only: once ingested they are never mutated.
/// <see cref="AddRangeAsync"/> stages the batch on the change tracker;
/// the caller must flush via <see cref="IUnitOfWork.SaveChangesAsync"/>.
/// </summary>
public sealed class CommitRecordRepository : ICommitRecordRepository
{
    private readonly AppDbContext _context;
    private readonly ILogger<CommitRecordRepository> _logger;

    /// <summary>
    /// Initialises a new instance of <see cref="CommitRecordRepository"/>.
    /// </summary>
    /// <param name="context">The scoped EF Core context. Must not be null.</param>
    /// <param name="logger">The structured logger. Must not be null.</param>
    public CommitRecordRepository(AppDbContext context, ILogger<CommitRecordRepository> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _logger  = logger  ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public async Task AddRangeAsync(IEnumerable<CommitRecord> commits)
    {
        ArgumentNullException.ThrowIfNull(commits, nameof(commits));

        // Materialise once so we can log the count without double-enumeration.
        var batch = commits as IList<CommitRecord> ?? commits.ToList();

        if (batch.Count == 0)
        {
            _logger.LogDebug("DB | Commits.AddRange — empty batch; skipping");
            return;
        }

        _logger.LogDebug(
            "DB | Commits.AddRange — staging {Count} records for insert",
            batch.Count);

        // EF Core's AddRangeAsync generates a single INSERT statement per row in SQLite.
        // For very large batches (>1000) consider using ExecuteUpdate or Bulk extensions.
        await _context.Commits.AddRangeAsync(batch);
        // Caller must call IUnitOfWork.SaveChangesAsync to commit.
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<CommitRecord>> GetByRepositoryAndDateRangeAsync(
        Guid repoId,
        DateTime from,
        DateTime to)
    {
        _logger.LogDebug(
            "DB | Commits.GetByRange RepoId={RepoId} From={From:u} To={To:u}",
            repoId, from, to);

        // Both predicates are covered by the composite index IX_Commits_RepositoryId_DateUtc,
        // so SQLite performs a single b-tree range scan.
        return await _context.Commits
            .AsNoTracking()
            .Where(c => c.RepositoryId == repoId
                     && c.DateUtc >= from
                     && c.DateUtc <= to)
            .OrderBy(c => c.DateUtc)
            .ToListAsync();
    }

    /// <inheritdoc/>
    public async Task<bool> CommitExistsAsync(string hash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash, nameof(hash));

        // Normalise to lowercase — LibGit2Sharp always returns lowercase SHA-1 hex,
        // but callers may provide uppercase. The IX_Commits_Hash unique index
        // is on a lowercase-stored column, so the comparison is exact.
        var normalisedHash = hash.ToLowerInvariant();

        _logger.LogDebug("DB | Commits.Exists Hash={Hash}", normalisedHash);

        // AnyAsync translates to EXISTS(SELECT 1 ...) which short-circuits
        // on the first hit and uses the IX_Commits_Hash index.
        return await _context.Commits
            .AsNoTracking()
            .AnyAsync(c => c.Hash == normalisedHash);
    }
}
