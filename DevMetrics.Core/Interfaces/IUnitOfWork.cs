namespace DevMetrics.Core.Interfaces;

/// <summary>
/// Represents a unit of work that groups one or more repository operations
/// into a single atomic transaction boundary.
/// </summary>
/// <remarks>
/// <para>
/// In the Infrastructure layer, this interface is implemented by the EF Core
/// <c>DbContext</c>, which naturally acts as a unit of work.
/// <c>SaveChangesAsync</c> flushes all changes tracked by the context
/// in a single database round-trip.
/// </para>
/// <para>
/// Usage pattern in application-layer use cases:
/// <code>
/// await _repositoryRepo.AddAsync(repo);
/// await _commitRepo.AddRangeAsync(commits);
/// await _unitOfWork.SaveChangesAsync(cancellationToken);
/// </code>
/// This guarantees that both the repository row and its initial commit records
/// are either both committed or both rolled back.
/// </para>
/// </remarks>
public interface IUnitOfWork
{
    /// <summary>
    /// Writes all pending changes tracked by the current unit of work to the
    /// underlying data store as a single atomic operation.
    /// </summary>
    /// <param name="ct">
    /// A <see cref="CancellationToken"/> to observe while awaiting the database write.
    /// When cancellation is requested before the write completes, no changes are committed.
    /// </param>
    /// <returns>
    /// The number of entity state entries that were written to the data store.
    /// A return value of <c>0</c> indicates that the change tracker had no pending mutations.
    /// </returns>
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
