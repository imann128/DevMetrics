using DevMetrics.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Respawn;
using Xunit;

namespace DevMetrics.Tests.Helpers;

/// <summary>
/// xUnit collection fixture that creates a single SQLite database file for a
/// test collection and uses Respawn to reset it to a clean state between tests.
/// </summary>
/// <remarks>
/// <para>
/// Registered as a collection fixture so one database is shared across all
/// tests in the collection — migrations run once, then Respawn wipes data
/// before each test.
/// </para>
/// <para>
/// Usage — apply <c>[Collection(nameof(DatabaseCollection))]</c> to your test
/// class and inject <see cref="DatabaseFixture"/> via the constructor:
/// <code>
/// [Collection(nameof(DatabaseCollection))]
/// public class MyTests(DatabaseFixture db)
/// {
///     [Fact]
///     public async Task Test1()
///     {
///         await db.ResetAsync();
///         using var ctx = db.CreateContext();
///         // ... arrange, act, assert
///     }
/// }
/// </code>
/// </para>
/// </remarks>
public sealed class DatabaseFixture : IAsyncLifetime
{
    private const string DbFileName = "./TestData/fixture-devmetrics.db";

    private Respawner?   _respawner;
    private SqliteConnection? _connection;

    /// <summary>EF Core connection string pointing to the fixture database.</summary>
    public string ConnectionString => $"Data Source={DbFileName}";

    // ── IAsyncLifetime ────────────────────────────────────────────────────────

    /// <summary>
    /// Creates the database directory, runs migrations, and initialises Respawn.
    /// Called once before the first test in the collection.
    /// </summary>
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DbFileName)!);

        // Apply migrations once to set up the schema.
        using var ctx = CreateContext();
        await ctx.Database.MigrateAsync();

        // Open a persistent connection for Respawn — Respawn needs the schema
        // to enumerate tables. SQLite Respawn support deletes all rows via
        // DELETE FROM rather than TRUNCATE (which SQLite doesn't support).
        _connection = new SqliteConnection(ConnectionString);
        await _connection.OpenAsync();

        _respawner = await Respawner.CreateAsync(_connection, new RespawnerOptions
        {
            // Respawn's SQLite support (v6+) uses DbAdapter auto-detection.
            // Explicitly specify tables to avoid touching the EF migrations history table.
            TablesToIgnore = ["__EFMigrationsHistory"]
        });
    }

    /// <summary>Closes the SQLite connection on test collection teardown.</summary>
    public async Task DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.CloseAsync();
            await _connection.DisposeAsync();
        }

        // Optionally delete the test DB file after the run (comment out to inspect data)
        if (File.Exists(DbFileName))
            File.Delete(DbFileName);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Resets all data in the database to empty (preserves schema).
    /// Call at the start of each test to ensure isolation.
    /// </summary>
    public async Task ResetAsync()
    {
        if (_respawner is null || _connection is null)
            throw new InvalidOperationException(
                "DatabaseFixture is not initialised. Did you call InitializeAsync?");

        await _respawner.ResetAsync(_connection);
    }

    /// <summary>
    /// Creates a new <see cref="AppDbContext"/> instance sharing the fixture's
    /// connection string. The caller is responsible for disposing it.
    /// </summary>
    public AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(ConnectionString)
            .Options;

        return new AppDbContext(options);
    }

    /// <summary>
    /// Seeds a set of entities into the fixture database.
    /// Useful in test <c>Arrange</c> blocks.
    /// </summary>
    public async Task SeedAsync<T>(IEnumerable<T> entities) where T : class
    {
        await using var ctx = CreateContext();
        ctx.Set<T>().AddRange(entities);
        await ctx.SaveChangesAsync();
    }
}

/// <summary>xUnit collection definition — binds tests to the <see cref="DatabaseFixture"/>.</summary>
[CollectionDefinition(nameof(DatabaseCollection))]
public class DatabaseCollection : ICollectionFixture<DatabaseFixture> { }
