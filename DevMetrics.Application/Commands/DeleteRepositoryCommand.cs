using MediatR;

namespace DevMetrics.Application.Commands;

/// <summary>
/// MediatR command that permanently removes a tracked repository and all its
/// associated <see cref="Core.Entities.CommitRecord"/> and
/// <see cref="Core.Entities.DailySummary"/> rows via cascade delete.
/// </summary>
/// <param name="Id">The <see cref="Guid"/> of the repository to delete.</param>
/// <remarks>
/// Returns <c>true</c> when the repository was found and deleted,
/// <c>false</c> when no repository with that <paramref name="Id"/> exists
/// (idempotent — callers should map <c>false</c> to HTTP 404).
/// </remarks>
public sealed record DeleteRepositoryCommand(Guid Id) : IRequest<bool>;
