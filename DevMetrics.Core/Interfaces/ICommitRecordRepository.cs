using DevMetrics.Core.Entities;

namespace DevMetrics.Core.Interfaces;

/// <summary>
/// Defines the data access contract for <see cref="CommitRecord"/> entities.
/// Commit records are append-only after ingestion — no update or delete operations
/// are exposed to enforce immutability of the historical record.
/// </summary>
public interface ICommitRecordRepository
{
    /// <summary>
    /// Stages a batch of new commit records for insertion in a single round-trip.
    /// Prefer this over calling <c>AddAsync</c> in a loop for performance when
    /// ingesting dozens or hundreds of commits from a single scan.
    /// The records are not persisted until <see cref="IUnitOfWork.SaveChangesAsync"/> is called.
    /// </summary>
    /// <param name="commits">
    /// The collection of <see cref="CommitRecord"/> entities to insert.
    /// Must not be empty; callers should guard against empty collections before invoking.
    /// </param>
    Task AddRangeAsync(IEnumerable<CommitRecord> commits);

    /// <summary>
    /// Retrieves all commit records associated with <paramref name="repoId"/> whose
    /// <see cref="CommitRecord.DateUtc"/> falls within the inclusive range
    /// [<paramref name="from"/>, <paramref name="to"/>].
    /// </summary>
    /// <param name="repoId">The unique identifier of the parent repository.</param>
    /// <param name="from">The inclusive UTC start of the date range.</param>
    /// <param name="to">The inclusive UTC end of the date range.</param>
    /// <returns>
    /// A read-only list of matching <see cref="CommitRecord"/> entities ordered by
    /// <see cref="CommitRecord.DateUtc"/> ascending.
    /// Returns an empty list when no commits fall within the specified range.
    /// </returns>
    Task<IReadOnlyList<CommitRecord>> GetByRepositoryAndDateRangeAsync(
        Guid repoId,
        DateTime from,
        DateTime to);

    /// <summary>
    /// Checks whether a commit with the given SHA-1 <paramref name="hash"/> already
    /// exists in the data store.
    /// Called during ingestion to skip commits that were already captured in a
    /// previous scan, ensuring idempotent behaviour when the background service
    /// re-runs after a partial failure.
    /// </summary>
    /// <param name="hash">
    /// The 40-character hexadecimal SHA-1 hash of the commit to look up.
    /// The comparison is case-insensitive.
    /// </param>
    /// <returns>
    /// <c>true</c> if a <see cref="CommitRecord"/> with this hash already exists;
    /// <c>false</c> otherwise.
    /// </returns>
    Task<bool> CommitExistsAsync(string hash);
}
