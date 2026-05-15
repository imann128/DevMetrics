using DevMetrics.Application.BackgroundServices;
using DevMetrics.Application.Services;
using DevMetrics.Infrastructure.Data;
using DevMetrics.Tests.Helpers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Moq;
using Xunit;

namespace DevMetrics.Tests.Integration;

/// <summary>
/// <see cref="WebApplicationFactory{TEntryPoint}"/> specialisation for DevMetrics.
/// Replaces production services with test doubles so integration tests run
/// without external dependencies (SMTP, real Git repos, background workers).
/// </summary>
/// <remarks>
/// Key substitutions:
/// <list type="bullet">
///   <item>
///     <description>
///       <see cref="AppDbContext"/> → SQLite file in a per-factory temp directory.
///       Using a file (not in-memory) because SQLite in-memory doesn't support
///       EF Core migrations, and the integration tests need the full schema.
///     </description>
///   </item>
///   <item>
///     <description>
///       Background services (<see cref="ScanBackgroundService"/>,
///       <see cref="WeeklyReportBackgroundService"/>,
///       <see cref="RepositoryWatcherBackgroundService"/>) are removed so tests
///       are fast and deterministic (no background timer races).
///     </description>
///   </item>
///   <item>
///     <description>
///       <see cref="Core.Interfaces.IGitRepositoryService"/> is replaced by a
///       configurable <see cref="Mock{T}"/> accessible via
///       <see cref="GitServiceMock"/>.
///     </description>
///   </item>
///   <item>
///     <description>
///       <see cref="IEmailService"/> is replaced by <see cref="MockEmailService"/>
///       accessible via <see cref="EmailService"/>.
///     </description>
///   </item>
/// </list>
/// </remarks>
public sealed class CustomWebApplicationFactory
    : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly string _dbPath;
    private readonly string _dbDir;

    /// <summary>Provides access to the mock git service for arranging test scenarios.</summary>
    public Mock<Core.Interfaces.IGitRepositoryService> GitServiceMock { get; } = new();

    /// <summary>Provides access to the captured email sends for assertion.</summary>
    public MockEmailService EmailService { get; } = new();

    /// <summary>
    /// Initialises the factory with a unique per-run SQLite file path.
    /// </summary>
    public CustomWebApplicationFactory()
    {
        _dbDir  = Path.Combine(Path.GetTempPath(), "devmetrics-integration", Guid.NewGuid().ToString("N"));
        _dbPath = Path.Combine(_dbDir, "test.db");
    }

    // ── WebApplicationFactory ─────────────────────────────────────────────────

    /// <inheritdoc/>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Load test-specific appsettings that suppress Serilog noise.
        builder.ConfigureAppConfiguration((ctx, config) =>
        {
            config.AddJsonFile(
                Path.Combine(AppContext.BaseDirectory, "appsettings.Testing.json"),
                optional: true,
                reloadOnChange: false);
        });

        builder.UseEnvironment("Testing");

        builder.ConfigureTestServices(services =>
        {
            // ── Remove production DbContext ───────────────────────────────────
            services.RemoveAll<AppDbContext>();
            services.RemoveAll<DbContextOptions<AppDbContext>>();

            // ── Register test SQLite DbContext ───────────────────────────────
            services.AddDbContext<AppDbContext>(options =>
                options.UseSqlite($"Data Source={_dbPath}"));

            // ── Remove background services (prevent timer races) ──────────────
            services.RemoveAll<IHostedService>();

            // ── Replace git service with a mock ──────────────────────────────
            services.RemoveAll<Core.Interfaces.IGitRepositoryService>();
            services.AddScoped<Core.Interfaces.IGitRepositoryService>(
                _ => GitServiceMock.Object);

            // ── Replace email service with test double ────────────────────────
            services.RemoveAll<IEmailService>();
            services.AddScoped<IEmailService>(_ => EmailService);
        });
    }

    // ── IAsyncLifetime ────────────────────────────────────────────────────────

    /// <summary>Creates the database directory and applies migrations before tests run.</summary>
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dbDir);

        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync();
    }

    /// <summary>Cleans up the temporary database file after tests complete.</summary>
    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();

        try
        {
            if (Directory.Exists(_dbDir))
                Directory.Delete(_dbDir, recursive: true);
        }
        catch { /* best effort */ }
    }

    // ── Seed helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Seeds entities into the test database and returns the <see cref="IServiceScope"/>
    /// for further arrangement. Caller must dispose the scope.
    /// </summary>
    public async Task SeedAsync<T>(IEnumerable<T> entities) where T : class
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Set<T>().AddRange(entities);
        await db.SaveChangesAsync();
    }

    /// <summary>Clears all rows from all tables while preserving the schema.</summary>
    public async Task ResetDatabaseAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // SQLite doesn't support TRUNCATE — delete all rows.
        db.DailySummaries.RemoveRange(db.DailySummaries);
        db.Commits.RemoveRange(db.Commits);
        db.Repositories.RemoveRange(db.Repositories);

        await db.SaveChangesAsync();
    }
}
