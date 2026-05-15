using LibGit2Sharp;

namespace DevMetrics.Tests.Helpers;

/// <summary>
/// Builds a real, fully-functional Git repository in a temporary directory
/// for use in integration and GitService unit tests.
/// Implements <see cref="IDisposable"/> to guarantee cleanup of temp files.
/// </summary>
/// <remarks>
/// Uses LibGit2Sharp directly (available via the Infrastructure project reference)
/// to avoid shelling out to the <c>git</c> CLI, which may not be installed or may
/// differ in version between CI environments.
///
/// Usage:
/// <code>
/// using var builder = new TestGitRepositoryBuilder();
/// builder.AddCommit("Initial commit", ("README.md", "# Hello"));
/// builder.AddCommit("Add feature", ("src/feature.cs", "class F {}"));
/// string repoPath = builder.RepositoryPath;
/// </code>
/// </remarks>
public sealed class TestGitRepositoryBuilder : IDisposable
{
    private static readonly Signature TestAuthor = new(
        "Test Author", "test@devmetrics.local",
        DateTimeOffset.UtcNow.AddDays(-7));

    private readonly string _tempPath;
    private bool _disposed;

    /// <summary>Absolute path to the repository root (contains <c>.git</c>).</summary>
    public string RepositoryPath => _tempPath;

    /// <summary>
    /// Initialises and creates the Git repository on disk.
    /// </summary>
    public TestGitRepositoryBuilder()
    {
        _tempPath = Path.Combine(
            Path.GetTempPath(),
            "devmetrics-tests",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(_tempPath);
        Repository.Init(_tempPath);
    }

    // ── Fluent API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Adds a commit to the repository's current branch.
    /// </summary>
    /// <param name="message">The commit message.</param>
    /// <param name="files">
    /// One or more <c>(relativePath, content)</c> tuples representing files
    /// to create or overwrite in this commit.
    /// </param>
    /// <param name="authorOffset">
    /// Optional offset from now for the commit's author timestamp.
    /// Defaults to <c>TimeSpan.Zero</c> (now).
    /// Use negative offsets to create older commits: <c>TimeSpan.FromDays(-3)</c>.
    /// </param>
    /// <returns>The SHA-1 hash of the new commit.</returns>
    public string AddCommit(
        string message,
        (string Path, string Content)[] files,
        TimeSpan? authorOffset = null)
    {
        using var repo = new Repository(_tempPath);

        foreach (var (relativePath, content) in files)
        {
            var fullPath = Path.Combine(_tempPath, relativePath);
            var dir      = Path.GetDirectoryName(fullPath)!;

            Directory.CreateDirectory(dir);
            File.WriteAllText(fullPath, content);

            // Stage the file.
            Commands.Stage(repo, relativePath);
        }

        var when = DateTimeOffset.UtcNow.Add(authorOffset ?? TimeSpan.Zero);
        var sig   = new Signature("Test Author", "test@devmetrics.local", when);

        var commit = repo.Commit(message, sig, sig);
        return commit.Sha;
    }

    /// <summary>
    /// Adds a sequence of commits spread evenly across the past
    /// <paramref name="daysBack"/> days. Useful for generating chart test data.
    /// </summary>
    /// <param name="count">Number of commits to create.</param>
    /// <param name="daysBack">Maximum age of the oldest commit in days.</param>
    /// <returns>The SHA hashes of all created commits, oldest first.</returns>
    public IReadOnlyList<string> AddCommits(int count, int daysBack = 7)
    {
        var hashes = new List<string>(count);
        var step   = TimeSpan.FromDays((double)daysBack / count);

        for (var i = 0; i < count; i++)
        {
            var offset = -TimeSpan.FromDays(daysBack) + step * i;
            var hash   = AddCommit(
                $"Commit {i + 1} of {count}",
                [($"file-{i}.txt", $"content {i} @ {DateTimeOffset.UtcNow.Add(offset)}")],
                offset);
            hashes.Add(hash);
        }

        return hashes;
    }

    /// <summary>Returns the path to the repository without a <c>.git</c> sub-directory,
    /// simulating a non-Git directory for negative tests.</summary>
    public static string CreateNonGitDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(), "devmetrics-tests", "nongit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            // LibGit2Sharp may have held file handles — retry deletion a few times.
            DeleteWithRetry(_tempPath);
        }
        catch
        {
            // Suppress cleanup failures — temp dir cleanup is best-effort.
        }
    }

    private static void DeleteWithRetry(string path, int maxAttempts = 3)
    {
        for (var i = 0; i < maxAttempts; i++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    // Force-clear read-only attributes (common on .git objects).
                    foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                        File.SetAttributes(file, FileAttributes.Normal);

                    Directory.Delete(path, recursive: true);
                }
                return;
            }
            catch when (i < maxAttempts - 1)
            {
                Thread.Sleep(100);
            }
        }
    }
}
