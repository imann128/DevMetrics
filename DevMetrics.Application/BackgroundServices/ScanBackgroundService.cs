using System.Threading.Channels;
using DevMetrics.Application.Commands;
using DevMetrics.Application.DTOs;
using DevMetrics.Application.Settings;
using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DevMetrics.Application.BackgroundServices;

/// <summary>
/// Long-running hosted service that dispatches <see cref="ScanRepositoriesCommand"/>
/// on a cron-configured schedule (default: every hour at minute 0).
/// </summary>
/// <remarks>
/// <b>Retry:</b> Each tick is retried up to 3 times with exponential backoff
/// (1 s, 5 s, 30 s). Per-repository failures inside the handler are tracked
/// as "PartialFailure" — they do not trigger retries at the service level.
/// <br/>
/// <b>Graceful shutdown:</b> <see cref="StopAsync"/> blocks until any in-progress
/// scan finishes or a 60-second timeout elapses.
/// <br/>
/// <b>Progress:</b> A shared <c>Channel&lt;ScanProgressEvent&gt;</c> receives
/// cycle-start/end events. Per-repo events are emitted directly from the handler.
/// <br/>
/// <b>Cache:</b> The most recent result is stored under
/// <see cref="LastHourlyScanCacheKey"/> for 2 hours.
/// </remarks>
public sealed class ScanBackgroundService : BackgroundService
{
    /// <summary>IMemoryCache key for the most recent scheduled scan result.</summary>
    public const string LastHourlyScanCacheKey = "scan:last_hourly_result";

    private static readonly TimeSpan[] RetryDelays =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30)];

    private readonly IServiceScopeFactory           _scopeFactory;
    private readonly IMemoryCache                   _cache;
    private readonly Channel<ScanProgressEvent>     _progressChannel;
    private readonly IOptions<CronSettings>         _cronOptions;
    private readonly IHostApplicationLifetime       _lifetime;
    private readonly ILogger<ScanBackgroundService> _logger;

    // One at a time — prevents overlapping scans and lets StopAsync wait cleanly.
    private readonly SemaphoreSlim _scanLock = new(initialCount: 1, maxCount: 1);

    /// <inheritdoc cref="ScanBackgroundService"/>
    public ScanBackgroundService(
        IServiceScopeFactory           scopeFactory,
        IMemoryCache                   cache,
        Channel<ScanProgressEvent>     progressChannel,
        IOptions<CronSettings>         cronOptions,
        IHostApplicationLifetime       lifetime,
        ILogger<ScanBackgroundService> logger)
    {
        _scopeFactory    = scopeFactory    ?? throw new ArgumentNullException(nameof(scopeFactory));
        _cache           = cache           ?? throw new ArgumentNullException(nameof(cache));
        _progressChannel = progressChannel ?? throw new ArgumentNullException(nameof(progressChannel));
        _cronOptions     = cronOptions     ?? throw new ArgumentNullException(nameof(cronOptions));
        _lifetime        = lifetime        ?? throw new ArgumentNullException(nameof(lifetime));
        _logger          = logger          ?? throw new ArgumentNullException(nameof(logger));
    }

    // ── BackgroundService lifecycle ───────────────────────────────────────────

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "ScanService | Started — cron: '{Cron}'", _cronOptions.Value.HourlyScan);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var delay = CalculateDelayToNextTick();

                _logger.LogDebug(
                    "ScanService | Next tick in {Delay:hh\\:mm\\:ss} at {At:u}",
                    delay, DateTime.UtcNow.Add(delay));

                await Task.Delay(delay, stoppingToken);

                if (stoppingToken.IsCancellationRequested) break;

                await RunScanWithRetryAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected on host shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex,
                "ScanService | Fatal error — signalling host stop");
            _lifetime.StopApplication();
        }

        _logger.LogInformation("ScanService | Stopped.");
    }

    /// <summary>
    /// Blocks until any in-progress scan finishes (max 60 s) before the host terminates.
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("ScanService | Stop requested — waiting for active scan to finish…");

        var acquired = await _scanLock.WaitAsync(
            TimeSpan.FromSeconds(60), cancellationToken);

        if (acquired)
        {
            _scanLock.Release();
            _logger.LogInformation("ScanService | Clean shutdown confirmed.");
        }
        else
        {
            _logger.LogWarning(
                "ScanService | Timed out waiting for scan lock — forcing shutdown.");
        }

        await base.StopAsync(cancellationToken);
    }

    // ── Retry wrapper ─────────────────────────────────────────────────────────

    private async Task RunScanWithRetryAsync(CancellationToken ct)
    {
        for (var attempt = 0; attempt <= RetryDelays.Length; attempt++)
        {
            try
            {
                await RunScanTickAsync(ct);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt < RetryDelays.Length)
            {
                var delay = RetryDelays[attempt];
                _logger.LogWarning(ex,
                    "ScanService | Attempt {A}/{Max} failed — retrying in {D:g}",
                    attempt + 1, RetryDelays.Length + 1, delay);
                await Task.Delay(delay, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "ScanService | All {N} attempts exhausted — skipping this tick",
                    RetryDelays.Length + 1);
            }
        }
    }

    // ── Core tick ─────────────────────────────────────────────────────────────

    private async Task RunScanTickAsync(CancellationToken ct)
    {
        // Guard against overlapping ticks (can happen if a retry delay spans a cron window).
        if (!await _scanLock.WaitAsync(TimeSpan.Zero, ct))
        {
            _logger.LogWarning(
                "ScanService | Previous scan still running — skipping this tick");
            return;
        }

        try
        {
            await _progressChannel.Writer.WriteAsync(
                ScanProgressEvent.CycleStarted(), ct);

            await using var scope = _scopeFactory.CreateAsyncScope();
            var sp                = scope.ServiceProvider;
            var mediator          = sp.GetRequiredService<IMediator>();

            // Health-check: warn on missing paths before dispatching.
            await WarnMissingPathsAsync(sp, ct);

            var result = await mediator.Send(new ScanRepositoriesCommand(), ct);

            // Cache for /api/scan/status/latest.
            _cache.Set(
                LastHourlyScanCacheKey,
                result,
                new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(2)
                });

            await _progressChannel.Writer.WriteAsync(
                ScanProgressEvent.CycleCompleted(result.NewCommitsFound), ct);

            _logger.LogInformation(
                "ScanService | Completed — {Repos} repos, {New} new commits, {Ms}ms [{Status}]",
                result.RepositoriesScanned, result.NewCommitsFound,
                result.DurationMs, result.Status);
        }
        finally
        {
            _scanLock.Release();
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Logs a warning for each tracked repository whose path no longer exists.
    /// Does not block the scan — the handler handles missing paths gracefully.
    /// </summary>
    private static async Task WarnMissingPathsAsync(
        IServiceProvider sp, CancellationToken ct)
    {
        var repoRepo = sp.GetRequiredService<Core.Interfaces.IRepositoryRepository>();
        var logger   = sp.GetRequiredService<ILogger<ScanBackgroundService>>();
        var repos    = await repoRepo.GetAllAsync();

        foreach (var repo in repos.Where(r => !Directory.Exists(r.Path)))
        {
            logger.LogWarning(
                "ScanService | Path missing — {Name} at {Path}. " +
                "Restore the path or remove the repository from tracking.",
                repo.Name, repo.Path);
        }
    }

    /// <summary>
    /// Parses the configured cron expression and returns the time until
    /// the next scheduled tick. Falls back to 1 hour on parse failure.
    /// </summary>
    private TimeSpan CalculateDelayToNextTick()
    {
        try
        {
            var expr = Cronos.CronExpression.Parse(
                _cronOptions.Value.HourlyScan,
                Cronos.CronFormat.Standard);

            // GetNextOccurrence is DST-aware when TimeZoneInfo is provided.
            var next = expr.GetNextOccurrence(DateTime.UtcNow, TimeZoneInfo.Utc);

            if (next.HasValue)
            {
                var delay = next.Value - DateTime.UtcNow;
                return delay > TimeSpan.Zero ? delay : TimeSpan.FromMinutes(1);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "ScanService | Invalid cron '{Expr}' — defaulting to 1-hour interval",
                _cronOptions.Value.HourlyScan);
        }

        return TimeSpan.FromHours(1);
    }
}
