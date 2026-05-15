using DevMetrics.Application.Commands;
using DevMetrics.Application.DTOs;
using DevMetrics.Application.Services;
using DevMetrics.Core.DTOs;
using DevMetrics.Core.Entities;
using DevMetrics.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace DevMetrics.Tests.Unit.Application;

/// <summary>
/// Unit tests for <see cref="ScanRepositoriesCommandHandler"/>.
/// All external dependencies are mocked — no file system or database access.
/// </summary>
public sealed class ScanRepositoriesCommandHandlerTests
{
    // ── Shared mocks ──────────────────────────────────────────────────────────

    private readonly Mock<IRepositoryRepository>   _repoRepo        = new();
    private readonly Mock<ICommitRecordRepository> _commitRepo      = new();
    private readonly Mock<IDailySummaryRepository> _dailySummaryRepo = new();
    private readonly Mock<IGitRepositoryService>   _gitService      = new();
    private readonly Mock<IUnitOfWork>             _unitOfWork      = new();
    private readonly Mock<IScanNotifier>           _notifier        = new();

    private ScanRepositoriesCommandHandler CreateHandler() => new(
        _repoRepo.Object,
        _commitRepo.Object,
        _dailySummaryRepo.Object,
        _gitService.Object,
        _unitOfWork.Object,
        _notifier.Object,
        NullLogger<ScanRepositoriesCommandHandler>.Instance);

    // ── Test data factory ─────────────────────────────────────────────────────

    private static Repository MakeRepo(string name = "test-repo") => new()
    {
        Id             = Guid.NewGuid(),
        Path           = $"/repos/{name}",
        Name           = name,
        // LastScannedUtc far in the past so it passes the 1-hour threshold.
        LastScannedUtc = DateTime.UtcNow.AddHours(-2)
    };

    private static GitCommit MakeGitCommit(string hash, DateTime? date = null) => new(
        Hash:         hash,
        Author:       "Test Author",
        DateUtc:      date ?? DateTime.UtcNow.AddMinutes(-30),
        LinesAdded:   10,
        LinesDeleted: 3,
        FilesChanged: 2);

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Should_AddNewCommits_When_NewCommitsExist()
    {
        // Arrange
        var repo      = MakeRepo();
        var gitCommit = MakeGitCommit("abc123def456abc123def456abc123def456abc1");

        _repoRepo.Setup(r => r.GetNeedsScanAsync(It.IsAny<DateTime>()))
                 .ReturnsAsync(new List<Repository> { repo });

        _gitService.Setup(g => g.GetCommitsSinceAsync(repo.Path, repo.LastScannedUtc))
                   .ReturnsAsync(new List<GitCommit> { gitCommit });

        // Hash does not exist yet — this is a new commit.
        _commitRepo.Setup(c => c.CommitExistsAsync(gitCommit.Hash))
                   .ReturnsAsync(false);

        _commitRepo.Setup(c => c.AddRangeAsync(It.IsAny<IEnumerable<CommitRecord>>()))
                   .Returns(Task.CompletedTask);

        _repoRepo.Setup(r => r.UpdateAsync(It.IsAny<Repository>()))
                 .Returns(Task.CompletedTask);

        _unitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()))
                   .ReturnsAsync(1);

        // Provide the commit records that RecalculateDailySummaryAsync will query.
        var storedRecord = new CommitRecord
        {
            Id           = Guid.NewGuid(),
            RepositoryId = repo.Id,
            Hash         = gitCommit.Hash,
            DateUtc      = gitCommit.DateUtc,
            LinesAdded   = gitCommit.LinesAdded,
            LinesDeleted = gitCommit.LinesDeleted,
            FilesChanged = gitCommit.FilesChanged
        };

        _commitRepo.Setup(c => c.GetByRepositoryAndDateRangeAsync(
                       repo.Id, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
                   .ReturnsAsync(new List<CommitRecord> { storedRecord });

        _dailySummaryRepo.Setup(d => d.UpsertAsync(It.IsAny<DailySummary>()))
                         .Returns(Task.CompletedTask);

        _notifier.Setup(n => n.NotifyScanCompletedAsync(
                     It.IsAny<ScanResultDto>(), It.IsAny<CancellationToken>()))
                 .Returns(Task.CompletedTask);

        var handler = CreateHandler();

        // Act
        var result = await handler.Handle(new ScanRepositoriesCommand(), CancellationToken.None);

        // Assert
        result.NewCommitsFound.Should().Be(1,
            "one new commit was returned by the git service and was not already present");
        result.RepositoriesScanned.Should().Be(1);
        result.Status.Should().Be("Completed");

        _commitRepo.Verify(
            c => c.AddRangeAsync(It.Is<IEnumerable<CommitRecord>>(
                list => list.Any(r => r.Hash == gitCommit.Hash))),
            Times.Once,
            "the new commit should be staged for insertion exactly once");

        _unitOfWork.Verify(
            u => u.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Once,
            "SaveChangesAsync should commit the staged commit and repo timestamp update");
    }

    [Fact]
    public async Task Should_SkipExistingCommits_When_HashAlreadyPresent()
    {
        // Arrange
        var repo      = MakeRepo();
        var gitCommit = MakeGitCommit("aaaa1111aaaa1111aaaa1111aaaa1111aaaa1111");

        _repoRepo.Setup(r => r.GetNeedsScanAsync(It.IsAny<DateTime>()))
                 .ReturnsAsync(new List<Repository> { repo });

        _gitService.Setup(g => g.GetCommitsSinceAsync(repo.Path, repo.LastScannedUtc))
                   .ReturnsAsync(new List<GitCommit> { gitCommit });

        // Hash already in the database — simulate a re-run after partial failure.
        _commitRepo.Setup(c => c.CommitExistsAsync(gitCommit.Hash))
                   .ReturnsAsync(true);

        _repoRepo.Setup(r => r.UpdateAsync(It.IsAny<Repository>()))
                 .Returns(Task.CompletedTask);

        _unitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()))
                   .ReturnsAsync(1);

        _notifier.Setup(n => n.NotifyScanCompletedAsync(
                     It.IsAny<ScanResultDto>(), It.IsAny<CancellationToken>()))
                 .Returns(Task.CompletedTask);

        var handler = CreateHandler();

        // Act
        var result = await handler.Handle(new ScanRepositoriesCommand(), CancellationToken.None);

        // Assert
        result.NewCommitsFound.Should().Be(0,
            "the only commit from Git was already in the database");

        _commitRepo.Verify(
            c => c.AddRangeAsync(It.IsAny<IEnumerable<CommitRecord>>()),
            Times.Never,
            "AddRangeAsync must not be called when all commits already exist");

        // StampAndSaveAsync path: UpdateAsync + SaveChangesAsync called once.
        _repoRepo.Verify(r => r.UpdateAsync(repo), Times.Once);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Should_UpdateDailySummary_AfterAddingCommits()
    {
        // Arrange
        var repo = MakeRepo();
        var today = DateTime.UtcNow.Date;

        // Two commits on the same day.
        var commit1 = MakeGitCommit("bbbb2222bbbb2222bbbb2222bbbb2222bbbb2222",
            today.AddHours(9));
        var commit2 = MakeGitCommit("cccc3333cccc3333cccc3333cccc3333cccc3333",
            today.AddHours(14));

        _repoRepo.Setup(r => r.GetNeedsScanAsync(It.IsAny<DateTime>()))
                 .ReturnsAsync(new List<Repository> { repo });

        _gitService.Setup(g => g.GetCommitsSinceAsync(repo.Path, repo.LastScannedUtc))
                   .ReturnsAsync(new List<GitCommit> { commit1, commit2 });

        _commitRepo.Setup(c => c.CommitExistsAsync(It.IsAny<string>()))
                   .ReturnsAsync(false);

        _commitRepo.Setup(c => c.AddRangeAsync(It.IsAny<IEnumerable<CommitRecord>>()))
                   .Returns(Task.CompletedTask);

        _repoRepo.Setup(r => r.UpdateAsync(It.IsAny<Repository>()))
                 .Returns(Task.CompletedTask);

        _unitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()))
                   .ReturnsAsync(2);

        // Return both stored records when the handler queries for the day's commits.
        var storedRecords = new List<CommitRecord>
        {
            new() { Id = Guid.NewGuid(), RepositoryId = repo.Id,
                    DateUtc = commit1.DateUtc, LinesAdded = 10, LinesDeleted = 3 },
            new() { Id = Guid.NewGuid(), RepositoryId = repo.Id,
                    DateUtc = commit2.DateUtc, LinesAdded = 20, LinesDeleted = 5 }
        };

        _commitRepo.Setup(c => c.GetByRepositoryAndDateRangeAsync(
                       repo.Id, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
                   .ReturnsAsync(storedRecords);

        DailySummary? capturedSummary = null;
        _dailySummaryRepo.Setup(d => d.UpsertAsync(It.IsAny<DailySummary>()))
                         .Callback<DailySummary>(s => capturedSummary = s)
                         .Returns(Task.CompletedTask);

        _notifier.Setup(n => n.NotifyScanCompletedAsync(
                     It.IsAny<ScanResultDto>(), It.IsAny<CancellationToken>()))
                 .Returns(Task.CompletedTask);

        var handler = CreateHandler();

        // Act
        await handler.Handle(new ScanRepositoriesCommand(), CancellationToken.None);

        // Assert — the summary must aggregate both commits correctly.
        capturedSummary.Should().NotBeNull("UpsertAsync must have been called");
        capturedSummary!.TotalCommits.Should().Be(2);
        capturedSummary.TotalLinesAdded.Should().Be(30,   "10 + 20");
        capturedSummary.TotalLinesDeleted.Should().Be(8,  "3 + 5");
        capturedSummary.RepositoryId.Should().Be(repo.Id);
        capturedSummary.Date.Should().Be(today,
            "DailySummary.Date must be normalised to midnight");
    }

    [Fact]
    public async Task Should_HandleGitExceptions_Gracefully()
    {
        // Arrange — the git service throws for one repo.
        var failingRepo = MakeRepo("failing-repo");

        _repoRepo.Setup(r => r.GetNeedsScanAsync(It.IsAny<DateTime>()))
                 .ReturnsAsync(new List<Repository> { failingRepo });

        _gitService.Setup(g => g.GetCommitsSinceAsync(failingRepo.Path, It.IsAny<DateTime>()))
                   .ThrowsAsync(new LibGit2Sharp.RepositoryNotFoundException(
                       $"Repository not found at {failingRepo.Path}"));

        _notifier.Setup(n => n.NotifyScanCompletedAsync(
                     It.IsAny<ScanResultDto>(), It.IsAny<CancellationToken>()))
                 .Returns(Task.CompletedTask);

        var handler = CreateHandler();

        // Act — capture result from a single invocation.
        // The handler must not throw (per-repo exceptions are caught internally)
        // and must report "Failed" when every repository in the cycle failed.
        ScanResultDto result = null!;
        var act = async () =>
        {
            result = await handler.Handle(new ScanRepositoriesCommand(), CancellationToken.None);
        };

        // Assert
        await act.Should().NotThrowAsync(
            "per-repository exceptions must be caught so the entire cycle does not abort");

        result.Status.Should().Be("Failed",
            "when the only repository fails, the overall status should be 'Failed'");
        result.NewCommitsFound.Should().Be(0);
    }

    [Fact]
    public async Task Should_ReportPartialFailure_When_OneOfMultipleReposFails()
    {
        // Arrange
        var goodRepo = MakeRepo("good-repo");
        var badRepo  = MakeRepo("bad-repo");

        _repoRepo.Setup(r => r.GetNeedsScanAsync(It.IsAny<DateTime>()))
                 .ReturnsAsync(new List<Repository> { goodRepo, badRepo });

        // Good repo: one new commit.
        var goodCommit = MakeGitCommit("dddd4444dddd4444dddd4444dddd4444dddd4444");
        _gitService.Setup(g => g.GetCommitsSinceAsync(goodRepo.Path, It.IsAny<DateTime>()))
                   .ReturnsAsync(new List<GitCommit> { goodCommit });

        // Bad repo: throws.
        _gitService.Setup(g => g.GetCommitsSinceAsync(badRepo.Path, It.IsAny<DateTime>()))
                   .ThrowsAsync(new UnauthorizedAccessException("Access denied"));

        _commitRepo.Setup(c => c.CommitExistsAsync(It.IsAny<string>()))
                   .ReturnsAsync(false);
        _commitRepo.Setup(c => c.AddRangeAsync(It.IsAny<IEnumerable<CommitRecord>>()))
                   .Returns(Task.CompletedTask);
        _repoRepo.Setup(r => r.UpdateAsync(It.IsAny<Repository>()))
                 .Returns(Task.CompletedTask);
        _unitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()))
                   .ReturnsAsync(1);
        _commitRepo.Setup(c => c.GetByRepositoryAndDateRangeAsync(
                       It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<DateTime>()))
                   .ReturnsAsync(new List<CommitRecord>
                   {
                       new() { Id = Guid.NewGuid(), RepositoryId = goodRepo.Id,
                               DateUtc = DateTime.UtcNow, LinesAdded = 5, LinesDeleted = 1 }
                   });
        _dailySummaryRepo.Setup(d => d.UpsertAsync(It.IsAny<DailySummary>()))
                         .Returns(Task.CompletedTask);
        _notifier.Setup(n => n.NotifyScanCompletedAsync(
                     It.IsAny<ScanResultDto>(), It.IsAny<CancellationToken>()))
                 .Returns(Task.CompletedTask);

        var handler = CreateHandler();

        // Act
        var result = await handler.Handle(new ScanRepositoriesCommand(), CancellationToken.None);

        // Assert
        result.Status.Should().Be("PartialFailure",
            "one repo succeeded and one failed");
        result.RepositoriesScanned.Should().Be(2);
        result.NewCommitsFound.Should().Be(1,
            "only the good repo contributed a commit");
    }
}
