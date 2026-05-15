using DevMetrics.Application.DTOs;
using DevMetrics.Core.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace DevMetrics.Application.Queries;

/// <summary>
/// Handles <see cref="GetAllRepositoriesQuery"/> by projecting all
/// <see cref="Core.Entities.Repository"/> entities to <see cref="RepositoryDto"/>.
/// </summary>
public sealed class GetAllRepositoriesQueryHandler
    : IRequestHandler<GetAllRepositoriesQuery, IReadOnlyList<RepositoryDto>>
{
    private readonly IRepositoryRepository _repositoryRepo;
    private readonly ILogger<GetAllRepositoriesQueryHandler> _logger;

    /// <inheritdoc cref="GetAllRepositoriesQueryHandler"/>
    public GetAllRepositoriesQueryHandler(
        IRepositoryRepository repositoryRepo,
        ILogger<GetAllRepositoriesQueryHandler> logger)
    {
        _repositoryRepo = repositoryRepo ?? throw new ArgumentNullException(nameof(repositoryRepo));
        _logger         = logger         ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<RepositoryDto>> Handle(
        GetAllRepositoriesQuery request,
        CancellationToken       cancellationToken)
    {
        var repos = await _repositoryRepo.GetAllAsync();

        _logger.LogDebug("GetAllRepositories | Returning {Count} repositories", repos.Count);

        return repos
            .Select(r => new RepositoryDto(
                r.Id,
                r.Path,
                r.Name,
                DateTime.SpecifyKind(r.LastScannedUtc, DateTimeKind.Utc)))
            .ToList();
    }
}
