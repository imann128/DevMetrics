using DevMetrics.Core.DTOs;

namespace DevMetrics.Core.Interfaces;

/// <summary>
/// Defines the contract for reading data from a local Git repository on disk.
/// The Infrastructure layer implements this using LibGit2Sharp; the Core and
/// Application layers depend only on this abstraction, keeping them testable
/// without a real <c>.git</c> directory.
/// </summary>
public interface IGitRepositoryService
{
    /// <summary>
    /// Reads high-level metadata about the repository at <paramref name="path"/>.
    /// This is a lightweight operation that does not walk the full commit history —
    /// it reads only the HEAD commit and the total commit count from the current branch.
    /// </summary>
    /// <param name="path">
    /// The absolute file system path to the repository root
    /// (the directory containing the <c>.git</c> folder).
    /// </param>
    /// <returns>
    /// A <see cref="RepositoryInfo"/> record with the repository's name,
    /// last commit date, and total commit count on the current branch.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="path"/> does not point to a valid Git repository.
    /// </exception>
    Task<RepositoryInfo> GetRepositoryInfoAsync(string path);

    /// <summary>
    /// Retrieves every commit authored after <paramref name="since"/> on the
    /// repository's currently checked-out branch, together with its diff statistics.
    /// </summary>
    /// <remarks>
    /// Diff statistics (lines added/deleted, files changed) are computed by comparing
    /// each commit tree to its first parent. Merge commits are included but their
    /// diff stats reflect only the merge resolution changes, not all merged commits.
    /// </remarks>
    /// <param name="path">
    /// The absolute file system path to the repository root.
    /// </param>
    /// <param name="since">
    /// The exclusive UTC lower bound. Only commits with an author date strictly
    /// after this timestamp are returned. Pass <see cref="DateTime.MinValue"/> to
    /// retrieve the entire history.
    /// </param>
    /// <returns>
    /// A read-only list of <see cref="GitCommit"/> records ordered by author date
    /// ascending (oldest first), ready for ingestion into <see cref="ICommitRecordRepository"/>.
    /// </returns>
    Task<IReadOnlyList<GitCommit>> GetCommitsSinceAsync(string path, DateTime since);
}
