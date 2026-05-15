namespace DevMetrics.Application.DTOs;

/// <summary>
/// A read-only projection of a <see cref="Core.Entities.Repository"/> entity,
/// safe to serialise and return from API endpoints.
/// </summary>
/// <param name="Id">The unique identifier of the repository.</param>
/// <param name="Path">The absolute file-system path to the repository root.</param>
/// <param name="Name">The human-readable display name of the repository.</param>
/// <param name="LastScannedUtc">
/// The UTC timestamp of the last successful scan.
/// Used by the dashboard to show staleness warnings.
/// </param>
public sealed record RepositoryDto(
    Guid     Id,
    string   Path,
    string   Name,
    DateTime LastScannedUtc
);
