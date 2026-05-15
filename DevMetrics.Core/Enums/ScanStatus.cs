namespace DevMetrics.Core.Enums;

/// <summary>
/// Represents the lifecycle state of a repository scan operation.
/// Used by the background service and SignalR hub to communicate
/// scan progress to connected dashboard clients in real time.
/// </summary>
public enum ScanStatus
{
    /// <summary>
    /// The scan has been registered but has not yet been picked up by the
    /// background worker. This is the initial state when a repository is
    /// first added to DevMetrics.
    /// </summary>
    NotStarted = 0,

    /// <summary>
    /// The background worker is actively reading commits from the repository
    /// and writing records to the database.
    /// Only one scan per repository should be <see cref="InProgress"/> at a time.
    /// </summary>
    InProgress = 1,

    /// <summary>
    /// The scan finished without errors. All new commits have been ingested
    /// and the repository's <c>LastScannedUtc</c> timestamp has been updated.
    /// </summary>
    Completed = 2,

    /// <summary>
    /// The scan encountered an unrecoverable error (e.g., the path no longer
    /// exists, the directory is not a valid Git repository, or a LibGit2Sharp
    /// exception was thrown). Inspect application logs for the root cause.
    /// </summary>
    Failed = 3
}
