using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace DevMetrics.Api.Hubs;

/// <summary>
/// SignalR hub that the React dashboard connects to for real-time updates.
/// </summary>
/// <remarks>
/// <para>
/// <b>Client events pushed from server:</b>
/// <list type="bullet">
///   <item>
///     <description>
///       <c>ScanCompleted</c> — pushed after every scan cycle with a
///       <c>ScanResultDto</c> payload. See <see cref="SignalRScanNotifier"/>.
///     </description>
///   </item>
///   <item>
///     <description>
///       <c>DashboardUpdated</c> — pushed with a fresh <c>DashboardDataDto</c>
///       after each scan so charts refresh without a page reload.
///     </description>
///   </item>
/// </list>
/// </para>
/// <para>
/// <b>Groups:</b> Clients can join named groups (e.g., <c>"dashboard"</c>) so
/// targeted notifications are possible when per-repository views are added later.
/// </para>
/// <para>
/// <b>Connection URL:</b> <c>ws://localhost:5000/dashboardHub</c>.
/// </para>
/// </remarks>
public sealed class DashboardHub : Hub
{
    private readonly ILogger<DashboardHub> _logger;

    /// <summary>Initialises the hub with a structured logger.</summary>
    public DashboardHub(ILogger<DashboardHub> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // ── Client-callable hub methods ───────────────────────────────────────────

    /// <summary>
    /// Adds the calling connection to a named SignalR group.
    /// The React client calls this immediately after connecting:
    /// <code>connection.invoke("JoinDashboardGroup", "dashboard")</code>
    /// </summary>
    /// <param name="groupName">
    /// The group to join. Use <c>"dashboard"</c> for the global dashboard view
    /// or a repository-specific name like <c>"repo-{id}"</c> for targeted updates.
    /// </param>
    public async Task JoinDashboardGroup(string groupName)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, groupName);

        _logger.LogInformation(
            "Hub | Connection {ConnId} joined group '{Group}'",
            Context.ConnectionId, groupName);
    }

    /// <summary>
    /// Removes the calling connection from a named SignalR group.
    /// </summary>
    /// <param name="groupName">The group to leave.</param>
    public async Task LeaveDashboardGroup(string groupName)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupName);

        _logger.LogInformation(
            "Hub | Connection {ConnId} left group '{Group}'",
            Context.ConnectionId, groupName);
    }

    // ── Lifecycle overrides ───────────────────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>
    /// Logs the new connection. The client is responsible for calling
    /// <see cref="JoinDashboardGroup"/> after connection to opt into updates.
    /// </remarks>
    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation(
            "Hub | Client connected — ConnectionId={ConnId} UserAgent={UA}",
            Context.ConnectionId,
            Context.GetHttpContext()?.Request.Headers.UserAgent.ToString() ?? "unknown");

        await base.OnConnectedAsync();
    }

    /// <inheritdoc/>
    /// <remarks>
    /// SignalR automatically removes the connection from all groups on disconnect,
    /// so no explicit <see cref="LeaveDashboardGroup"/> call is required from the client.
    /// </remarks>
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (exception is null)
        {
            _logger.LogInformation(
                "Hub | Client disconnected cleanly — ConnectionId={ConnId}",
                Context.ConnectionId);
        }
        else
        {
            _logger.LogWarning(exception,
                "Hub | Client disconnected with error — ConnectionId={ConnId}",
                Context.ConnectionId);
        }

        await base.OnDisconnectedAsync(exception);
    }
}
