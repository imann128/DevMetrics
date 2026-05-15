using DevMetrics.Application.DTOs;

namespace DevMetrics.Api.Models;

/// <summary>
/// In-memory record of a manually-triggered scan operation, stored in
/// <see cref="Microsoft.Extensions.Caching.Memory.IMemoryCache"/> for
/// 15 minutes so <c>GET /api/scan/status/{operationId}</c> can poll progress.
/// </summary>
/// <param name="OperationId">Unique identifier returned to the caller in the 202 response.</param>
/// <param name="StartedAt">UTC time when the scan was submitted.</param>
/// <param name="Status">
/// One of: <c>"Pending"</c>, <c>"Running"</c>, <c>"Completed"</c>, <c>"Failed"</c>.
/// </param>
/// <param name="Result">Populated once the scan handler returns. <c>null</c> while pending/running.</param>
/// <param name="Error">Error message when <c>Status</c> is <c>"Failed"</c>.</param>
public sealed record ScanOperation(
    string        OperationId,
    DateTime      StartedAt,
    string        Status,
    ScanResultDto? Result = null,
    string?        Error  = null
)
{
    /// <summary>Returns a new instance with the given status.</summary>
    public ScanOperation WithStatus(string status)
        => this with { Status = status };

    /// <summary>Returns a new instance marked Completed with the scan result.</summary>
    public ScanOperation WithResult(ScanResultDto result)
        => this with { Status = "Completed", Result = result };

    /// <summary>Returns a new instance marked Failed with an error message.</summary>
    public ScanOperation WithError(string error)
        => this with { Status = "Failed", Error = error };
}
