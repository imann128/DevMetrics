using DevMetrics.Application.DTOs;
using MediatR;

namespace DevMetrics.Application.Commands;

/// <summary>
/// MediatR command that registers a new local Git repository for tracking by DevMetrics.
/// </summary>
/// <param name="Path">
/// The absolute file-system path to the repository root.
/// The handler validates that the path exists on disk and contains a <c>.git</c> directory.
/// </param>
/// <remarks>
/// After a repository is successfully added, its <c>LastScannedUtc</c> is set to
/// <see cref="DateTime.UnixEpoch"/> so that the next hourly scan cycle picks it up
/// immediately and ingests its full commit history.
/// </remarks>
public sealed record AddRepositoryCommand(string Path) : IRequest<RepositoryDto>;
