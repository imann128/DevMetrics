namespace DevMetrics.Core.Entities;

/// <summary>
/// Root aggregate representing a local Git repository that is being tracked by DevMetrics.
/// Each repository is identified by its unique file system path and holds
/// navigation collections for its associated commit records and daily summaries.
/// </summary>
public class Repository
{
    /// <summary>
    /// Gets or sets the surrogate primary key of this repository record.
    /// Defaults to a new <see cref="Guid"/> on construction.
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Gets or sets the absolute file system path to the repository root
    /// (the directory that contains the <c>.git</c> folder).
    /// This value is required and must be unique across all tracked repositories.
    /// </summary>
    public required string Path { get; set; }

    /// <summary>
    /// Gets or sets the human-readable display name of the repository.
    /// Typically derived from the directory name or the remote's repository name.
    /// Defaults to an empty string and can be overridden by the user.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the UTC timestamp of the most recent successful scan of this repository.
    /// Used by the background service to determine which repositories are due for rescanning.
    /// Defaults to <see cref="DateTime.UtcNow"/> at the time the entity is instantiated.
    /// </summary>
    public DateTime LastScannedUtc { get; set; } = DateTime.UtcNow;

    // ── Navigation properties (populated by EF Core) ──────────────────────────

    /// <summary>
    /// Gets or sets the collection of individual commit records that belong to this repository.
    /// Populated via EF Core's change tracker when explicitly included in queries.
    /// </summary>
    public ICollection<CommitRecord> CommitRecords { get; set; } = new List<CommitRecord>();

    /// <summary>
    /// Gets or sets the collection of per-day aggregated summaries for this repository.
    /// Populated via EF Core's change tracker when explicitly included in queries.
    /// </summary>
    public ICollection<DailySummary> DailySummaries { get; set; } = new List<DailySummary>();
}
