using DevMetrics.Application.Commands;
using DevMetrics.Application.DTOs;
using DevMetrics.Application.Queries;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace DevMetrics.Api.Controllers;

/// <summary>
/// Manages the set of local Git repositories tracked by DevMetrics.
/// </summary>
[ApiController]
[Route("api/repositories")]
[Produces("application/json")]
public sealed class RepositoriesController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly ILogger<RepositoriesController> _logger;

    /// <inheritdoc cref="RepositoriesController"/>
    public RepositoriesController(IMediator mediator, ILogger<RepositoriesController> logger)
    {
        _mediator = mediator ?? throw new ArgumentNullException(nameof(mediator));
        _logger   = logger   ?? throw new ArgumentNullException(nameof(logger));
    }

    // ── GET /api/repositories ─────────────────────────────────────────────────

    /// <summary>Returns all repositories currently tracked by DevMetrics.</summary>
    /// <response code="200">The list of tracked repositories (may be empty).</response>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<RepositoryDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<RepositoryDto>>> GetAll(
        CancellationToken ct)
    {
        var result = await _mediator.Send(new GetAllRepositoriesQuery(), ct);
        return Ok(result);
    }

    // ── POST /api/repositories ────────────────────────────────────────────────

    /// <summary>
    /// Registers a new local Git repository for tracking.
    /// The repository must exist on disk and contain a valid <c>.git</c> directory.
    /// </summary>
    /// <param name="request">JSON body containing the absolute path to the repository.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="201">Repository registered. Location header points to the new resource.</response>
    /// <response code="400">Path is missing, doesn't exist, or is not a Git repository.</response>
    /// <response code="409">The path is already tracked.</response>
    [HttpPost]
    [ProducesResponseType(typeof(RepositoryDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RepositoryDto>> Add(
        [FromBody] AddRepositoryRequest request,
        CancellationToken               ct)
    {
        _logger.LogInformation("API | POST /repositories Path={Path}", request.Path);

        var dto = await _mediator.Send(new AddRepositoryCommand(request.Path), ct);

        // Return 201 Created with a Location header pointing to the new resource.
        // Note: there's no GET /api/repositories/{id} endpoint yet, so we point
        // to the collection endpoint as a reasonable default.
        return CreatedAtAction(nameof(GetAll), new { id = dto.Id }, dto);
    }

    // ── DELETE /api/repositories/{id} ────────────────────────────────────────

    /// <summary>
    /// Permanently removes a tracked repository and all its stored commits and
    /// daily summaries. This action cannot be undone.
    /// </summary>
    /// <param name="id">The unique identifier of the repository to remove.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="204">Repository deleted successfully.</response>
    /// <response code="404">No repository with this identifier exists.</response>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        _logger.LogInformation("API | DELETE /repositories/{Id}", id);

        var deleted = await _mediator.Send(new DeleteRepositoryCommand(id), ct);

        return deleted
            ? NoContent()
            : NotFound(new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title  = "Repository not found",
                Detail = $"No repository with Id '{id}' is currently tracked."
            });
    }
}

// ── Request body models ───────────────────────────────────────────────────────

/// <summary>Request body for <c>POST /api/repositories</c>.</summary>
/// <param name="Path">
/// The absolute file-system path to the Git repository root
/// (the directory containing the <c>.git</c> folder).
/// </param>
public sealed record AddRepositoryRequest(string Path);
