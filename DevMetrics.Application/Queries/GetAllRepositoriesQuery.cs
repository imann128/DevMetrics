using DevMetrics.Application.DTOs;
using MediatR;

namespace DevMetrics.Application.Queries;

/// <summary>
/// MediatR query that returns all tracked repositories ordered by name.
/// Used by <c>GET /api/repositories</c>.
/// </summary>
public sealed record GetAllRepositoriesQuery : IRequest<IReadOnlyList<RepositoryDto>>;
