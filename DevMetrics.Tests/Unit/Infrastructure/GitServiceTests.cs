using DevMetrics.Infrastructure.Services;
using DevMetrics.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevMetrics.Tests.Unit.Infrastructure;

/// <summary>
/// Integration-style unit tests for <see cref="GitService"/> that exercise
/// LibGit2Sharp against real temporary Git repositories created on disk.
/// Each test creates its own isolated repo and cleans up in <c>Dispose</c>.
/// </summary>
public sealed class GitServiceTests
{
    private readonly GitService _sut = new(NullLogger<GitService>.Instance);

    // ── GetRepositoryInfoAsync ────────────────────────────────────────────────

    [Fact]
    public async Task GetRepositoryInfoAsync_Should_ReturnRepoInfo_For_ValidRepo()
    {
        // Arrange — create a real repo with two commits.
        using var repo = new TestGitRepositoryBuilder();
        repo.AddCommit("Initial commit", [("README.md", "# Test Repo")]);
        repo.AddCommit("Second commit",  [("src/lib.cs", "class Lib {}")]);

        // Act
        var info = await _sut.GetRepositoryInfoAsync(repo.RepositoryPath);

        // Assert
        info.Should().NotBeNull();
        info.Path.Should().Be(repo.RepositoryPath);
        info.TotalCommits.Should().Be(2,
            "exactly two commits were made to this repository");
        info.LastCommitDate.Should().NotBeNull(
            "the repository has at least one commit");
        info.LastCommitDate!.Value.Should().BeCloseTo(
            DateTime.UtcNow, precision: TimeSpan.FromMinutes(2),
            "the last commit was just made");
    }

    [Fact]
    public async Task GetRepositoryInfoAsync_Should_Throw_When_PathNotGitRepo()
    {
        // Arrange — a directory that exists but is not a Git repository.
        var plainDir = TestGitRepositoryBuilder.CreateNonGitDirectory();

        try
        {
            // Act
            var act = async () => await _sut.GetRepositoryInfoAsync(plainDir);

            // Assert
            await act.Should().ThrowAsync<ArgumentException>(
                "GetRepositoryInfoAsync must throw when the path is not a Git repository")
                .WithMessage("*does not contain a valid Git repository*");
        }
        finally
        {
            if (Directory.Exists(plainDir))
                Directory.Delete(plainDir, recursive: true);
        }
    }

    // ── GetCommitsSinceAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task GetCommitsSinceAsync_Should_ReturnNewCommits_After_GivenDate()
    {
        // Arrange — create commits spread across the past 3 days.
        using var repo = new TestGitRepositoryBuilder();

        // One old commit (3 days ago) and two recent ones.
        repo.AddCommit("Old commit",    [("old.txt", "old")],    TimeSpan.FromDays(-3));
        repo.AddCommit("Recent commit", [("new1.txt", "new1")],  TimeSpan.FromHours(-2));
        repo.AddCommit("Latest commit", [("new2.txt", "new2")],  TimeSpan.FromMinutes(-30));

        // Only retrieve commits made in the last 24 hours.
        var since = DateTime.UtcNow.AddDays(-1);

        // Act
        var commits = await _sut.GetCommitsSinceAsync(repo.RepositoryPath, since);

        // Assert
        commits.Should().HaveCount(2,
            "only the two commits made within the last 24 hours should be returned");
        commits.Should().BeInAscendingOrder(c => c.DateUtc,
            "GetCommitsSinceAsync returns commits oldest-first per its contract");
        commits.Should().AllSatisfy(c =>
        {
            c.Hash.Should().HaveLength(40,
                "SHA-1 hashes are always 40 hex characters");
            c.Author.Should().Be("Test Author");
        });
    }

    [Fact]
    public async Task GetCommitsSinceAsync_Should_ReturnAllCommits_When_SinceIsUnixEpoch()
    {
        // Arrange
        using var repo = new TestGitRepositoryBuilder();
        var hashes = repo.AddCommits(count: 5, daysBack: 7);

        // Act — DateTime.UnixEpoch means "give me everything"
        var commits = await _sut.GetCommitsSinceAsync(
            repo.RepositoryPath, DateTime.UnixEpoch);

        // Assert
        commits.Should().HaveCount(5,
            "all 5 commits are after UnixEpoch");
        commits.Select(c => c.Hash)
               .Should().BeEquivalentTo(hashes,
                   "every hash created by the builder should appear in the results");
    }

    [Fact]
    public async Task GetCommitsSinceAsync_Should_CalculateDiffStats_Correctly()
    {
        // Arrange — one commit with a known file change.
        using var repo = new TestGitRepositoryBuilder();

        // Initial commit: creates a 3-line file.
        repo.AddCommit("Add file", [("data.txt", "line1\nline2\nline3\n")]);

        // Second commit: overwrites with 5 lines — adds 5, removes 3.
        repo.AddCommit("Modify file",
            [("data.txt", "line1\nline2\nline3\nline4\nline5\n")]);

        var since = DateTime.UtcNow.AddDays(-1);

        // Act
        var commits = await _sut.GetCommitsSinceAsync(repo.RepositoryPath, since);

        // Assert — get the "Modify file" commit (second and most recent).
        // Only the second commit falls after "since" if the first was also recent;
        // in this test both are recent so we look at the latest one.
        var modifyCommit = commits.Last();

        modifyCommit.LinesAdded.Should().BeGreaterThan(0,
            "modifying a file should register added lines");
        modifyCommit.FilesChanged.Should().Be(1,
            "only one file was modified in this commit");
    }

    [Fact]
    public async Task GetCommitsSinceAsync_Should_Throw_When_PathIsInvalid()
    {
        // Arrange
        var invalidPath = "/this/path/does/not/exist/at/all";

        // Act
        var act = async () =>
            await _sut.GetCommitsSinceAsync(invalidPath, DateTime.UtcNow.AddDays(-1));

        // Assert
        await act.Should().ThrowAsync<ArgumentException>(
            "a non-existent path cannot be opened as a Git repository");
    }
}
