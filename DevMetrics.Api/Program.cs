using DevMetrics.Api.HealthChecks;
using DevMetrics.Api.Hubs;
using DevMetrics.Api.Middleware;
using DevMetrics.Api.Services;
using DevMetrics.Application.Extensions;
using DevMetrics.Application.Services;
using DevMetrics.Infrastructure.Data;
using DevMetrics.Infrastructure.Extensions;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Serilog;
using System.Text.Json;

// ── Step 1: Bootstrap Serilog before the host so startup errors are captured ──
InfrastructureServiceExtensions.ConfigureSerilog();

try
{
    Log.Information("DevMetrics | Starting host");

    var builder = WebApplication.CreateBuilder(args);

    // ── Step 2: Serilog as the sole ILogger provider ──────────────────────────
    // ReadFrom.Configuration merges the "Serilog" section from appsettings.json.
    // ReadFrom.Services wires in any services that implement ILogEventSink (none
    // currently, but keeps the door open for Seq, OpenTelemetry, etc.).
    // Enrich.WithMachineName / WithEnvironmentName are container-friendly extras.
    builder.Host.UseSerilog((ctx, services, config) =>
        config
            .ReadFrom.Configuration(ctx.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .Enrich.WithMachineName()
            .Enrich.WithEnvironmentName());

    // ── Step 3: Kestrel (dev) / ASPNETCORE_URLS (container) ──────────────────
    // In Docker, ASPNETCORE_URLS=http://+:80 is set via docker-compose.yml so
    // Kestrel binds to all interfaces on port 80 without changing code.
    // The localhost fallback is for local `dotnet run` sessions only.
    if (builder.Environment.IsDevelopment())
    {
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenLocalhost(5000);
            options.ListenLocalhost(5001, o => o.UseHttps());
        });
    }

    var configuration = builder.Configuration;
    var services      = builder.Services;

    // ── Step 4: Layer registrations ───────────────────────────────────────────
    services.AddInfrastructure(configuration);
    services.AddApplication(configuration);

    // Override NullScanNotifier with the real SignalR implementation.
    services.AddScoped<IScanNotifier, SignalRScanNotifier>();

    // ── Step 5: ASP.NET Core fundamentals ────────────────────────────────────
    services.AddControllers()
            .AddJsonOptions(opts =>
            {
                // Consistent camelCase serialisation across all API endpoints.
                opts.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            });

    services.AddRazorPages();

    services.AddHttpClient("DevMetricsAPI", client =>
    {
        client.BaseAddress = new Uri("http://localhost:5000/");
        client.DefaultRequestHeaders.Add("Accept", "application/json");
    });

    services.AddSignalR(options =>
    {
        options.EnableDetailedErrors        = builder.Environment.IsDevelopment();
        options.MaximumReceiveMessageSize   = 1_024 * 1_024; // 1 MB
    });

    services.AddMemoryCache();

    // ── Step 6: CORS ──────────────────────────────────────────────────────────
    const string CorsPolicyName = "DevMetricsCors";

    services.AddCors(options =>
    {
        options.AddPolicy(CorsPolicyName, policy =>
        {
            // In production override this via CORS__AllowedOrigins env var or
            // appsettings.Production.json — do not hard-code prod origins here.
            policy
                .WithOrigins(
                    "http://localhost:3000",
                    "http://localhost:4200",
                    "http://localhost:5173",
                    "https://localhost:3000",
                    "https://localhost:4200",
                    "http://localhost:5000",
                    "https://localhost:5001")
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials(); // Required for SignalR WebSocket from browser
        });
    });

    // ── Step 7: OpenAPI / Swagger (dev + staging only) ────────────────────────
    services.AddEndpointsApiExplorer();
    services.AddSwaggerGen(options =>
    {
        options.SwaggerDoc("v1", new()
        {
            Title       = "DevMetrics API",
            Version     = "v1",
            Description = "Developer productivity dashboard — Git activity tracking API."
        });

        foreach (var xmlFile in Directory.GetFiles(AppContext.BaseDirectory, "DevMetrics.*.xml"))
            options.IncludeXmlComments(xmlFile, includeControllerXmlComments: true);
    });

    // ── Step 8: Health checks ─────────────────────────────────────────────────
    services.AddHealthChecks()
        // "live" tag  → liveness probe  (/health/live):  is the process alive?
        // "ready" tag → readiness probe (/health/ready): can it serve requests?
        .AddCheck<DatabaseHealthCheck>(
            "database",
            failureStatus: HealthStatus.Unhealthy,
            tags: ["ready", "database"])
        .AddCheck<GitServiceHealthCheck>(
            "git-library",
            failureStatus: HealthStatus.Unhealthy,
            tags: ["live", "git"])
        .AddCheck<BackgroundServiceHealthCheck>(
            "background-scan",
            failureStatus: HealthStatus.Degraded,
            tags: ["ready", "background"]);

    // ── Step 9: Build the application ─────────────────────────────────────────
    var app = builder.Build();

    // ── Step 10: Auto-migrate on startup ──────────────────────────────────────
    await ApplyMigrationsAsync(app);

    // ── Step 11: Middleware pipeline ──────────────────────────────────────────
    // ErrorHandlingMiddleware must be outermost — it catches all unhandled exceptions.
    app.UseMiddleware<ErrorHandlingMiddleware>();

    // Swagger — disabled in Production via appsettings.Production.json
    if (app.Environment.IsDevelopment() || app.Environment.IsStaging())
    {
        app.UseSwagger();
        app.UseSwaggerUI(options =>
        {
            options.SwaggerEndpoint("/swagger/v1/swagger.json", "DevMetrics v1");
            options.RoutePrefix = "swagger";
        });
    }

    // HTTPS redirection — enabled in Production, skipped in Development
    // (developer certificate is not always trusted).
    if (app.Environment.IsProduction())
        app.UseHttpsRedirection();

    app.UseStaticFiles();
    app.UseRouting();
    app.UseCors(CorsPolicyName);

    app.UseSerilogRequestLogging(opts =>
    {
        opts.MessageTemplate =
            "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000}ms";
        // Suppress health-check endpoint noise from request logs.
        opts.GetLevel = (ctx, elapsed, ex) =>
            ctx.Request.Path.StartsWithSegments("/health")
                ? Serilog.Events.LogEventLevel.Verbose
                : Serilog.Events.LogEventLevel.Information;
    });

    app.UseAuthorization();

    // ── Step 12: Endpoint mapping ─────────────────────────────────────────────
    app.MapControllers();
    app.MapRazorPages();
    app.MapHub<DashboardHub>("/dashboardHub").RequireCors(CorsPolicyName);

    // Health endpoints:
    //   /health        — combined (all checks)
    //   /health/live   — liveness  (tagged "live")
    //   /health/ready  — readiness (tagged "ready")
    app.MapHealthChecks("/health", new HealthCheckOptions
    {
        ResponseWriter = WriteHealthResponse
    });

    app.MapHealthChecks("/health/live", new HealthCheckOptions
    {
        Predicate      = check => check.Tags.Contains("live"),
        ResponseWriter = WriteHealthResponse
    });

    app.MapHealthChecks("/health/ready", new HealthCheckOptions
    {
        Predicate      = check => check.Tags.Contains("ready"),
        ResponseWriter = WriteHealthResponse
    });

    Log.Information("DevMetrics | Host ready — http://localhost:5000/swagger");

    await app.RunAsync();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "DevMetrics | Host terminated unexpectedly");
}
finally
{
    await Log.CloseAndFlushAsync();
}

// ── Helpers ───────────────────────────────────────────────────────────────────

static async Task ApplyMigrationsAsync(WebApplication app)
{
    try
    {
        Directory.CreateDirectory("./Data");

        await using var scope = app.Services.CreateAsyncScope();
        var db      = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();

        if (pending.Count > 0)
        {
            Log.Information(
                "DevMetrics | Applying {Count} migration(s): {Names}",
                pending.Count, string.Join(", ", pending));

            await db.Database.MigrateAsync();
            Log.Information("DevMetrics | Migrations applied successfully");
        }
        else
        {
            Log.Debug("DevMetrics | Database schema is up to date");
        }
    }
    catch (Exception ex)
    {
        Log.Error(ex, "DevMetrics | Migration failed — aborting startup");
        throw;
    }
}

/// <summary>
/// Writes health check results as structured JSON instead of the default plain-text
/// "Healthy" / "Unhealthy" response. This makes the endpoint consumable by monitoring
/// tools (Prometheus, Datadog, Uptime Kuma, etc.) without custom parsers.
/// </summary>
static Task WriteHealthResponse(HttpContext ctx, HealthReport report)
{
    ctx.Response.ContentType = "application/json; charset=utf-8";

    var result = new
    {
        status  = report.Status.ToString(),
        duration = report.TotalDuration.TotalMilliseconds,
        checks  = report.Entries.Select(e => new
        {
            name        = e.Key,
            status      = e.Value.Status.ToString(),
            description = e.Value.Description,
            duration    = e.Value.Duration.TotalMilliseconds,
            data        = e.Value.Data.Count > 0 ? e.Value.Data : null,
            error       = e.Value.Exception?.Message
        })
    };

    return ctx.Response.WriteAsync(
        JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            PropertyNamingPolicy    = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition  =
                System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        }));
}

// Exposes the implicit Program class to WebApplicationFactory<Program> in tests.
public partial class Program { }
