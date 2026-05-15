using DevMetrics.Application.DTOs;

namespace DevMetrics.Application.Services;

/// <summary>
/// A no-op implementation of <see cref="IScanNotifier"/> used when no real
/// notification sink (e.g., SignalR hub) has been registered by the host.
/// </summary>
/// <remarks>
/// Registered as the default binding by
/// <see cref="Extensions.ApplicationServiceExtensions.AddApplication"/>.
/// The <c>DevMetrics.Api</c> layer replaces this with <c>SignalRScanNotifier</c>
/// after calling <c>AddApplication()</c>, using DI's last-registration-wins rule.
/// See <see cref="IScanNotifier"/> for the complete integration pattern.
/// </remarks>
public sealed class NullScanNotifier : IScanNotifier
{
    /// <inheritdoc/>
    /// <remarks>Always returns <see cref="Task.CompletedTask"/> immediately.</remarks>
    public Task NotifyScanCompletedAsync(ScanResultDto result, CancellationToken ct = default)
        => Task.CompletedTask;
}
