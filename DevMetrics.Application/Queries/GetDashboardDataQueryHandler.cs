using DevMetrics.Application.DTOs;
using DevMetrics.Application.Queries;
using DevMetrics.Core.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace DevMetrics.Application.Queries;

/// <summary>
/// Handles <see cref="GetDashboardDataQuery"/> by:
/// <list type="number">
///   <item>Loading all tracked repositories.</item>
///   <item>Querying <see cref="Core.Entities.DailySummary"/> rows for the date window.</item>
///   <item>Aggregating per-day totals across repositories into <see cref="CommitSummaryDto"/>.</item>
///   <item>Computing the most-recent scan timestamp.</item>
/// </list>
/// All reads use <c>AsNoTracking</c> (via the repository layer) — this is a
/// pure query handler with no state mutations.
/// </summary>
public sealed class GetDashboardDataQueryHandler
    : IRequestHandler<GetDashboardDataQuery, DashboardDataDto>
{
    private readonly IRepositoryRepository   _repositoryRepo;
    private readonly IDailySummaryRepository _dailySummaryRepo;
    private readonly ILogger<GetDashboardDataQueryHandler> _logger;

    /// <summary>
    /// Initialises the handler. All parameters are injected by MediatR's DI pipeline.
    /// </summary>
    public GetDashboardDataQueryHandler(
        IRepositoryRepository   repositoryRepo,
        IDailySummaryRepository dailySummaryRepo,
        ILogger<GetDashboardDataQueryHandler> logger)
    {
        _repositoryRepo   = repositoryRepo   ?? throw new ArgumentNullException(nameof(repositoryRepo));
        _dailySummaryRepo = dailySummaryRepo ?? throw new ArgumentNullException(nameof(dailySummaryRepo));
        _logger           = logger           ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public async Task<DashboardDataDto> Handle(
        GetDashboardDataQuery query,
        CancellationToken     cancellationToken)
    {
        var days = Math.Clamp(query.Days, 1, 365); // guard against nonsensical values

        var to   = DateTime.UtcNow.Date;
        var from = to.AddDays(-(days - 1)); // inclusive window of `days` calendar days

        _logger.LogDebug(
            "Dashboard | Assembling payload for {Days} days [{From:yyyy-MM-dd} → {To:yyyy-MM-dd}]",
            days, from, to);

        // ── 1. All tracked repositories ───────────────────────────────────────
        var repos = await _repositoryRepo.GetAllAsync();

        var repoDtos = repos
            .Select(r => new RepositoryDto(
                r.Id,
                r.Path,
                r.Name,
                DateTime.SpecifyKind(r.LastScannedUtc, DateTimeKind.Utc)))
            .ToList();

        // ── 2. Daily summaries for the requested window ───────────────────────
        var rawSummaries = await _dailySummaryRepo.GetByDateRangeAsync(
            from, to, query.RepositoryId);

        // ── 3. Aggregate per-day totals across all repositories ───────────────
        // When RepositoryId is null, multiple rows per date (one per repo) exist —
        // group and sum them so the dashboard chart shows one bar per day.
        // When RepositoryId is supplied, GetByDateRangeAsync already filters to
        // that repo, so the group-by is still correct (only one row per date).
        var dailySummaryDtos = rawSummaries
            .GroupBy(s => s.Date.Date)
            .OrderBy(g => g.Key)
            .Select(g => new CommitSummaryDto(
                Date:         DateTime.SpecifyKind(g.Key, DateTimeKind.Utc),
                TotalCommits: g.Sum(s => s.TotalCommits),
                LinesAdded:   g.Sum(s => s.TotalLinesAdded),
                LinesDeleted: g.Sum(s => s.TotalLinesDeleted)))
            .ToList();

        // ── 4. Last scan timestamp ────────────────────────────────────────────
        // Use the maximum LastScannedUtc across all repos.
        // If no repos are tracked yet, return null so the UI shows "Never".
        DateTime? lastScanTime = repos.Count > 0
            ? DateTime.SpecifyKind(repos.Max(r => r.LastScannedUtc), DateTimeKind.Utc)
            : null;

        // Guard: if LastScannedUtc == UnixEpoch (never actually scanned), treat as null.
        if (lastScanTime == DateTime.UnixEpoch)
            lastScanTime = null;

        _logger.LogDebug(
            "Dashboard | {Repos} repos, {Days_Count} day summaries, last scan {LastScan}",
            repoDtos.Count, dailySummaryDtos.Count, lastScanTime);

        return new DashboardDataDto(
            Repositories:  repoDtos,
            DailySummaries: dailySummaryDtos,
            LastScanTime:  lastScanTime);
    }
}
