using DevMetrics.Application.Commands;
using DevMetrics.Application.DTOs;
using DevMetrics.Core.DTOs;
using DevMetrics.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace DevMetrics.Tests.Unit.Application;

/// <summary>
/// Unit tests for <see cref="AddRepositoryCommandHandler"/>.
/// File system interactions (Directory.Exists, .git check) require real temp directories,
/// so tests that hit those paths create and clean up temporary folders.
/// The <see cref="IGitRepositoryService"/> and repository interfaces are mocked.
/// </summary>
public sealed class AddRepositoryCommandHandlerTests : IDisposable
{
    private readonly Mock<IRepositoryRepository> _repoRepo    = new();
    private readonly Mock<IGitRepositoryService> _gitService  = new();
    private readonly Mock<IUnitOfWork>           _unitOfWork  = new();

    // Temp directories created during tests — cleaned up in Dispose.
    private readonly List<string> _tempDirs = new();

    private AddRepositoryCommandHandler CreateHandler() => new(
        _repoRepo.Object,
        _gitService.Object,
        _unitOfWork.Object,
        NullLogger<AddRepositoryCommandHandler>.Instance);

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a temp directory with a real <c>.git</c> sub-folder,
    /// simulating a valid (though empty-history) Git repository for validation purposes.
    /// </summary>
    private string CreateFakeGitDir(string? name = null)
    {
        var path = Path.Combine(
            Path.GetTempPath(), "devmetrics-unit-tests",
            name ?? Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(path);
        Directory.CreateDirectory(Path.Combine(path, ".git"));
        _tempDirs.Add(path);
        return path;
    }

    private string CreateNonGitDir()
    {
        var path = Path.Combine(
            Path.GetTempPath(), "devmetrics-unit-tests",
            "nongit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        _tempDirs.Add(path);
        return path;
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Should_AddRepository_When_ValidGitRepo()
    {
        // Arrange
        var repoPath = CreateFakeGitDir("my-project");

        var repoInfo = new RepositoryInfo(
            Path:           repoPath,
            Name:           "my-project",
            LastCommitDate: DateTime.UtcNow.AddDays(-1),
            TotalCommits:   7);

        _gitService.Setup(g => g.GetRepositoryInfoAsync(repoPath))
                   .ReturnsAsync(repoInfo);

        // Path not yet tracked.
        _repoRepo.Setup(r => r.GetByPathAsync(repoPath))
                 .ReturnsAsync((Core.Entities.Repository?)null);

        _repoRepo.Setup(r => r.AddAsync(It.IsAny<Core.Entities.Repository>()))
                 .Returns(Task.CompletedTask);

        _unitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()))
                   .ReturnsAsync(1);

        var handler = CreateHandler();

        // Act
        var dto = await handler.Handle(
            new AddRepositoryCommand(repoPath), CancellationToken.None);

        // Assert
        dto.Should().NotBeNull();
        dto.Name.Should().Be("my-project");
        dto.LastScannedUtc.Should().Be(DateTime.UnixEpoch,
            "a newly added repository should have LastScannedUtc = UnixEpoch " +
            "so the background service picks it up on its next tick");

        _repoRepo.Verify(
            r => r.AddAsync(It.Is<Core.Entities.Repository>(
                entity => entity.Path == repoPath
                       && entity.Name == "my-project"
                       && entity.LastScannedUtc == DateTime.UnixEpoch)),
            Times.Once,
            "the repository entity must be staged for insertion with UnixEpoch timestamp");

        _unitOfWork.Verify(
            u => u.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Should_ThrowDirectoryNotFoundException_When_PathDoesNotExist()
    {
        // Arrange — path that definitely does not exist.
        var nonExistentPath = Path.Combine(
            Path.GetTempPath(), "devmetrics-unit-tests", "does-not-exist-" + Guid.NewGuid().ToString("N"));

        var handler = CreateHandler();

        // Act
        var act = async () =>
            await handler.Handle(
                new AddRepositoryCommand(nonExistentPath), CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<DirectoryNotFoundException>(
            "the handler must throw DirectoryNotFoundException when the path is absent");

        // Git service and repo should never be called.
        _gitService.Verify(g => g.GetRepositoryInfoAsync(It.IsAny<string>()), Times.Never);
        _repoRepo.Verify(r => r.AddAsync(It.IsAny<Core.Entities.Repository>()), Times.Never);
    }

    [Fact]
    public async Task Should_ThrowArgumentException_When_NotGitRepo()
    {
        // Arrange — a real directory that exists but has no .git sub-folder.
        var plainDir = CreateNonGitDir();

        var handler = CreateHandler();

        // Act
        var act = async () =>
            await handler.Handle(
                new AddRepositoryCommand(plainDir), CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>(
            "a directory without a .git folder is not a valid Git repository")
            .WithMessage("*does not appear to be a Git repository*");

        _gitService.Verify(g => g.GetRepositoryInfoAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Should_ThrowInvalidOperationException_When_DuplicatePath()
    {
        // Arrange
        var repoPath = CreateFakeGitDir("existing-repo");

        var existingEntity = new Core.Entities.Repository
        {
            Id   = Guid.NewGuid(),
            Path = repoPath,
            Name = "existing-repo"
        };

        _gitService.Setup(g => g.GetRepositoryInfoAsync(repoPath))
                   .ReturnsAsync(new RepositoryInfo(repoPath, "existing-repo", null, 0));

        // The path is already tracked.
        _repoRepo.Setup(r => r.GetByPathAsync(repoPath))
                 .ReturnsAsync(existingEntity);

        var handler = CreateHandler();

        // Act
        var act = async () =>
            await handler.Handle(
                new AddRepositoryCommand(repoPath), CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>(
            "adding a repository whose path is already tracked should be rejected")
            .WithMessage("*already tracked*");

        _repoRepo.Verify(r => r.AddAsync(It.IsAny<Core.Entities.Repository>()), Times.Never,
            "AddAsync must not be called when a duplicate is detected");
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch { /* best effort */ }
        }
    }
}
