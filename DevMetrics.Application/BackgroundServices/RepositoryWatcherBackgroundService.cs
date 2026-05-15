using DevMetrics.Application.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DevMetrics.Application.BackgroundServices;

/// <summary>
/// Watches the <c>.git</c> directories of all tracked repositories for
/// file-system changes that indicate a new commit has been made.
/// When activity is detected it debounces for 30 seconds then notifies
/// connected dashboard clients via <see cref="IScanNotifier"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Detection strategy:</b> Watches for changes to
/// <c>.git/refs/heads/</c>, <c>.git/COMMIT_EDITMSG</c>, and <c>.git/HEAD</c>.
/// These files are written atomically by Git on every commit. The watcher
/// fires on <c>Created</c>, <c>Changed</c>, and <c>Renamed</c> events.
/// </para>
/// <para>
/// <b>Debounce:</b> A per-repository <see cref="Timer"/> is reset on every
/// file-system event. After 30 seconds of quiet the notification is sent.
/// This prevents a flood of events during a rebase or cherry-pick sequence.
/// </para>
/// <para>
/// <b>Why not trigger a scan here?</b> The hourly scan threshold prevents a
/// just-scanned repository from being re-scanned within an hour. Rather than
/// coupling the watcher to scan logic, it sends a lightweight SignalR
/// "RepositoryActivityDetected" event. The dashboard can show a "new activity"
/// badge, and the next scheduled scan will pick up the commits.
/// If you want immediate ingestion on commit, set the
/// <c>CronExpressions:HourlyScan</c> to a shorter interval (e.g. <c>"* * * * *"</c>)
/// and the scan service will catch it on its next tick.
/// </para>
/// <para>
/// <b>Thread safety:</b> <see cref="FileSystemWatcher"/> events fire on the
/// thread pool. The debounce timers are replaced atomically using
/// <see cref="System.Threading.Interlocked"/> techniques via
/// <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/>.
/// </para>
/// </remarks>
public sealed class RepositoryWatcherBackgroundService : BackgroundService
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory                         _scopeFactory;
    private readonly ILogger<RepositoryWatcherBackgroundService>  _logger;

    // One FileSystemWatcher per repository path.
    private readonly List<FileSystemWatcher> _watchers = new();

    // One debounce timer per repository path. The value is replaced on each event.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Timer>
        _debounceTimers = new();

    // Repo name lookup so the timer callback can log / notify without a DB query.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string>
        _repoNames = new();

    // IScanNotifier is scoped; capture it once during startup to avoid scope issues.
    // Because IScanNotifier's default methods are no-ops, using a long-lived instance is safe.
    private IScanNotifier? _notifier;

    /// <inheritdoc cref="RepositoryWatcherBackgroundService"/>
    public RepositoryWatcherBackgroundService(
        IServiceScopeFactory scopeFactory,
        ILogger<RepositoryWatcherBackgroundService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger       = logger       ?? throw new ArgumentNullException(nameof(logger));
    }

    // ── BackgroundService ─────────────────────────────────────────────────────

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("RepoWatcher | Starting file system watchers…");

        // Create a long-lived scope for the notifier and repo list.
        await using var scope = _scopeFactory.CreateAsyncScope();
        var sp       = scope.ServiceProvider;
        var repoRepo = sp.GetRequiredService<Core.Interfaces.IRepositoryRepository>();
        _notifier    = sp.GetRequiredService<IScanNotifier>();

        var repos = await repoRepo.GetAllAsync();

        foreach (var repo in repos)
        {
            TryAddWatcher(repo.Path, repo.Name);
        }

        _logger.LogInformation(
            "RepoWatcher | Watching {Count} repository/repositories.",
            _watchers.Count);

        // Hold until cancellation — all work is done in watcher event handlers.
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected on host shutdown.
        }
        finally
        {
            DisposeWatchers();
            DisposeTimers();
        }

        _logger.LogInformation("RepoWatcher | Stopped.");
    }

    // ── Watcher setup ─────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a <see cref="FileSystemWatcher"/> for the <c>.git</c> sub-directory
    /// of the given repository. Skips silently if the path does not exist.
    /// </summary>
    private void TryAddWatcher(string repoPath, string repoName)
    {
        var gitDir = Path.Combine(repoPath, ".git");

        if (!Directory.Exists(gitDir))
        {
            _logger.LogDebug(
                "RepoWatcher | Skipping {Name} — no .git directory at {Path}",
                repoName, gitDir);
            return;
        }

        try
        {
            var watcher = new FileSystemWatcher(gitDir)
            {
                IncludeSubdirectories        = true,
                NotifyFilter                 = NotifyFilters.LastWrite
                                             | NotifyFilters.FileName
                                             | NotifyFilters.DirectoryName,
                EnableRaisingEvents          = true,
                // Watch all files — filter in the handler to avoid missing edge cases.
                Filter                       = "*"
            };

            // Wire up event handlers using the path as the correlation key.
            void onEvent(object _, FileSystemEventArgs e) =>
                HandleFileSystemEvent(repoPath, repoName, e.FullPath);

            watcher.Changed += onEvent;
            watcher.Created += onEvent;
            watcher.Renamed += (_, e) => HandleFileSystemEvent(repoPath, repoName, e.FullPath);
            watcher.Error   += (_, e) => _logger.LogWarning(
                "RepoWatcher | Watcher error for {Name}: {Error}",
                repoName, e.GetException().Message);

            _watchers.Add(watcher);
            _repoNames[repoPath] = repoName;

            _logger.LogDebug(
                "RepoWatcher | Watching {Name} at {Path}", repoName, gitDir);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "RepoWatcher | Could not create watcher for {Name} at {Path}",
                repoName, repoPath);
        }
    }

    // ── Event handling ────────────────────────────────────────────────────────

    private void HandleFileSystemEvent(
        string repoPath,
        string repoName,
        string changedFile)
    {
        // Only react to files that change on a commit. Ignore transient lock files
        // (*.lock) and pack operations (objects/pack/) to reduce noise.
        var fileName = Path.GetFileName(changedFile);

        var isRelevant =
            fileName == "COMMIT_EDITMSG"            ||
            fileName == "HEAD"                      ||
            fileName == "MERGE_HEAD"                ||
            changedFile.Contains($"{Path.DirectorySeparatorChar}refs{Path.DirectorySeparatorChar}heads") ||
            changedFile.Contains($"{Path.DirectorySeparatorChar}refs{Path.DirectorySeparatorChar}tags");

        var isNoise =
            fileName?.EndsWith(".lock", StringComparison.OrdinalIgnoreCase) == true ||
            changedFile.Contains($"{Path.DirectorySeparatorChar}objects{Path.DirectorySeparatorChar}");

        if (!isRelevant || isNoise) return;

        _logger.LogDebug(
            "RepoWatcher | Activity detected in {Name} — {File}. Starting debounce.",
            repoName, fileName);

        // Replace any existing debounce timer with a fresh one.
        // The previous timer is disposed, cancelling the pending notification.
        var newTimer = new Timer(
            _ => OnDebounceElapsed(repoPath, repoName),
            state:      null,
            dueTime:    DebounceDelay,
            period:     Timeout.InfiniteTimeSpan);

        var oldTimer = _debounceTimers.AddOrUpdate(
            repoPath,
            addValue:    newTimer,
            updateValueFactory: (_, existing) =>
            {
                existing.Dispose();
                return newTimer;
            });

        // If AddOrUpdate returned the existing timer (race condition), dispose the new one.
        if (!ReferenceEquals(oldTimer, newTimer))
            newTimer.Dispose();
    }

    private void OnDebounceElapsed(string repoPath, string repoName)
    {
        _debounceTimers.TryRemove(repoPath, out var t);
        t?.Dispose();

        _logger.LogInformation(
            "RepoWatcher | Debounce elapsed — notifying for {Name}", repoName);

        // Fire-and-forget: notifier is lightweight (sends a SignalR message).
        _ = NotifyAsync(repoPath, repoName);
    }

    private async Task NotifyAsync(string repoPath, string repoName)
    {
        try
        {
            if (_notifier is null) return;

            await _notifier.NotifyRepositoryActivityAsync(repoPath, repoName);

            _logger.LogInformation(
                "RepoWatcher | RepositoryActivityDetected sent for {Name}", repoName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "RepoWatcher | Failed to send activity notification for {Name}", repoName);
        }
    }

    // ── Cleanup ───────────────────────────────────────────────────────────────

    private void DisposeWatchers()
    {
        foreach (var w in _watchers)
        {
            w.EnableRaisingEvents = false;
            w.Dispose();
        }
        _watchers.Clear();
    }

    private void DisposeTimers()
    {
        foreach (var (_, t) in _debounceTimers)
            t.Dispose();
        _debounceTimers.Clear();
    }
}
