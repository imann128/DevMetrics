namespace DevMetrics.Application.DTOs;

/// <summary>
/// The root payload returned by <c>GetDashboardDataQuery</c> and pushed
/// to the React dashboard via SignalR after each scan.
/// Contains everything the front-end needs to render all charts and widgets.
/// </summary>
/// <param name="Repositories">
/// All repositories currently tracked by DevMetrics, ordered by name.
/// Used to populate the repository selector and the per-repo summary cards.
/// </param>
/// <param name="DailySummaries">
/// Per-day aggregated stats for the requested window (default: last 14 days),
/// ordered by <see cref="CommitSummaryDto.Date"/> ascending.
/// Summaries are cross-repository totals; use the repo filter in
/// <c>GetDashboardDataQuery</c> for per-repo breakdowns.
/// </param>
/// <param name="LastScanTime">
/// The UTC timestamp of the most recent completed scan across all repositories,
/// or <c>null</c> if no scan has run yet.
/// Displayed as a "last updated" indicator on the dashboard.
/// </param>
public sealed record DashboardDataDto(
    List<RepositoryDto>    Repositories,
    List<CommitSummaryDto> DailySummaries,
    DateTime?              LastScanTime
);
