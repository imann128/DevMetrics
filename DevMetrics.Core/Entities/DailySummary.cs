namespace DevMetrics.Core.Entities;

/// <summary>
/// Represents a pre-aggregated daily productivity summary for a single <see cref="Repository"/>.
/// Rather than querying thousands of <see cref="CommitRecord"/> rows on every dashboard refresh,
/// the background service materialises these summaries after each scan, allowing the API
/// to serve chart data from a single lightweight table scan.
/// </summary>
public class DailySummary
{
    /// <summary>
    /// Gets or sets the surrogate primary key of this summary record.
    /// Defaults to a new <see cref="Guid"/> on construction.
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Gets or sets the foreign key linking this summary to its parent <see cref="Repository"/>.
    /// </summary>
    public Guid RepositoryId { get; set; }

    /// <summary>
    /// Gets or sets the calendar date this summary covers.
    /// Only the <em>date portion</em> is meaningful — the time component must be
    /// normalised to midnight UTC (<c>date.Date</c>) before persisting to avoid
    /// duplicate rows for the same day.
    /// </summary>
    public DateTime Date { get; set; }

    /// <summary>
    /// Gets or sets the total number of commits authored on <see cref="Date"/>
    /// within the associated repository.
    /// </summary>
    public int TotalCommits { get; set; }

    /// <summary>
    /// Gets or sets the sum of <see cref="CommitRecord.LinesAdded"/> across all commits
    /// on <see cref="Date"/> within the associated repository.
    /// </summary>
    public int TotalLinesAdded { get; set; }

    /// <summary>
    /// Gets or sets the sum of <see cref="CommitRecord.LinesDeleted"/> across all commits
    /// on <see cref="Date"/> within the associated repository.
    /// </summary>
    public int TotalLinesDeleted { get; set; }

    // ── Navigation property ───────────────────────────────────────────────────

    /// <summary>
    /// Gets or sets the parent <see cref="Repository"/> navigation property.
    /// Nullable because EF Core uses lazy or explicit loading — not always populated.
    /// </summary>
    public Repository? Repository { get; set; }
}
