namespace DevMetrics.Application.DTOs;

/// <summary>
/// Represents a single calendar day's aggregated commit activity,
/// summed across all tracked repositories.
/// Used to populate the time-series chart on the dashboard.
/// </summary>
/// <param name="Date">
/// The calendar date this summary covers (midnight UTC, date portion only).
/// </param>
/// <param name="TotalCommits">
/// Total commits authored on <see cref="Date"/> across all repositories.
/// </param>
/// <param name="LinesAdded">
/// Total lines inserted across all commits on <see cref="Date"/>.
/// </param>
/// <param name="LinesDeleted">
/// Total lines removed across all commits on <see cref="Date"/>.
/// </param>
public sealed record CommitSummaryDto(
    DateTime Date,
    int      TotalCommits,
    int      LinesAdded,
    int      LinesDeleted
);
