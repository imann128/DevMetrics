using DevMetrics.Application.DTOs;
using DevMetrics.Application.Queries;
using DevMetrics.Infrastructure.Data;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DevMetrics.Api.Controllers;

/// <summary>
/// Provides aggregated productivity data for the React dashboard.
/// </summary>
[ApiController]
[Route("api/dashboard")]
[Produces("application/json")]
public sealed class DashboardController : ControllerBase
{
    private readonly IMediator    _mediator;
    private readonly AppDbContext _db;
    private readonly ILogger<DashboardController> _logger;

    /// <inheritdoc cref="DashboardController"/>
    public DashboardController(
        IMediator    mediator,
        AppDbContext db,
        ILogger<DashboardController> logger)
    {
        _mediator = mediator ?? throw new ArgumentNullException(nameof(mediator));
        _db       = db       ?? throw new ArgumentNullException(nameof(db));
        _logger   = logger   ?? throw new ArgumentNullException(nameof(logger));
    }

    // ── GET /api/dashboard/summary ────────────────────────────────────────────

    /// <summary>
    /// Returns aggregated commit data for the dashboard charts.
    /// </summary>
    /// <param name="days">
    /// Number of calendar days to include, counting backwards from today.
    /// Must be between 1 and 365. Default: 14.
    /// </param>
    /// <param name="repositoryId">
    /// Optional filter to restrict results to a single repository.
    /// When omitted, data is aggregated across all repositories.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Dashboard payload with repository list and daily summaries.</response>
    /// <response code="400">Invalid <paramref name="days"/> value.</response>
    [HttpGet("summary")]
    [ProducesResponseType(typeof(DashboardDataDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails),   StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<DashboardDataDto>> GetSummary(
        [FromQuery] int   days         = 14,
        [FromQuery] Guid? repositoryId = null,
        CancellationToken ct           = default)
    {
        if (days is < 1 or > 365)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title  = "Invalid days parameter",
                Detail = $"'days' must be between 1 and 365. Received: {days}."
            });
        }

        _logger.LogDebug(
            "API | GET /dashboard/summary days={Days} repoId={RepoId}",
            days, repositoryId);

        var data = await _mediator.Send(
            new GetDashboardDataQuery(days, repositoryId), ct);

        return Ok(data);
    }

    // ── GET /api/dashboard/health ─────────────────────────────────────────────

    /// <summary>
    /// Returns the API health status including database connectivity and last scan time.
    /// Suitable for use as a liveness / readiness probe.
    /// </summary>
    /// <response code="200">API is healthy.</response>
    /// <response code="503">Database is unreachable.</response>
    [HttpGet("health")]
    [ProducesResponseType(typeof(HealthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(HealthResponse), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<HealthResponse>> Health(CancellationToken ct)
    {
        bool dbConnected;

        try
        {
            // CanConnectAsync is a lightweight ping — does not open a full connection.
            dbConnected = await _db.Database.CanConnectAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "API | Health check — DB connectivity test failed");
            dbConnected = false;
        }

        DateTime? lastScan = null;

        if (dbConnected)
        {
            try
            {
                // Pull the most recent LastScannedUtc across all repos in one SQL MIN/MAX call.
                var maxScanned = await _db.Repositories
                    .AsNoTracking()
                    .MaxAsync(r => (DateTime?)r.LastScannedUtc, ct);

                // Exclude UnixEpoch (means "never scanned") from the result.
                if (maxScanned.HasValue && maxScanned.Value > DateTime.UnixEpoch)
                    lastScan = maxScanned;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "API | Health check — LastScanTime query failed");
            }
        }

        var response = new HealthResponse(
            Status:      dbConnected ? "ok" : "degraded",
            DbConnected: dbConnected,
            LastScanTime: lastScan);

        return dbConnected
            ? Ok(response)
            : StatusCode(StatusCodes.Status503ServiceUnavailable, response);
    }
}

// ── Response models ───────────────────────────────────────────────────────────

/// <summary>Response body for <c>GET /api/dashboard/health</c>.</summary>
/// <param name="Status"><c>"ok"</c> or <c>"degraded"</c>.</param>
/// <param name="DbConnected">Whether the SQLite database is reachable.</param>
/// <param name="LastScanTime">UTC timestamp of the most recent scan, or <c>null</c> if never scanned.</param>
public sealed record HealthResponse(
    string    Status,
    bool      DbConnected,
    DateTime? LastScanTime);
