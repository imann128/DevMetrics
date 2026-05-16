using DevMetrics.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DevMetrics.Api.HealthChecks;

/// <summary>
/// Health check that verifies the SQLite database is reachable and the schema
/// is up to date by calling <c>DatabaseFacade.CanConnectAsync</c>.
/// </summary>
/// <remarks>
/// Registered as <c>"database"</c> in <c>Program.cs</c> with the
/// <c>ready</c> tag, making it part of the readiness probe (not liveness).
/// A failing database check returns <see cref="HealthStatus.Unhealthy"/>;
/// the container orchestrator should route traffic away until the DB recovers.
/// </remarks>
public sealed class DatabaseHealthCheck : IHealthCheck
{
    private readonly AppDbContext _db;
    private readonly ILogger<DatabaseHealthCheck> _logger;

    /// <inheritdoc cref="DatabaseHealthCheck"/>
    public DatabaseHealthCheck(AppDbContext db, ILogger<DatabaseHealthCheck> logger)
    {
        _db     = db     ?? throw new ArgumentNullException(nameof(db));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken  cancellationToken = default)
    {
        try
        {
            var canConnect = await _db.Database.CanConnectAsync(cancellationToken);

            if (!canConnect)
            {
                _logger.LogWarning("Health | Database ping returned false");
                return HealthCheckResult.Unhealthy(
                    "Cannot connect to the SQLite database. " +
                    "Verify the Data Source path is writable.");
            }

            // Check for unapplied migrations — degraded but still serving traffic.
            var pending = (await _db.Database.GetPendingMigrationsAsync(cancellationToken))
                              .ToList();

            if (pending.Count > 0)
            {
                _logger.LogWarning(
                    "Health | {Count} pending migration(s): {Names}",
                    pending.Count, string.Join(", ", pending));

                return HealthCheckResult.Degraded(
                    $"{pending.Count} pending migration(s) detected. " +
                    $"Run 'dotnet ef database update' or restart the application.");
            }

            return HealthCheckResult.Healthy("SQLite database is connected and schema is current.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Health | Database health check threw an exception");

            return HealthCheckResult.Unhealthy(
                "Database health check failed with an exception.",
                ex);
        }
    }
}
