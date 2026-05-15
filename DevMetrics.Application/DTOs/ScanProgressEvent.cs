namespace DevMetrics.Application.DTOs;

/// <summary>
/// A single progress event emitted by the background scan service.
/// Written to a <see cref="System.Threading.Channels.Channel{T}"/> singleton
/// so any consumer (SignalR hub, status endpoint) can read events in real time.
/// </summary>
/// <param name="RepositoryName">
/// The display name of the repository being processed.
/// <c>"[system]"</c> for events that span all repositories (start/end of cycle).
/// </param>
/// <param name="Status">
/// One of: <c>"CycleStarted"</c>, <c>"RepoStarted"</c>,
/// <c>"RepoCompleted"</c>, <c>"RepoFailed"</c>, <c>"RepoSkipped"</c>,
/// <c>"CycleCompleted"</c>.
/// </param>
/// <param name="NewCommitsFound">
/// Number of new commits ingested for this repository.
/// <c>null</c> for cycle-level events.
/// </param>
/// <param name="Error">
/// Error message when <see cref="Status"/> is <c>"RepoFailed"</c>.
/// </param>
/// <param name="Timestamp">UTC time this event was emitted.</param>
public sealed record ScanProgressEvent(
    string   RepositoryName,
    string   Status,
    int?     NewCommitsFound = null,
    string?  Error           = null,
    DateTime Timestamp       = default
)
{
    /// <summary>Convenience factory for cycle-level events.</summary>
    public static ScanProgressEvent CycleStarted()
        => new("[system]", "CycleStarted", Timestamp: DateTime.UtcNow);

    /// <summary>Convenience factory for cycle-level events.</summary>
    public static ScanProgressEvent CycleCompleted(int totalNew)
        => new("[system]", "CycleCompleted", NewCommitsFound: totalNew, Timestamp: DateTime.UtcNow);

    /// <summary>Convenience factory for per-repo events.</summary>
    public static ScanProgressEvent RepoStarted(string name)
        => new(name, "RepoStarted", Timestamp: DateTime.UtcNow);

    /// <summary>Convenience factory for per-repo events.</summary>
    public static ScanProgressEvent RepoCompleted(string name, int newCommits)
        => new(name, "RepoCompleted", NewCommitsFound: newCommits, Timestamp: DateTime.UtcNow);

    /// <summary>Convenience factory for per-repo events.</summary>
    public static ScanProgressEvent RepoFailed(string name, string error)
        => new(name, "RepoFailed", Error: error, Timestamp: DateTime.UtcNow);

    /// <summary>Convenience factory for skipped repos (path missing).</summary>
    public static ScanProgressEvent RepoSkipped(string name, string reason)
        => new(name, "RepoSkipped", Error: reason, Timestamp: DateTime.UtcNow);
}
