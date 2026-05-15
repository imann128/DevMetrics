namespace DevMetrics.Application.DTOs;

/// <summary>
/// Summarises the outcome of one <c>ScanRepositoriesCommand</c> execution cycle.
/// Returned synchronously to the MediatR caller and also pushed to connected
/// dashboard clients via the <see cref="Services.IScanNotifier"/> abstraction.
/// </summary>
/// <param name="RepositoriesScanned">
/// The number of repositories that were evaluated during this scan cycle.
/// Includes repositories that yielded no new commits.
/// </param>
/// <param name="NewCommitsFound">
/// The total count of new <see cref="Core.Entities.CommitRecord"/> rows
/// written across all scanned repositories.
/// </param>
/// <param name="DurationMs">
/// Wall-clock duration of the entire scan cycle in milliseconds.
/// Useful for monitoring scan performance over time.
/// </param>
/// <param name="Status">
/// A human-readable outcome string: <c>"Completed"</c>, <c>"PartialFailure"</c>,
/// or <c>"Failed"</c>. <c>"PartialFailure"</c> means at least one repository
/// encountered an error but others succeeded.
/// </param>
public sealed record ScanResultDto(
    int    RepositoriesScanned,
    int    NewCommitsFound,
    long   DurationMs,
    string Status
);
