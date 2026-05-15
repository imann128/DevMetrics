namespace DevMetrics.Core.DTOs;

/// <summary>
/// An immutable representation of a single Git commit as read from the repository
/// by <see cref="Interfaces.IGitRepositoryService"/>.
/// This record is the transfer object between the Infrastructure Git layer and the
/// Application ingestion use case — it is never persisted directly.
/// The Application layer maps it to a <see cref="Entities.CommitRecord"/> entity
/// before writing to the database.
/// </summary>
/// <param name="Hash">
/// The 40-character lowercase hexadecimal SHA-1 hash that uniquely identifies
/// this commit in the Git object store.
/// Used as the natural deduplication key via
/// <see cref="Interfaces.ICommitRecordRepository.CommitExistsAsync"/>.
/// </param>
/// <param name="Author">
/// The author name exactly as recorded in the commit's <c>author</c> field
/// (not the committer). For example: <c>"Ada Lovelace"</c>.
/// </param>
/// <param name="DateUtc">
/// The UTC timestamp when the commit was authored.
/// LibGit2Sharp exposes author dates as <see cref="DateTimeOffset"/>;
/// the Infrastructure implementation must convert to UTC before populating this field.
/// </param>
/// <param name="LinesAdded">
/// The total number of lines inserted across all files changed in this commit.
/// Computed by summing <c>ContentChanges.LinesAdded</c> over every patch entry
/// in the commit's diff against its first parent.
/// </param>
/// <param name="LinesDeleted">
/// The total number of lines removed across all files changed in this commit.
/// Computed by summing <c>ContentChanges.LinesDeleted</c> over every patch entry.
/// </param>
/// <param name="FilesChanged">
/// The number of distinct files that were added, modified, renamed, or deleted
/// in this commit. Corresponds to the count of entries in the commit's patch diff.
/// </param>
public record GitCommit(
    string Hash,
    string Author,
    DateTime DateUtc,
    int LinesAdded,
    int LinesDeleted,
    int FilesChanged
);
