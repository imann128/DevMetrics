using DevMetrics.Application.DTOs;
using DevMetrics.Application.Services;
using DevMetrics.Api.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace DevMetrics.Api.Services;

/// <summary>
/// Concrete <see cref="IScanNotifier"/> that broadcasts events to all
/// connected SignalR clients via <see cref="DashboardHub"/>.
/// Registered in <c>Program.cs</c> after <c>AddApplication()</c> to override
/// the default <see cref="NullScanNotifier"/>.
/// </summary>
/// <remarks>
/// Client-side event names (subscribe with <c>connection.on(...)</c>):
/// <list type="bullet">
///   <item><c>ScanCompleted</c> — <see cref="ScanResultDto"/> payload</item>
///   <item><c>RepositoryActivityDetected</c> — <c>{ repositoryPath, repositoryName }</c></item>
/// </list>
/// </remarks>
public sealed class SignalRScanNotifier : IScanNotifier
{
    private readonly IHubContext<DashboardHub> _hub;
    private readonly ILogger<SignalRScanNotifier> _logger;

    /// <inheritdoc cref="SignalRScanNotifier"/>
    public SignalRScanNotifier(
        IHubContext<DashboardHub>    hub,
        ILogger<SignalRScanNotifier> logger)
    {
        _hub    = hub    ?? throw new ArgumentNullException(nameof(hub));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public async Task NotifyScanCompletedAsync(
        ScanResultDto result, CancellationToken ct = default)
    {
        _logger.LogDebug(
            "SignalR | Broadcasting ScanCompleted — Repos={R} New={N} Status={S}",
            result.RepositoriesScanned, result.NewCommitsFound, result.Status);

        await _hub.Clients.All.SendAsync("ScanCompleted", result, ct);
    }

    /// <inheritdoc/>
    public async Task NotifyRepositoryActivityAsync(
        string repositoryPath, string repositoryName, CancellationToken ct = default)
    {
        _logger.LogDebug(
            "SignalR | Broadcasting RepositoryActivityDetected — {Name}", repositoryName);

        await _hub.Clients.All.SendAsync(
            "RepositoryActivityDetected",
            new { repositoryPath, repositoryName },
            ct);
    }
}
