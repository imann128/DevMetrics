using DevMetrics.Application.DTOs;
using DevMetrics.Core.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace DevMetrics.Application.Commands;

/// <summary>
/// Handles <see cref="AddRepositoryCommand"/> by validating the supplied path,
/// reading Git metadata, checking for duplicates, and persisting the new
/// <see cref="Core.Entities.Repository"/> entity.
/// </summary>
/// <remarks>
/// Validation layers (in order):
/// <list type="number">
///   <item>Path must not be null or whitespace.</item>
///   <item>Directory must exist on disk.</item>
///   <item>Directory must contain a <c>.git</c> sub-directory.</item>
///   <item>LibGit2Sharp must be able to open it (validates packs/objects).</item>
///   <item>Path must not already be tracked (duplicate check).</item>
/// </list>
/// </remarks>
public sealed class AddRepositoryCommandHandler
    : IRequestHandler<AddRepositoryCommand, RepositoryDto>
{
    private readonly IRepositoryRepository _repositoryRepo;
    private readonly IGitRepositoryService _gitService;
    private readonly IUnitOfWork           _unitOfWork;
    private readonly ILogger<AddRepositoryCommandHandler> _logger;

    /// <summary>
    /// Initialises the handler. All parameters are injected by MediatR's DI pipeline.
    /// </summary>
    public AddRepositoryCommandHandler(
        IRepositoryRepository repositoryRepo,
        IGitRepositoryService gitService,
        IUnitOfWork           unitOfWork,
        ILogger<AddRepositoryCommandHandler> logger)
    {
        _repositoryRepo = repositoryRepo ?? throw new ArgumentNullException(nameof(repositoryRepo));
        _gitService     = gitService     ?? throw new ArgumentNullException(nameof(gitService));
        _unitOfWork     = unitOfWork     ?? throw new ArgumentNullException(nameof(unitOfWork));
        _logger         = logger         ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public async Task<RepositoryDto> Handle(
        AddRepositoryCommand command,
        CancellationToken    cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Path, nameof(command.Path));

        var path = NormalisePath(command.Path);

        _logger.LogInformation("AddRepository | Registering path: {Path}", path);

        // ── Layer 1 & 2: directory must exist ─────────────────────────────────
        if (!Directory.Exists(path))
        {
            _logger.LogWarning("AddRepository | Path does not exist: {Path}", path);
            throw new DirectoryNotFoundException(
                $"The directory '{path}' does not exist or is inaccessible.");
        }

        // ── Layer 3: must contain a .git folder or be a bare repo ─────────────
        ValidateIsGitRepository(path);

        // ── Layer 4: LibGit2Sharp must be able to open it ─────────────────────
        // GetRepositoryInfoAsync throws ArgumentException if it is not a valid
        // Git repository (e.g., corrupted objects, incomplete clone).
        var repoInfo = await _gitService.GetRepositoryInfoAsync(path);

        _logger.LogDebug(
            "AddRepository | Git metadata — Name={Name} Commits={TotalCommits} LastCommit={Last:u}",
            repoInfo.Name, repoInfo.TotalCommits, repoInfo.LastCommitDate);

        // ── Layer 5: duplicate path check ─────────────────────────────────────
        var existing = await _repositoryRepo.GetByPathAsync(path);
        if (existing is not null)
        {
            _logger.LogWarning(
                "AddRepository | Path already tracked — Id={Id} Name={Name}",
                existing.Id, existing.Name);
            throw new InvalidOperationException(
                $"The repository at '{path}' is already tracked (Id = {existing.Id}).");
        }

        // ── Create and persist the entity ─────────────────────────────────────
        var repo = new Core.Entities.Repository
        {
            Id   = Guid.NewGuid(),
            Path = path,
            Name = repoInfo.Name,

            // Setting LastScannedUtc to UnixEpoch ensures the next hourly scan
            // cycle immediately picks up this repository and ingests its full
            // commit history (since all commits will be "since UnixEpoch").
            LastScannedUtc = DateTime.UnixEpoch
        };

        await _repositoryRepo.AddAsync(repo);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "AddRepository | Registered Id={Id} Name={Name} at {Path}",
            repo.Id, repo.Name, repo.Path);

        return ToDto(repo);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Validates that <paramref name="path"/> looks like a Git repository by
    /// checking for the conventional <c>.git</c> sub-directory (standard clone)
    /// or a <c>HEAD</c> file at the root (bare clone / worktree).
    /// </summary>
    private static void ValidateIsGitRepository(string path)
    {
        // Standard clone: contains a .git sub-directory (or a .git file for worktrees).
        var dotGit = System.IO.Path.Combine(path, ".git");
        if (Directory.Exists(dotGit) || File.Exists(dotGit))
            return;

        // Bare clone: HEAD file and objects/ directory sit directly in the repo root.
        var headFile   = System.IO.Path.Combine(path, "HEAD");
        var objectsDir = System.IO.Path.Combine(path, "objects");
        if (File.Exists(headFile) && Directory.Exists(objectsDir))
            return;

        throw new ArgumentException(
            $"'{path}' does not appear to be a Git repository " +
            "(no .git directory and no bare-repository structure found).",
            nameof(path));
    }

    /// <summary>
    /// Normalises a path: trims whitespace, resolves relative paths to absolute,
    /// and removes a trailing directory separator.
    /// </summary>
    /// <remarks>
    /// <see cref="System.IO.Path.GetFullPath"/> is called only for relative paths.
    /// Already-rooted paths (e.g. <c>/repos/my-project</c> on Linux or
    /// <c>D:\Projects\repo</c> on Windows) are used as-is to prevent the OS
    /// from mangling a path intended for a different platform — for example,
    /// calling <c>GetFullPath</c> on a Windows-style path inside a Linux container
    /// would prepend the process working directory and produce a nonsense path.
    /// </remarks>
    private static string NormalisePath(string path)
    {
        var trimmed  = path.Trim();
        // Only expand relative paths — leave already-rooted paths untouched.
        var absolute = System.IO.Path.IsPathRooted(trimmed)
            ? trimmed
            : System.IO.Path.GetFullPath(trimmed);
        return absolute.TrimEnd(
            System.IO.Path.DirectorySeparatorChar,
            System.IO.Path.AltDirectorySeparatorChar);
    }

    /// <summary>Maps a <see cref="Core.Entities.Repository"/> entity to a <see cref="RepositoryDto"/>.</summary>
    private static RepositoryDto ToDto(Core.Entities.Repository repo) =>
        new(repo.Id, repo.Path, repo.Name, repo.LastScannedUtc);
}
