using DevMetrics.Application.DTOs;
using MediatR;

namespace DevMetrics.Application.Queries;

/// <summary>
/// MediatR query that assembles the full <see cref="DashboardDataDto"/> payload
/// used to render the React dashboard.
/// </summary>
/// <param name="Days">
/// The number of calendar days to include in the <c>DailySummaries</c> window,
/// counting backwards from today (inclusive). Default: <c>14</c>.
/// The caller can override this to retrieve a 7-day, 30-day, or any other window.
/// </param>
/// <param name="RepositoryId">
/// An optional filter to restrict <c>DailySummaries</c> to a single repository.
/// When <c>null</c>, cross-repository totals are returned.
/// </param>
public sealed record GetDashboardDataQuery(
    int   Days         = 14,
    Guid? RepositoryId = null
) : IRequest<DashboardDataDto>;
