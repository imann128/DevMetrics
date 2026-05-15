using DevMetrics.Core.Interfaces;
using DevMetrics.Infrastructure.Data;
using Microsoft.Extensions.Logging;

namespace DevMetrics.Infrastructure.Repositories;

/// <summary>
/// Implementation of <see cref="IUnitOfWork"/> that delegates to
/// <see cref="AppDbContext.SaveChangesAsync(CancellationToken)"/>.
/// </summary>
/// <remarks>
/// Because all repositories in this project share the same scoped
/// <see cref="AppDbContext"/> instance (injected by the DI container),
/// calling <see cref="SaveChangesAsync"/> here commits every pending
/// change across all repositories in one atomic SQLite transaction.
/// </remarks>
public sealed class UnitOfWork : IUnitOfWork
{
    private readonly AppDbContext _context;
    private readonly ILogger<UnitOfWork> _logger;

    /// <summary>
    /// Initialises a new instance of <see cref="UnitOfWork"/>.
    /// </summary>
    /// <param name="context">The scoped EF Core context. Must not be null.</param>
    /// <param name="logger">The structured logger. Must not be null.</param>
    public UnitOfWork(AppDbContext context, ILogger<UnitOfWork> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _logger  = logger  ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Logs both the attempt and the final row count. When <paramref name="ct"/>
    /// is cancelled before the write completes, EF Core throws
    /// <see cref="OperationCanceledException"/> and no data is committed.
    /// </remarks>
    public async Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        _logger.LogDebug("UoW | Flushing EF Core change tracker to SQLite");

        var written = await _context.SaveChangesAsync(ct);

        if (written > 0)
        {
            _logger.LogInformation(
                "UoW | Committed {Count} state {Entries} to SQLite",
                written,
                written == 1 ? "entry" : "entries");
        }
        else
        {
            _logger.LogDebug("UoW | SaveChanges returned 0 — change tracker had no pending mutations");
        }

        return written;
    }
}
