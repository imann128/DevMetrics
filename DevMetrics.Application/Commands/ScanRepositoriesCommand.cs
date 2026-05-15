using DevMetrics.Application.DTOs;
using MediatR;

namespace DevMetrics.Application.Commands;

/// <summary>
/// MediatR command that initiates a full scan cycle across all repositories
/// that are overdue for a rescan (i.e., <c>LastScannedUtc</c> is older than
/// one hour from the time the command is dispatched).
/// </summary>
/// <remarks>
/// This command is dispatched in two contexts:
/// <list type="bullet">
///   <item><description>
///     Automatically by <see cref="BackgroundServices.ScanBackgroundService"/>
///     on its hourly <see cref="System.Threading.PeriodicTimer"/> tick.
///   </description></item>
///   <item><description>
///     On-demand via the <c>POST /api/scan</c> REST endpoint for manual triggers.
///   </description></item>
/// </list>
/// </remarks>
public sealed record ScanRepositoriesCommand : IRequest<ScanResultDto>;
