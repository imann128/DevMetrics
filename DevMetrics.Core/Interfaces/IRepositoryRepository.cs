using DevMetrics.Core.Entities;

namespace DevMetrics.Core.Interfaces;

/// <summary>
/// Defines the data access contract for <see cref="Repository"/> aggregate roots.
/// All methods that mutate state must be followed by a call to
/// <see cref="IUnitOfWork.SaveChangesAsync"/> to commit the transaction.
/// </summary>
public interface IRepositoryRepository
{
    /// <summary>
    /// Retrieves a single repository by its surrogate primary key.
    /// </summary>
    /// <param name="id">The <see cref="Guid"/> primary key of the repository.</param>
    /// <returns>
    /// The matching <see cref="Repository"/>, or <c>null</c> if no record
    /// with that identifier exists in the data store.
    /// </returns>
    Task<Repository?> GetByIdAsync(Guid id);

    /// <summary>
    /// Retrieves a repository by its unique file system path.
    /// Used when registering a new repository to detect duplicates before calling
    /// <see cref="AddAsync"/>.
    /// </summary>
    /// <param name="path">The absolute file system path to the repository root.</param>
    /// <returns>
    /// The matching <see cref="Repository"/>, or <c>null</c> if the path
    /// has not been registered.
    /// </returns>
    Task<Repository?> GetByPathAsync(string path);

    /// <summary>
    /// Retrieves all repositories currently tracked by DevMetrics.
    /// No related entities are eagerly loaded — use explicit
    /// <c>Include</c> calls in the concrete implementation when needed.
    /// </summary>
    /// <returns>
    /// A read-only list of every <see cref="Repository"/> in the data store,
    /// ordered by <see cref="Repository.Name"/> ascending.
    /// </returns>
    Task<IReadOnlyList<Repository>> GetAllAsync();

    /// <summary>
    /// Retrieves all repositories whose <see cref="Repository.LastScannedUtc"/>
    /// is earlier than <paramref name="threshold"/>.
    /// Called by the background service each tick to determine which repositories
    /// are overdue for a rescan.
    /// </summary>
    /// <param name="threshold">
    /// A UTC point in time; repositories last scanned before this moment are returned.
    /// Typically computed as <c>DateTime.UtcNow.AddHours(-1)</c>.
    /// </param>
    /// <returns>
    /// A read-only list of <see cref="Repository"/> entities that need scanning,
    /// ordered by <see cref="Repository.LastScannedUtc"/> ascending (oldest first).
    /// </returns>
    Task<IReadOnlyList<Repository>> GetNeedsScanAsync(DateTime threshold);

    /// <summary>
    /// Stages a new <see cref="Repository"/> entity for insertion.
    /// The record is not persisted until <see cref="IUnitOfWork.SaveChangesAsync"/> is called.
    /// </summary>
    /// <param name="repo">The <see cref="Repository"/> to add. Its <c>Id</c> must be set.</param>
    Task AddAsync(Repository repo);

    /// <summary>
    /// Marks an existing <see cref="Repository"/> entity as modified.
    /// The changes are not persisted until <see cref="IUnitOfWork.SaveChangesAsync"/> is called.
    /// </summary>
    /// <param name="repo">The <see cref="Repository"/> with updated property values.</param>
    Task UpdateAsync(Repository repo);

    /// <summary>
    /// Stages the repository with the given <paramref name="id"/> for deletion,
    /// along with all cascade-dependent <see cref="CommitRecord"/> and
    /// <see cref="DailySummary"/> rows (enforced at the database level).
    /// The deletion is not committed until <see cref="IUnitOfWork.SaveChangesAsync"/> is called.
    /// </summary>
    /// <param name="id">The <see cref="Guid"/> of the repository to remove.</param>
    Task DeleteAsync(Guid id);
}
