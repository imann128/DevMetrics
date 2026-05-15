using System.Diagnostics;
using DevMetrics.Application.DTOs;
using DevMetrics.Application.Services;
using DevMetrics.Core.Entities;
using DevMetrics.Core.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace DevMetrics.Application.Commands;

/// <summary>
/// Handles <see cref="ScanRepositoriesCommand"/> by executing the full
/// ingest pipeline for every overdue repository:
/// <list type="number">
///   <item>Query Git for commits since <c>LastScannedUtc</c>.</item>
///   <item>Skip hashes already present in the database (idempotent).</item>
///   <item>Persist new <see cref="CommitRecord"/> rows.</item>
///   <item>Recalculate <see cref="DailySummary"/> for every affected date.</item>
///   <item>Stamp <c>Repository.LastScannedUtc</c> and save.</item>
///   <item>Notify the SignalR hub via <see cref="IScanNotifier"/>.</item>
/// </list>
/// </summary>
public sealed class ScanRepositoriesCommandHandler
    : IRequestHandler<ScanRepositoriesCommand, ScanResultDto>
{
    // ── Scan threshold: repos scanned more than this long ago are included ────
    private static readonly TimeSpan ScanThreshold = TimeSpan.FromHours(1);

    private readonly IRepositoryRepository   _repositoryRepo;
    private readonly ICommitRecordRepository _commitRepo;
    private readonly IDailySummaryRepository _dailySummaryRepo;
    private readonly IGitRepositoryService   _gitService;
    private readonly IUnitOfWork             _unitOfWork;
    private readonly IScanNotifier           _scanNotifier;
    private readonly ILogger<ScanRepositoriesCommandHandler> _logger;

    /// <summary>
    /// Initialises the handler. All parameters are injected by MediatR's
    /// DI-backed pipeline.
    /// </summary>
    public ScanRepositoriesCommandHandler(
        IRepositoryRepository   repositoryRepo,
        ICommitRecordRepository commitRepo,
        IDailySummaryRepository dailySummaryRepo,
        IGitRepositoryService   gitService,
        IUnitOfWork             unitOfWork,
        IScanNotifier           scanNotifier,
        ILogger<ScanRepositoriesCommandHandler> logger)
    {
        _repositoryRepo   = repositoryRepo   ?? throw new ArgumentNullException(nameof(repositoryRepo));
        _commitRepo       = commitRepo       ?? throw new ArgumentNullException(nameof(commitRepo));
        _dailySummaryRepo = dailySummaryRepo ?? throw new ArgumentNullException(nameof(dailySummaryRepo));
        _gitService       = gitService       ?? throw new ArgumentNullException(nameof(gitService));
        _unitOfWork       = unitOfWork       ?? throw new ArgumentNullException(nameof(unitOfWork));
        _scanNotifier     = scanNotifier     ?? throw new ArgumentNullException(nameof(scanNotifier));
        _logger           = logger           ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public async Task<ScanResultDto> Handle(
        ScanRepositoriesCommand request,
        CancellationToken       cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        _logger.LogInformation("Scan | Starting repository scan cycle");

        var threshold  = DateTime.UtcNow - ScanThreshold;
        var repos      = await _repositoryRepo.GetNeedsScanAsync(threshold);
        var totalNew   = 0;
        var failures   = 0;

        foreach (var repo in repos)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var newCount = await ScanSingleRepositoryAsync(repo, cancellationToken);
                totalNew += newCount;

                _logger.LogInformation(
                    "Scan | {Name}: ingested {NewCommits} new commit(s)",
                    repo.Name, newCount);
            }
            catch (OperationCanceledException)
            {
                throw; // propagate shutdown signal immediately
            }
            catch (Exception ex)
            {
                // One bad repo must not abort the entire cycle.
                failures++;
                _logger.LogError(ex,
                    "Scan | {Name} at {Path} failed — skipping",
                    repo.Name, repo.Path);
            }
        }

        sw.Stop();

        var status = failures == 0
            ? "Completed"
            : failures == repos.Count ? "Failed" : "PartialFailure";

        var result = new ScanResultDto(
            RepositoriesScanned: repos.Count,
            NewCommitsFound:     totalNew,
            DurationMs:          sw.ElapsedMilliseconds,
            Status:              status);

        _logger.LogInformation(
            "Scan | Cycle complete — {Repos} repos, {New} new commits, {Duration}ms [{Status}]",
            result.RepositoriesScanned, result.NewCommitsFound,
            result.DurationMs, result.Status);

        // Notify connected SignalR clients (no-op when hub is not registered).
        await _scanNotifier.NotifyScanCompletedAsync(result, cancellationToken);

        return result;
    }

    // ── Private pipeline ──────────────────────────────────────────────────────

    /// <summary>
    /// Runs the full ingest pipeline for a single repository and returns
    /// the number of new commits written.
    /// </summary>
    private async Task<int> ScanSingleRepositoryAsync(
        Core.Entities.Repository repo,
        CancellationToken        ct)
    {
        _logger.LogDebug("Scan | {Name}: reading commits since {Since:u}",
            repo.Name, repo.LastScannedUtc);

        // 1 ── Fetch commits from Git ─────────────────────────────────────────
        var gitCommits = await _gitService.GetCommitsSinceAsync(repo.Path, repo.LastScannedUtc);

        if (gitCommits.Count == 0)
        {
            // Nothing new — still stamp LastScannedUtc so this repo doesn't
            // show up again until the next threshold window.
            await StampAndSaveAsync(repo, ct);
            return 0;
        }

        // 2 ── Filter out already-ingested hashes (idempotent re-runs) ────────
        var newCommitRecords = new List<CommitRecord>(gitCommits.Count);

        foreach (var gc in gitCommits)
        {
            if (await _commitRepo.CommitExistsAsync(gc.Hash))
            {
                _logger.LogDebug("Scan | Skipping duplicate hash {Hash}", gc.Hash[..8]);
                continue;
            }

            newCommitRecords.Add(MapToCommitRecord(gc, repo.Id));
        }

        if (newCommitRecords.Count == 0)
        {
            await StampAndSaveAsync(repo, ct);
            return 0;
        }

        // 3 ── Persist new commit records ─────────────────────────────────────
        await _commitRepo.AddRangeAsync(newCommitRecords);

        // 4 ── Stamp LastScannedUtc on the repo entity (staged) ───────────────
        repo.LastScannedUtc = DateTime.UtcNow;
        await _repositoryRepo.UpdateAsync(repo);

        // Commit commits + repo timestamp in one transaction.
        await _unitOfWork.SaveChangesAsync(ct);

        // 5 ── Recalculate DailySummary for every affected calendar date ───────
        // We recalculate from the DB (not just from newCommitRecords) so that
        // the summary reflects all commits on that day, including those from
        // previous partial scans.
        var affectedDates = newCommitRecords
            .Select(c => c.DateUtc.Date)
            .Distinct();

        foreach (var date in affectedDates)
        {
            await RecalculateDailySummaryAsync(repo.Id, date, ct);
        }

        return newCommitRecords.Count;
    }

    /// <summary>
    /// Recalculates and upserts the <see cref="DailySummary"/> for a specific
    /// repository and calendar date by reading all stored commits for that day.
    /// </summary>
    private async Task RecalculateDailySummaryAsync(
        Guid              repoId,
        DateTime          date,
        CancellationToken ct)
    {
        // Query all commits for the full calendar day (midnight-to-midnight).
        var dayStart = date.Date;
        var dayEnd   = dayStart.AddDays(1).AddTicks(-1);

        var commits = await _commitRepo.GetByRepositoryAndDateRangeAsync(
            repoId, dayStart, dayEnd);

        var summary = new DailySummary
        {
            RepositoryId      = repoId,
            Date              = dayStart,    // normalised to midnight
            TotalCommits      = commits.Count,
            TotalLinesAdded   = commits.Sum(c => c.LinesAdded),
            TotalLinesDeleted = commits.Sum(c => c.LinesDeleted)
        };

        // UpsertAsync commits internally per its interface contract.
        await _dailySummaryRepo.UpsertAsync(summary);

        _logger.LogDebug(
            "Scan | Upserted DailySummary RepoId={RepoId} Date={Date:yyyy-MM-dd} " +
            "Commits={Commits} +{Added} -{Deleted}",
            repoId, date, summary.TotalCommits,
            summary.TotalLinesAdded, summary.TotalLinesDeleted);
    }

    /// <summary>
    /// Stamps <c>LastScannedUtc</c> on a repository that had no new commits
    /// and persists the update, so the scan threshold advances correctly.
    /// </summary>
    private async Task StampAndSaveAsync(Core.Entities.Repository repo, CancellationToken ct)
    {
        repo.LastScannedUtc = DateTime.UtcNow;
        await _repositoryRepo.UpdateAsync(repo);
        await _unitOfWork.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Maps a <see cref="Core.DTOs.GitCommit"/> value object to a persisted
    /// <see cref="CommitRecord"/> entity.
    /// </summary>
    private static CommitRecord MapToCommitRecord(Core.DTOs.GitCommit gc, Guid repoId) =>
        new()
        {
            Id           = Guid.NewGuid(),
            RepositoryId = repoId,
            Hash         = gc.Hash,
            Author       = gc.Author,
            DateUtc      = gc.DateUtc,
            LinesAdded   = gc.LinesAdded,
            LinesDeleted = gc.LinesDeleted,
            FilesChanged = gc.FilesChanged
        };
}
