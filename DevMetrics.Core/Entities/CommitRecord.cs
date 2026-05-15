namespace DevMetrics.Core.Entities;

/// <summary>
/// Represents a single Git commit captured from a tracked <see cref="Repository"/>.
/// Each record stores the commit's identity (hash, author, timestamp) together with
/// the diff statistics that were computed at ingestion time.
/// </summary>
public class CommitRecord
{
    /// <summary>
    /// Gets or sets the surrogate primary key of this commit record.
    /// Defaults to a new <see cref="Guid"/> on construction.
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Gets or sets the foreign key linking this record to its parent <see cref="Repository"/>.
    /// </summary>
    public Guid RepositoryId { get; set; }

    /// <summary>
    /// Gets or sets the 40-character hexadecimal SHA-1 hash that uniquely identifies
    /// this commit within the Git object graph.
    /// Used for idempotent ingestion via <c>ICommitRecordRepository.CommitExistsAsync</c>.
    /// </summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the author name as recorded in the Git commit metadata
    /// (i.e., the <c>author.name</c> field, not the committer).
    /// </summary>
    public string Author { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the UTC timestamp when this commit was authored.
    /// LibGit2Sharp returns <c>DateTimeOffset</c> values; convert to UTC before storing.
    /// Defaults to <see cref="DateTime.UtcNow"/> on construction.
    /// </summary>
    public DateTime DateUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Gets or sets the total number of lines inserted across all files changed in this commit.
    /// Computed by summing <c>Patch.LinesAdded</c> from LibGit2Sharp's diff output.
    /// </summary>
    public int LinesAdded { get; set; }

    /// <summary>
    /// Gets or sets the total number of lines removed across all files changed in this commit.
    /// Computed by summing <c>Patch.LinesDeleted</c> from LibGit2Sharp's diff output.
    /// </summary>
    public int LinesDeleted { get; set; }

    /// <summary>
    /// Gets or sets the number of distinct files that were added, modified, renamed,
    /// or deleted in this commit.
    /// </summary>
    public int FilesChanged { get; set; }

    // ── Navigation property ───────────────────────────────────────────────────

    /// <summary>
    /// Gets or sets the parent <see cref="Repository"/> navigation property.
    /// Nullable because EF Core uses lazy or explicit loading — not always populated.
    /// </summary>
    public Repository? Repository { get; set; }
}
