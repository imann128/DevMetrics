using DevMetrics.Core.DTOs;
using DevMetrics.Core.Interfaces;
using LibGit2Sharp;
using Microsoft.Extensions.Logging;

// Alias LibGit2Sharp.Repository to avoid collision with DevMetrics.Core.Entities.Repository.
// GitService only works with DTOs, but the alias keeps the intent explicit.
using GitRepository = LibGit2Sharp.Repository;

namespace DevMetrics.Infrastructure.Services;

/// <summary>
/// <see cref="IGitRepositoryService"/> implementation that uses LibGit2Sharp
/// to read commit history and diff statistics from local Git repositories.
/// </summary>
/// <remarks>
/// <para>
/// LibGit2Sharp wraps the native <c>libgit2</c> C library and exposes it via
/// managed handles. Every <see cref="GitRepository"/> instance is an
/// <see cref="IDisposable"/> that holds a file-system lock on the <c>.git</c>
/// directory — all <c>using</c> blocks here ensure handles are released promptly.
/// </para>
/// <para>
/// Both public methods are implemented synchronously and wrapped in
/// <see cref="Task.FromResult{T}"/> because LibGit2Sharp has no async API.
/// The CPU-bound diff computation is fast enough for the hourly background
/// cadence; if it becomes a bottleneck, wrap the call in
/// <c>Task.Run(() => ...)</c> at the call site to offload it from the thread pool.
/// </para>
/// </remarks>
public sealed class GitService : IGitRepositoryService
{
    private readonly ILogger<GitService> _logger;

    /// <summary>
    /// Initialises a new instance of <see cref="GitService"/>.
    /// </summary>
    /// <param name="logger">The structured logger. Must not be null.</param>
    public GitService(ILogger<GitService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // ── IGitRepositoryService ─────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task<RepositoryInfo> GetRepositoryInfoAsync(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, nameof(path));

        _logger.LogDebug("Git | Reading metadata from {Path}", path);

        try
        {
            using var repo = new GitRepository(path);

            var headCommit = repo.Head.Tip;     // null on a brand-new empty repo
            var name       = DeriveRepositoryName(repo, path);
            var total      = CountCommits(repo); // walks full history — cached by LibGit2Sharp

            var info = new RepositoryInfo(
                Path:           path,
                Name:           name,
                LastCommitDate: headCommit?.Author.When.UtcDateTime,
                TotalCommits:   total
            );

            _logger.LogInformation(
                "Git | {Name} — {TotalCommits} commits, last at {LastCommitDate:u}",
                info.Name, info.TotalCommits, info.LastCommitDate);

            return Task.FromResult(info);
        }
        catch (RepositoryNotFoundException ex)
        {
            _logger.LogError(ex,
                "Git | No valid Git repository found at {Path}", path);
            throw new ArgumentException(
                $"'{path}' does not contain a valid Git repository (.git folder not found).",
                nameof(path), ex);
        }
        catch (LibGit2SharpException ex)
        {
            _logger.LogError(ex,
                "Git | LibGit2Sharp exception reading metadata from {Path}", path);
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex,
                "Git | Access denied reading repository at {Path}", path);
            throw;
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<GitCommit>> GetCommitsSinceAsync(string path, DateTime since)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, nameof(path));

        // Normalise the lower-bound to UTC regardless of what the caller passes.
        var sinceUtc = since.Kind == DateTimeKind.Utc
            ? since
            : since.ToUniversalTime();

        _logger.LogDebug("Git | Reading commits from {Path} since {Since:u}", path, sinceUtc);

        try
        {
            using var repo = new GitRepository(path);

            // Walk commits on the current branch in reverse-chronological order
            // (newest first). CommitSortStrategies.Time is the default but
            // stated explicitly for clarity.
            var filter = new CommitFilter
            {
                SortBy              = CommitSortStrategies.Time,
                IncludeReachableFrom = repo.Head
            };

            var results = new List<GitCommit>();

            foreach (var commit in repo.Commits.QueryBy(filter))
            {
                var authorDateUtc = commit.Author.When.UtcDateTime;

                // Commits are time-ordered descending. The first commit whose author
                // date is at or before `sinceUtc` means all subsequent commits will
                // also be older — we can break early.
                // NOTE: Out-of-order timestamps from force-pushed or amended commits
                // are rare in practice but possible. If strict correctness is required
                // replace TakeWhile semantics here with a full Where pass.
                if (authorDateUtc <= sinceUtc)
                    break;

                var (linesAdded, linesDeleted, filesChanged) = ComputeDiffStats(repo, commit);

                results.Add(new GitCommit(
                    Hash:         commit.Sha,   // LibGit2Sharp always returns lowercase hex
                    Author:       commit.Author.Name,
                    DateUtc:      authorDateUtc,
                    LinesAdded:   linesAdded,
                    LinesDeleted: linesDeleted,
                    FilesChanged: filesChanged
                ));
            }

            // Reverse so the caller receives oldest-first, matching the contract.
            results.Reverse();

            _logger.LogInformation(
                "Git | Found {Count} new commit(s) in {Path} since {Since:u}",
                results.Count, path, sinceUtc);

            return Task.FromResult<IReadOnlyList<GitCommit>>(results);
        }
        catch (RepositoryNotFoundException ex)
        {
            _logger.LogError(ex,
                "Git | No valid Git repository found at {Path}", path);
            throw new ArgumentException(
                $"'{path}' does not contain a valid Git repository.",
                nameof(path), ex);
        }
        catch (LibGit2SharpException ex)
        {
            _logger.LogError(ex,
                "Git | LibGit2Sharp exception reading commits from {Path}", path);
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex,
                "Git | Access denied reading commits from {Path}", path);
            throw;
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Computes the diff statistics for a single commit against its first parent.
    /// For root commits (no parents) the diff is computed against an empty tree.
    /// </summary>
    /// <param name="repo">The open LibGit2Sharp repository handle.</param>
    /// <param name="commit">The commit whose diff statistics to compute.</param>
    /// <returns>
    /// A tuple of (LinesAdded, LinesDeleted, FilesChanged) for the commit.
    /// Returns (0, 0, 0) if the diff cannot be computed (e.g., binary files only).
    /// </returns>
    private static (int LinesAdded, int LinesDeleted, int FilesChanged) ComputeDiffStats(
        GitRepository repo,
        Commit commit)
    {
        try
        {
            Tree? oldTree;

            if (!commit.Parents.Any())
            {
                // Root/initial commit: diff against empty tree (null = empty tree in libgit2).
                oldTree = null;
            }
            else
            {
                // For merge commits this uses only the first parent, which captures
                // the actual code changes rather than the combined merge diff.
                oldTree = commit.Parents.First().Tree;
            }

            // Compare<Patch> returns line-level diff statistics.
            // IDisposable — dispose promptly to release native memory.
            using var patch = repo.Diff.Compare<Patch>(oldTree, commit.Tree);

            return (
                LinesAdded:   patch.LinesAdded,
                LinesDeleted: patch.LinesDeleted,
                FilesChanged: patch.Count()    // one PatchEntryChanges per file
            );
        }
        catch (LibGit2SharpException)
        {
            // Some commits (e.g., subtree merges, packed objects) cannot be diffed.
            // Return zeroed stats rather than propagating and skipping the commit entirely.
            return (0, 0, 0);
        }
    }

    /// <summary>
    /// Derives a human-readable repository name by inspecting the <c>origin</c>
    /// remote URL, falling back to the directory name if no remote is configured.
    /// </summary>
    /// <param name="repo">The open LibGit2Sharp repository handle.</param>
    /// <param name="path">The file system path used as a fallback.</param>
    /// <returns>A non-empty display name for the repository.</returns>
    private static string DeriveRepositoryName(GitRepository repo, string path)
    {
        // Prefer the remote name as it's more meaningful than the local folder name
        // (e.g., "my-project" from "git@github.com:user/my-project.git").
        var origin = repo.Network.Remotes["origin"];
        if (origin is not null)
        {
            var url = origin.Url.TrimEnd('/');
            var segment = url.Split('/').LastOrDefault() ?? string.Empty;

            // Strip the conventional ".git" suffix from SSH and HTTPS clone URLs.
            if (segment.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                segment = segment[..^4];

            if (!string.IsNullOrWhiteSpace(segment))
                return segment;
        }

        // Fall back to the directory name (trim trailing path separator first
        // so Path.GetFileName works correctly on paths that end with '/').
        return System.IO.Path.GetFileName(
                   path.TrimEnd(System.IO.Path.DirectorySeparatorChar,
                                System.IO.Path.AltDirectorySeparatorChar))
               ?? path;
    }

    /// <summary>
    /// Counts the total number of reachable commits on the repository's current branch.
    /// </summary>
    /// <remarks>
    /// This walks the entire commit graph and is O(n) in history length.
    /// For repositories with tens of thousands of commits this will be measurably slow;
    /// the result should be cached in <see cref="Core.Entities.Repository"/> and
    /// re-computed only during full scans.
    /// </remarks>
    private static int CountCommits(GitRepository repo)
    {
        var filter = new CommitFilter
        {
            SortBy               = CommitSortStrategies.None,  // unordered count is faster
            IncludeReachableFrom = repo.Head
        };

        return repo.Commits.QueryBy(filter).Count();
    }
}
