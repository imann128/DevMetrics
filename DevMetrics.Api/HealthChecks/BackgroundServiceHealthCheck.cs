using DevMetrics.Application.BackgroundServices;
using DevMetrics.Application.DTOs;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DevMetrics.Api.HealthChecks;

/// <summary>
/// Health check that reports whether the hourly scan background service is
/// running normally by inspecting the cached result of its most recent tick.
/// </summary>
/// <remarks>
/// <para>
/// The check is <em>degraded</em> (not unhealthy) when a scan has not completed
/// within the last two hours — the application is still serving dashboard traffic,
/// but telemetry may be stale. An operator should inspect the application logs.
/// </para>
/// <para>
/// The check returns <c>Healthy</c> with a "not yet run" description when the
/// cache entry is absent because the service only just started. This avoids
/// false-positive degraded alerts immediately after a cold start.
/// </para>
/// </remarks>
public sealed class BackgroundServiceHealthCheck : IHealthCheck
{
    /// <summary>
    /// Maximum age of the last scan result before the service is considered stale.
    /// Set to 2 hours (2× the default hourly interval) to allow one missed tick
    /// before alarming.
    /// </summary>
    private static readonly TimeSpan StalenessThreshold = TimeSpan.FromHours(2);

    private readonly IMemoryCache _cache;
    private readonly ILogger<BackgroundServiceHealthCheck> _logger;

    /// <inheritdoc cref="BackgroundServiceHealthCheck"/>
    public BackgroundServiceHealthCheck(
        IMemoryCache cache,
        ILogger<BackgroundServiceHealthCheck> logger)
    {
        _cache  = cache  ?? throw new ArgumentNullException(nameof(cache));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken  cancellationToken = default)
    {
        // Try to read the most recent scheduled scan result stored by ScanBackgroundService.
        if (!_cache.TryGetValue(ScanBackgroundService.LastHourlyScanCacheKey,
                out ScanResultDto? lastResult) || lastResult is null)
        {
            // Cache miss: either the service hasn't run yet (cold start) or
            // the entry expired (> 2 hours since the last scan result was written).
            _logger.LogDebug(
                "Health | BackgroundService cache miss — " +
                "service may not have run yet or last result has expired");

            return Task.FromResult(HealthCheckResult.Healthy(
                "Scan service has not completed a cycle yet since startup. " +
                "This is expected immediately after a cold start."));
        }

        var data = new Dictionary<string, object>
        {
            ["repositoriesScanned"] = lastResult.RepositoriesScanned,
            ["newCommitsFound"]     = lastResult.NewCommitsFound,
            ["durationMs"]          = lastResult.DurationMs,
            ["status"]              = lastResult.Status
        };

        // The cache TTL is 2 hours (set in ScanBackgroundService). If we can read
        // the entry, the scan completed within the staleness window.
        if (lastResult.Status == "Failed")
        {
            _logger.LogWarning(
                "Health | Last scan cycle status was 'Failed' — check application logs");

            return Task.FromResult(HealthCheckResult.Degraded(
                "The most recent scan cycle reported a 'Failed' status. " +
                "All repositories failed to scan. Check application logs for details.",
                data: data));
        }

        if (lastResult.Status == "PartialFailure")
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                $"The most recent scan cycle completed with partial failures " +
                $"({lastResult.RepositoriesScanned} repos scanned, " +
                $"{lastResult.NewCommitsFound} new commits). " +
                "Some repositories could not be read — check application logs.",
                data: data));
        }

        return Task.FromResult(HealthCheckResult.Healthy(
            $"Scan service is running normally. Last cycle: " +
            $"{lastResult.RepositoriesScanned} repos, " +
            $"{lastResult.NewCommitsFound} new commits, " +
            $"{lastResult.DurationMs}ms.",
            data));
    }
}
