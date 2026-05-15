using DevMetrics.Core.Entities;

namespace DevMetrics.Core.Interfaces;

/// <summary>
/// Defines the data access contract for <see cref="DailySummary"/> entities.
/// Summaries are materialised views — they are never written by the user directly,
/// only by the background service after each scan via <see cref="UpsertAsync"/>.
/// </summary>
public interface IDailySummaryRepository
{
    /// <summary>
    /// Retrieves the daily summary for a specific repository on a specific calendar date.
    /// </summary>
    /// <param name="repoId">The unique identifier of the parent repository.</param>
    /// <param name="date">
    /// The calendar date of interest.
    /// Only the <em>date portion</em> is used; the time component is ignored.
    /// </param>
    /// <returns>
    /// The matching <see cref="DailySummary"/> if one exists for that repository and date;
    /// <c>null</c> if no commits were recorded on that day.
    /// </returns>
    Task<DailySummary?> GetByRepositoryAndDateAsync(Guid repoId, DateTime date);

    /// <summary>
    /// Inserts or replaces a daily summary using upsert semantics.
    /// <list type="bullet">
    ///   <item>
    ///     <description>
    ///       If no summary exists for the given <see cref="DailySummary.RepositoryId"/>
    ///       and <see cref="DailySummary.Date"/> combination, a new row is inserted.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       If a summary already exists for that day, its aggregate counters
    ///       (<see cref="DailySummary.TotalCommits"/>,
    ///       <see cref="DailySummary.TotalLinesAdded"/>,
    ///       <see cref="DailySummary.TotalLinesDeleted"/>)
    ///       are overwritten with the new values.
    ///     </description>
    ///   </item>
    /// </list>
    /// This method internally calls <see cref="IUnitOfWork.SaveChangesAsync"/> —
    /// callers do <em>not</em> need a separate save call.
    /// </summary>
    /// <param name="summary">The <see cref="DailySummary"/> to insert or update.</param>
    Task UpsertAsync(DailySummary summary);

    /// <summary>
    /// Retrieves daily summaries within a UTC date range, with an optional
    /// filter to a single repository.
    /// </summary>
    /// <param name="from">The inclusive start of the date range.</param>
    /// <param name="to">The inclusive end of the date range.</param>
    /// <param name="repoId">
    /// When provided, only summaries for that repository are returned.
    /// When <c>null</c>, summaries across <em>all</em> tracked repositories
    /// are returned — useful for the global dashboard view.
    /// </param>
    /// <returns>
    /// A read-only list of <see cref="DailySummary"/> entities ordered by
    /// <see cref="DailySummary.Date"/> ascending, then by
    /// <see cref="DailySummary.RepositoryId"/>.
    /// Returns an empty list when the date range contains no data.
    /// </returns>
    Task<IReadOnlyList<DailySummary>> GetByDateRangeAsync(
        DateTime from,
        DateTime to,
        Guid? repoId = null);
}
