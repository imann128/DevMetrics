using DevMetrics.Application.DTOs;

namespace DevMetrics.Application.Services;

/// <summary>
/// Abstraction for notifying connected clients of scan events.
/// Lives in the Application layer so background services and handlers
/// can push real-time updates without depending on ASP.NET Core SignalR.
/// </summary>
/// <remarks>
/// Default interface methods are used for new members so existing
/// implementations (<see cref="NullScanNotifier"/>, <c>SignalRScanNotifier</c>)
/// do not need to be modified unless they want to provide real behaviour.
/// </remarks>
public interface IScanNotifier
{
    /// <summary>
    /// Notifies all connected clients that a full scan cycle has completed.
    /// </summary>
    /// <param name="result">The outcome summary of the completed scan cycle.</param>
    /// <param name="ct">Cancellation token.</param>
    Task NotifyScanCompletedAsync(ScanResultDto result, CancellationToken ct = default);

    /// <summary>
    /// Notifies all connected clients that Git activity was detected in a repository
    /// by the <see cref="BackgroundServices.RepositoryWatcherBackgroundService"/>.
    /// The client uses this to show a "new activity" badge without waiting for
    /// the next hourly scan to complete.
    /// </summary>
    /// <param name="repositoryPath">Absolute file system path to the repository root.</param>
    /// <param name="repositoryName">Human-readable display name of the repository.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// Default implementation is a no-op — override in <c>SignalRScanNotifier</c>:
    /// <code>
    /// public Task NotifyRepositoryActivityAsync(
    ///     string path, string name, CancellationToken ct)
    ///     => _hub.Clients.All.SendAsync(
    ///         "RepositoryActivityDetected",
    ///         new { repositoryPath = path, repositoryName = name }, ct);
    /// </code>
    /// </remarks>
    Task NotifyRepositoryActivityAsync(
        string repositoryPath,
        string repositoryName,
        CancellationToken ct = default)
        => Task.CompletedTask;   // default: no-op
}
