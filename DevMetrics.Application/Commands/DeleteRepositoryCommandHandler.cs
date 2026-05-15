using DevMetrics.Core.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace DevMetrics.Application.Commands;

/// <summary>
/// Handles <see cref="DeleteRepositoryCommand"/> by verifying the repository
/// exists, staging the delete, and committing via <see cref="IUnitOfWork"/>.
/// </summary>
public sealed class DeleteRepositoryCommandHandler
    : IRequestHandler<DeleteRepositoryCommand, bool>
{
    private readonly IRepositoryRepository _repositoryRepo;
    private readonly IUnitOfWork           _unitOfWork;
    private readonly ILogger<DeleteRepositoryCommandHandler> _logger;

    /// <inheritdoc cref="DeleteRepositoryCommandHandler"/>
    public DeleteRepositoryCommandHandler(
        IRepositoryRepository repositoryRepo,
        IUnitOfWork           unitOfWork,
        ILogger<DeleteRepositoryCommandHandler> logger)
    {
        _repositoryRepo = repositoryRepo ?? throw new ArgumentNullException(nameof(repositoryRepo));
        _unitOfWork     = unitOfWork     ?? throw new ArgumentNullException(nameof(unitOfWork));
        _logger         = logger         ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public async Task<bool> Handle(
        DeleteRepositoryCommand command,
        CancellationToken       cancellationToken)
    {
        var existing = await _repositoryRepo.GetByIdAsync(command.Id);

        if (existing is null)
        {
            _logger.LogWarning("DeleteRepository | Id={Id} not found — returning false", command.Id);
            return false;
        }

        _logger.LogInformation(
            "DeleteRepository | Removing Id={Id} Name={Name}", existing.Id, existing.Name);

        await _repositoryRepo.DeleteAsync(command.Id);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return true;
    }
}
