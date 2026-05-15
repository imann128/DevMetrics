namespace DevMetrics.Core.DTOs;

/// <summary>
/// An immutable snapshot of high-level metadata about a Git repository as read
/// directly from the <c>.git</c> directory on disk.
/// Returned by <see cref="Interfaces.IGitRepositoryService.GetRepositoryInfoAsync"/>
/// and used by the Application layer to initialise or refresh a
/// <see cref="Entities.Repository"/> entity.
/// </summary>
/// <param name="Path">
/// The absolute file system path to the repository root — the directory that
/// contains the <c>.git</c> folder.
/// </param>
/// <param name="Name">
/// The display name of the repository, derived from the remote's repository name
/// (e.g., <c>origin</c> URL) or, if no remote is configured, from the directory name.
/// </param>
/// <param name="LastCommitDate">
/// The UTC timestamp of the most recent commit on the currently checked-out branch,
/// or <c>null</c> if the repository has no commits yet (empty repository).
/// </param>
/// <param name="TotalCommits">
/// The total number of reachable commits on the current branch.
/// This is an approximation for very large repositories — LibGit2Sharp walks
/// the entire commit graph, so callers should cache this value rather than
/// computing it on every request.
/// </param>
public record RepositoryInfo(
    string Path,
    string Name,
    DateTime? LastCommitDate,
    int TotalCommits
);
