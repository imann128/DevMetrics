using DevMetrics.Api.Models;
using DevMetrics.Application.Commands;
using DevMetrics.Application.DTOs;
using DevMetrics.Application.Queries;
using DevMetrics.Api.Hubs;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Memory;

namespace DevMetrics.Api.Controllers;

/// <summary>
/// Provides endpoints for triggering and monitoring repository scan operations.
/// </summary>
[ApiController]
[Route("api/scan")]
[Produces("application/json")]
public sealed class ScanController : ControllerBase
{
    // Cache entry lifetime — operations older than this are evicted automatically.
    private static readonly TimeSpan OperationCacheTtl = TimeSpan.FromMinutes(15);

    private readonly IMediator                _mediator;
    private readonly IMemoryCache             _cache;
    private readonly IHubContext<DashboardHub> _hub;
    private readonly ILogger<ScanController>  _logger;

    /// <inheritdoc cref="ScanController"/>
    public ScanController(
        IMediator                 mediator,
        IMemoryCache              cache,
        IHubContext<DashboardHub> hub,
        ILogger<ScanController>   logger)
    {
        _mediator = mediator ?? throw new ArgumentNullException(nameof(mediator));
        _cache    = cache    ?? throw new ArgumentNullException(nameof(cache));
        _hub      = hub      ?? throw new ArgumentNullException(nameof(hub));
        _logger   = logger   ?? throw new ArgumentNullException(nameof(logger));
    }

    // ── POST /api/scan/trigger ────────────────────────────────────────────────

    /// <summary>
    /// Manually triggers an immediate repository scan outside the hourly schedule.
    /// The scan runs asynchronously in the background. Use the returned
    /// <c>operationId</c> to poll <c>GET /api/scan/status/{operationId}</c>.
    /// </summary>
    /// <response code="202">Scan accepted and running. Body contains the <c>operationId</c>.</response>
    [HttpPost("trigger")]
    [ProducesResponseType(typeof(TriggerScanResponse), StatusCodes.Status202Accepted)]
    public IActionResult Trigger(CancellationToken ct)
    {
        var operationId = Guid.NewGuid().ToString("N");

        var operation = new ScanOperation(
            OperationId: operationId,
            StartedAt:   DateTime.UtcNow,
            Status:      "Pending");

        // Store with sliding expiration — reading the status resets the TTL.
        _cache.Set(
            CacheKey(operationId),
            operation,
            new MemoryCacheEntryOptions
            {
                SlidingExpiration = OperationCacheTtl
            });

        _logger.LogInformation(
            "API | Scan triggered manually — OperationId={OpId}", operationId);

        // Fire-and-forget the scan. CancellationToken.None is intentional —
        // we don't want the HTTP request cancellation to abort the background scan.
        _ = RunScanInBackgroundAsync(operationId, CancellationToken.None);

        var response = new TriggerScanResponse(
            OperationId: operationId,
            StatusUrl:   Url.Action(nameof(GetStatus), new { operationId })!,
            StartedAt:   operation.StartedAt);

        return Accepted(response.StatusUrl, response);
    }

    // ── GET /api/scan/status/{operationId} ────────────────────────────────────

    /// <summary>
    /// Returns the current status of a previously triggered scan operation.
    /// Operations are retained for 15 minutes after their last access.
    /// </summary>
    /// <param name="operationId">The <c>operationId</c> returned by <c>POST /api/scan/trigger</c>.</param>
    /// <response code="200">Operation status and result (when completed).</response>
    /// <response code="404">No operation with this ID exists (expired or invalid).</response>
    [HttpGet("status/{operationId}")]
    [ProducesResponseType(typeof(ScanStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails),     StatusCodes.Status404NotFound)]
    public IActionResult GetStatus(string operationId)
    {
        if (!_cache.TryGetValue(CacheKey(operationId), out ScanOperation? operation)
            || operation is null)
        {
            return NotFound(new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title  = "Operation not found",
                Detail = $"No scan operation with Id '{operationId}' exists or it has expired."
            });
        }

        return Ok(new ScanStatusResponse(
            OperationId: operation.OperationId,
            Status:      operation.Status,
            StartedAt:   operation.StartedAt,
            Result:      operation.Result,
            Error:       operation.Error));
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Runs the scan command on a background thread, updates the cache entry
    /// with the result, then pushes a <c>DashboardUpdated</c> SignalR event.
    /// </summary>
    private async Task RunScanInBackgroundAsync(string operationId, CancellationToken ct)
    {
        // Mark as Running.
        UpdateCache(operationId, op => op.WithStatus("Running"));

        try
        {
            var result = await _mediator.Send(new ScanRepositoriesCommand(), ct);

            UpdateCache(operationId, op => op.WithResult(result));

            _logger.LogInformation(
                "API | Background scan complete — OpId={OpId} Repos={Repos} New={New} Status={Status}",
                operationId, result.RepositoriesScanned, result.NewCommitsFound, result.Status);

            // Push refreshed dashboard data to all connected clients so charts
            // update without a page reload.
            await PushDashboardUpdateAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "API | Background scan failed — OpId={OpId}", operationId);

            UpdateCache(operationId, op => op.WithError(ex.Message));
        }
    }

    /// <summary>
    /// Queries the latest 14-day dashboard data and pushes it to all
    /// connected SignalR clients via the <c>DashboardUpdated</c> event.
    /// </summary>
    private async Task PushDashboardUpdateAsync(CancellationToken ct)
    {
        try
        {
            var dashboard = await _mediator.Send(new GetDashboardDataQuery(Days: 14), ct);

            // "DashboardUpdated" is the event name the React client subscribes to:
            //   connection.on("DashboardUpdated", (data) => { setDashboard(data); })
            await _hub.Clients.All.SendAsync("DashboardUpdated", dashboard, ct);

            _logger.LogDebug("API | DashboardUpdated pushed to all SignalR clients");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "API | Failed to push DashboardUpdated — clients will update on next poll");
        }
    }

    /// <summary>Applies a transformation to a cached <see cref="ScanOperation"/>.</summary>
    private void UpdateCache(string operationId, Func<ScanOperation, ScanOperation> transform)
    {
        if (_cache.TryGetValue(CacheKey(operationId), out ScanOperation? current)
            && current is not null)
        {
            _cache.Set(CacheKey(operationId), transform(current),
                new MemoryCacheEntryOptions { SlidingExpiration = OperationCacheTtl });
        }
    }

    private static string CacheKey(string operationId) => $"scan_op:{operationId}";
}

// ── Response models ───────────────────────────────────────────────────────────

/// <summary>Response body for <c>POST /api/scan/trigger</c>.</summary>
/// <param name="OperationId">Opaque identifier for polling the scan status.</param>
/// <param name="StatusUrl">Full URL for <c>GET /api/scan/status/{operationId}</c>.</param>
/// <param name="StartedAt">UTC time the scan was accepted.</param>
public sealed record TriggerScanResponse(
    string   OperationId,
    string   StatusUrl,
    DateTime StartedAt);

/// <summary>Response body for <c>GET /api/scan/status/{operationId}</c>.</summary>
/// <param name="OperationId">The operation identifier.</param>
/// <param name="Status"><c>"Pending"</c>, <c>"Running"</c>, <c>"Completed"</c>, or <c>"Failed"</c>.</param>
/// <param name="StartedAt">UTC time the scan was accepted.</param>
/// <param name="Result">Populated when <c>Status</c> is <c>"Completed"</c>.</param>
/// <param name="Error">Populated when <c>Status</c> is <c>"Failed"</c>.</param>
public sealed record ScanStatusResponse(
    string         OperationId,
    string         Status,
    DateTime       StartedAt,
    ScanResultDto? Result,
    string?        Error);
