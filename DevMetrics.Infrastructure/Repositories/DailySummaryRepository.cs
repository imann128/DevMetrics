using DevMetrics.Core.Entities;
using DevMetrics.Core.Interfaces;
using DevMetrics.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DevMetrics.Infrastructure.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IDailySummaryRepository"/>.
/// Unlike the other repositories, <see cref="UpsertAsync"/> commits changes
/// internally — it does not rely on the caller to invoke
/// <see cref="IUnitOfWork.SaveChangesAsync"/> — because the upsert is a
/// self-contained atomic operation documented at the interface level.
/// Read methods use <c>AsNoTracking</c> for performance.
/// </summary>
public sealed class DailySummaryRepository : IDailySummaryRepository
{
    private readonly AppDbContext _context;
    private readonly ILogger<DailySummaryRepository> _logger;

    /// <summary>
    /// Initialises a new instance of <see cref="DailySummaryRepository"/>.
    /// </summary>
    /// <param name="context">The scoped EF Core context. Must not be null.</param>
    /// <param name="logger">The structured logger. Must not be null.</param>
    public DailySummaryRepository(AppDbContext context, ILogger<DailySummaryRepository> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _logger  = logger  ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public async Task<DailySummary?> GetByRepositoryAndDateAsync(Guid repoId, DateTime date)
    {
        // Normalise to midnight so the WHERE clause matches regardless of
        // what time component the caller supplied.
        var dateOnly = date.Date;

        _logger.LogDebug(
            "DB | DailySummaries.GetByRepoAndDate RepoId={RepoId} Date={Date:yyyy-MM-dd}",
            repoId, dateOnly);

        return await _context.DailySummaries
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.RepositoryId == repoId && d.Date == dateOnly);
    }

    /// <inheritdoc/>
    public async Task UpsertAsync(DailySummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary, nameof(summary));

        // Always normalise the date before persisting — prevents duplicate rows
        // when the same day is recalculated at different clock times.
        summary.Date = summary.Date.Date;

        // Fetch the existing row (tracked, so EF detects changes automatically).
        // We query without AsNoTracking here because we need the change tracker
        // to detect the mutation on `existing` and generate an UPDATE statement.
        var existing = await _context.DailySummaries
            .FirstOrDefaultAsync(d => d.RepositoryId == summary.RepositoryId
                                   && d.Date == summary.Date);

        if (existing is null)
        {
            _logger.LogInformation(
                "DB | DailySummaries.Upsert — INSERT RepoId={RepoId} Date={Date:yyyy-MM-dd} " +
                "Commits={Commits} Added={Added} Deleted={Deleted}",
                summary.RepositoryId, summary.Date,
                summary.TotalCommits, summary.TotalLinesAdded, summary.TotalLinesDeleted);

            await _context.DailySummaries.AddAsync(summary);
        }
        else
        {
            _logger.LogInformation(
                "DB | DailySummaries.Upsert — UPDATE RepoId={RepoId} Date={Date:yyyy-MM-dd} " +
                "Commits={Commits} Added={Added} Deleted={Deleted}",
                summary.RepositoryId, summary.Date,
                summary.TotalCommits, summary.TotalLinesAdded, summary.TotalLinesDeleted);

            // Mutate the tracked entity — EF will generate a targeted UPDATE statement
            // that only touches the three aggregate columns, not the entire row.
            existing.TotalCommits      = summary.TotalCommits;
            existing.TotalLinesAdded   = summary.TotalLinesAdded;
            existing.TotalLinesDeleted = summary.TotalLinesDeleted;
        }

        // UpsertAsync commits immediately per its interface contract.
        // Wrapping in SaveChangesAsync means the insert-or-update is atomic.
        await _context.SaveChangesAsync(CancellationToken.None);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<DailySummary>> GetByDateRangeAsync(
        DateTime from,
        DateTime to,
        Guid? repoId = null)
    {
        _logger.LogDebug(
            "DB | DailySummaries.GetByDateRange From={From:yyyy-MM-dd} To={To:yyyy-MM-dd} RepoId={RepoId}",
            from.Date, to.Date, repoId.HasValue ? repoId.Value.ToString() : "all");

        // Start with a base predicate over the date range.
        // Both from and to are normalised to midnight to match stored values.
        IQueryable<DailySummary> query = _context.DailySummaries
            .AsNoTracking()
            .Where(d => d.Date >= from.Date && d.Date <= to.Date);

        // Conditionally narrow to a single repository when requested.
        // The conditional filter is added before ToListAsync so EF Core
        // generates a single SQL query with the WHERE clause, not two queries.
        if (repoId.HasValue)
        {
            query = query.Where(d => d.RepositoryId == repoId.Value);
        }

        return await query
            .OrderBy(d => d.Date)
            .ThenBy(d => d.RepositoryId)
            .ToListAsync();
    }
}
