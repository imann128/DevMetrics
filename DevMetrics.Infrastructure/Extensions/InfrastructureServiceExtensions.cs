using DevMetrics.Core.Interfaces;
using DevMetrics.Infrastructure.Data;
using DevMetrics.Infrastructure.Repositories;
using DevMetrics.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Events;

namespace DevMetrics.Infrastructure.Extensions;

/// <summary>
/// Extension methods that register all Infrastructure-layer services into
/// the ASP.NET Core dependency injection container.
/// Called once from <c>DevMetrics.Api/Program.cs</c> during host bootstrapping.
/// </summary>
public static class InfrastructureServiceExtensions
{
    /// <summary>
    /// The connection string key looked up from <c>appsettings.json</c> /
    /// environment variables. Falls back to a hard-coded SQLite path when absent.
    /// </summary>
    private const string ConnectionStringName = "DefaultConnection";

    /// <summary>
    /// Default SQLite connection string used when no explicit connection string
    /// is configured. The <c>./Data/</c> directory is created automatically by
    /// EF Core if it does not exist.
    /// </summary>
    private const string DefaultConnectionString = "Data Source=./Data/devmetrics.db";

    /// <summary>
    /// Registers all Infrastructure-layer services:
    /// <list type="bullet">
    ///   <item><description><see cref="AppDbContext"/> with SQLite</description></item>
    ///   <item><description>All four repository implementations (scoped)</description></item>
    ///   <item><description><see cref="IGitRepositoryService"/> (scoped)</description></item>
    /// </list>
    /// </summary>
    /// <param name="services">The DI service collection. Must not be null.</param>
    /// <param name="configuration">
    /// The application configuration used to resolve the
    /// <c>ConnectionStrings:DefaultConnection</c> value. Must not be null.
    /// </param>
    /// <returns>The same <paramref name="services"/> instance for fluent chaining.</returns>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services,     nameof(services));
        ArgumentNullException.ThrowIfNull(configuration, nameof(configuration));

        // ── SQLite via EF Core ─────────────────────────────────────────────────
        var connectionString = configuration.GetConnectionString(ConnectionStringName)
                               ?? DefaultConnectionString;

        services.AddDbContext<AppDbContext>(options =>
        {
            options.UseSqlite(connectionString);

            // Enable sensitive data logging only in Development to help debug
            // parameterised queries without risking data leakage in production.
#if DEBUG
            options.EnableSensitiveDataLogging();
            options.EnableDetailedErrors();
#endif
        });

        // ── Repositories (scoped — one instance per HTTP request / background tick) ──
        // Each repository receives the same AppDbContext instance within a scope,
        // ensuring they share the same change-tracker and participate in the
        // same UnitOfWork transaction.
        services.AddScoped<IRepositoryRepository,    RepositoryRepository>();
        services.AddScoped<ICommitRecordRepository,  CommitRecordRepository>();
        services.AddScoped<IDailySummaryRepository,  DailySummaryRepository>();
        services.AddScoped<IUnitOfWork,              UnitOfWork>();

        // ── Git service (scoped — opens and closes LibGit2Sharp handles per scope) ──
        // Scoped rather than Transient to avoid re-opening the repo handle multiple
        // times within a single scan operation.
        services.AddScoped<IGitRepositoryService, GitService>();

        return services;
    }

    /// <summary>
    /// Configures the global <see cref="Log.Logger"/> with structured logging
    /// to both the console (development) and a rolling file sink (all environments).
    /// </summary>
    /// <remarks>
    /// Call this <em>before</em> <c>WebApplication.CreateBuilder(args)</c> so
    /// that startup errors are captured. After <c>builder.Build()</c>,
    /// call <c>builder.Host.UseSerilog()</c> to replace the default
    /// <c>ILoggerFactory</c> with Serilog throughout the DI container.
    /// <para>
    /// Log files are written to <c>./Logs/devmetrics-YYYYMMDD.txt</c>.
    /// Up to 30 daily files are retained before the oldest is deleted.
    /// EF Core internal logs are suppressed below Warning level to keep the
    /// output focused on application events.
    /// </para>
    /// </remarks>
    public static void ConfigureSerilog()
    {
        Log.Logger = new LoggerConfiguration()
            // Global minimum — adjust per sink as needed
            .MinimumLevel.Debug()

            // Suppress chatty EF Core internals (command text, connection open/close)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Infrastructure",   LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Query",            LogEventLevel.Warning)

            // Suppress ASP.NET Core routing noise
            .MinimumLevel.Override("Microsoft.AspNetCore",                           LogEventLevel.Warning)

            // Enrich every event with the source context (class name) and thread ID
            .Enrich.FromLogContext()
            .Enrich.WithThreadId()

            // ── Console sink (human-readable, colourised) ─────────────────────
            .WriteTo.Console(
                restrictedToMinimumLevel: LogEventLevel.Debug,
                outputTemplate:
                    "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext:l}: {Message:lj}" +
                    "{NewLine}{Exception}")

            // ── Rolling file sink (structured for log aggregators) ────────────
            .WriteTo.File(
                path:                    "./Logs/devmetrics-.txt",
                rollingInterval:         RollingInterval.Day,
                retainedFileCountLimit:  30,
                restrictedToMinimumLevel: LogEventLevel.Information,
                outputTemplate:
                    "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] " +
                    "[Thread {ThreadId}] {SourceContext:l}: {Message:lj}" +
                    "{NewLine}{Exception}")

            .CreateLogger();
    }
}
