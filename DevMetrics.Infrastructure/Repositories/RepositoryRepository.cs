using DevMetrics.Core.Entities;
using DevMetrics.Core.Interfaces;
using DevMetrics.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DevMetrics.Infrastructure.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IRepositoryRepository"/>.
/// All mutating operations (Add, Update, Delete) stage changes on the
/// <see cref="AppDbContext"/> change tracker without writing to SQLite.
/// The caller must follow up with <see cref="IUnitOfWork.SaveChangesAsync"/>
/// to commit the transaction.
/// </summary>
public sealed class RepositoryRepository : IRepositoryRepository
{
    private readonly AppDbContext _context;
    private readonly ILogger<RepositoryRepository> _logger;

    /// <summary>
    /// Initialises a new instance of <see cref="RepositoryRepository"/>.
    /// </summary>
    /// <param name="context">The scoped EF Core context. Must not be null.</param>
    /// <param name="logger">The structured logger. Must not be null.</param>
    public RepositoryRepository(AppDbContext context, ILogger<RepositoryRepository> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _logger  = logger  ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public async Task<Repository?> GetByIdAsync(Guid id)
    {
        _logger.LogDebug("DB | Repositories.FindAsync Id={Id}", id);

        // FindAsync checks the EF change-tracker cache before hitting SQLite —
        // efficient when the same entity is accessed multiple times in one scope.
        return await _context.Repositories.FindAsync(id);
    }

    /// <inheritdoc/>
    public async Task<Repository?> GetByPathAsync(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, nameof(path));

        _logger.LogDebug("DB | Repositories.FirstOrDefault Path={Path}", path);

        return await _context.Repositories
            .AsNoTracking()  // read-only caller — skip change-tracking overhead
            .FirstOrDefaultAsync(r => r.Path == path);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Repository>> GetAllAsync()
    {
        _logger.LogDebug("DB | Repositories.GetAll");

        var repos = await _context.Repositories
            .AsNoTracking()
            .OrderBy(r => r.Name)
            .ToListAsync();

        // EF Core + SQLite stores DateTime as text and always returns DateTimeKind.Unspecified.
        // Normalise to Utc so downstream consumers (JSON serialiser, GitService) handle it correctly.
        foreach (var r in repos)
            r.LastScannedUtc = DateTime.SpecifyKind(r.LastScannedUtc, DateTimeKind.Utc);

        return repos;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Repository>> GetNeedsScanAsync(DateTime threshold)
    {
        _logger.LogDebug("DB | Repositories.GetNeedsScan threshold={Threshold:u}", threshold);

        var repos = await _context.Repositories
            .AsNoTracking()
            .Where(r => r.LastScannedUtc < threshold)
            .OrderBy(r => r.LastScannedUtc)  // oldest-scanned first
            .ToListAsync();

        // Same Unspecified → Utc normalisation as GetAllAsync.
        // Critical for GitService.GetCommitsSinceAsync: if kind is Unspecified,
        // ToUniversalTime() treats it as local (PKT = UTC+5) and shifts it 5 hours
        // forward, causing the scan to miss the most recent 5 hours of commits.
        foreach (var r in repos)
            r.LastScannedUtc = DateTime.SpecifyKind(r.LastScannedUtc, DateTimeKind.Utc);

        return repos;
    }

    /// <inheritdoc/>
    public async Task AddAsync(Repository repo)
    {
        ArgumentNullException.ThrowIfNull(repo, nameof(repo));

        _logger.LogInformation(
            "DB | Repositories.Add — staging insert Name={Name} Path={Path}",
            repo.Name, repo.Path);

        await _context.Repositories.AddAsync(repo);
        // Caller must call IUnitOfWork.SaveChangesAsync to commit.
    }

    /// <inheritdoc/>
    public Task UpdateAsync(Repository repo)
    {
        ArgumentNullException.ThrowIfNull(repo, nameof(repo));

        _logger.LogDebug(
            "DB | Repositories.Update — staging update Id={Id} LastScannedUtc={Ts:u}",
            repo.Id, repo.LastScannedUtc);

        // EF tracks the entity; calling Update() marks all scalar properties as modified
        // so they are included in the next SaveChanges round-trip.
        _context.Repositories.Update(repo);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(Guid id)
    {
        var repo = await _context.Repositories.FindAsync(id);

        if (repo is null)
        {
            _logger.LogWarning(
                "DB | Repositories.Delete — Id={Id} not found; no-op", id);
            return;
        }

        _logger.LogInformation(
            "DB | Repositories.Delete — staging delete Id={Id} Name={Name}",
            repo.Id, repo.Name);

        // EF's cascade rule (configured in OnModelCreating) will automatically
        // stage dependent CommitRecord and DailySummary rows for deletion.
        _context.Repositories.Remove(repo);
        // Caller must call IUnitOfWork.SaveChangesAsync to commit.
    }
}
