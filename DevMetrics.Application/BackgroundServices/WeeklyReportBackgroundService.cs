using Cronos;
using DevMetrics.Application.Queries;
using DevMetrics.Application.Services;
using DevMetrics.Application.Settings;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DevMetrics.Application.BackgroundServices;

/// <summary>
/// Sends the weekly productivity summary email on a cron schedule
/// (default: Monday 09:00 UTC, expression <c>"0 9 * * 1"</c>).
/// </summary>
/// <remarks>
/// <b>Scheduling:</b> Uses Cronos for DST-safe next-occurrence calculation.
/// The service sleeps exactly until the next cron window, recalculating
/// after every send to avoid drift.
/// <br/>
/// <b>Retry:</b> On send failure the service waits 1 hour and retries
/// up to 3 times before giving up until the next scheduled occurrence.
/// <br/>
/// <b>Recipients:</b> Loaded from <c>Email:Recipients</c> each time the
/// report runs, so a config reload (live or restart) takes effect immediately.
/// </remarks>
public sealed class WeeklyReportBackgroundService : BackgroundService
{
    private const int MaxSendRetries   = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory                   _scopeFactory;
    private readonly IOptions<CronSettings>                 _cronOptions;
    private readonly IHostApplicationLifetime               _lifetime;
    private readonly ILogger<WeeklyReportBackgroundService> _logger;

    /// <inheritdoc cref="WeeklyReportBackgroundService"/>
    public WeeklyReportBackgroundService(
        IServiceScopeFactory                   scopeFactory,
        IOptions<CronSettings>                 cronOptions,
        IHostApplicationLifetime               lifetime,
        ILogger<WeeklyReportBackgroundService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _cronOptions  = cronOptions  ?? throw new ArgumentNullException(nameof(cronOptions));
        _lifetime     = lifetime     ?? throw new ArgumentNullException(nameof(lifetime));
        _logger       = logger       ?? throw new ArgumentNullException(nameof(logger));
    }

    // ── BackgroundService ─────────────────────────────────────────────────────

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "WeeklyReport | Started — cron: '{Cron}'",
            _cronOptions.Value.WeeklyReport);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var delay = CalculateDelayToNextOccurrence();
                if (!delay.HasValue)
                {
                    _logger.LogError(
                        "WeeklyReport | Cron expression '{Cron}' has no future occurrence. " +
                        "Service stopping.", _cronOptions.Value.WeeklyReport);
                    break;
                }

                _logger.LogInformation(
                    "WeeklyReport | Next report in {D:d\\.hh\\:mm\\:ss} at {At:u}",
                    delay.Value, DateTime.UtcNow.Add(delay.Value));

                await Task.Delay(delay.Value, stoppingToken);

                if (stoppingToken.IsCancellationRequested) break;

                await RunWithRetryAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected on host shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex,
                "WeeklyReport | Fatal error — signalling host stop");
            _lifetime.StopApplication();
        }

        _logger.LogInformation("WeeklyReport | Stopped.");
    }

    // ── Retry wrapper ─────────────────────────────────────────────────────────

    private async Task RunWithRetryAsync(CancellationToken ct)
    {
        for (var attempt = 1; attempt <= MaxSendRetries; attempt++)
        {
            try
            {
                await SendReportAsync(attempt, ct);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "WeeklyReport | Send attempt {A}/{Max} failed", attempt, MaxSendRetries);

                if (attempt < MaxSendRetries)
                {
                    _logger.LogInformation(
                        "WeeklyReport | Retrying in {Delay:g}…", RetryDelay);
                    await Task.Delay(RetryDelay, ct);
                }
                else
                {
                    _logger.LogError(
                        "WeeklyReport | All {Max} attempts exhausted — " +
                        "report will retry at the next scheduled occurrence.",
                        MaxSendRetries);
                }
            }
        }
    }

    // ── Core send ─────────────────────────────────────────────────────────────

    private async Task SendReportAsync(int attempt, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var sp            = scope.ServiceProvider;
        var mediator      = sp.GetRequiredService<IMediator>();
        var emailService  = sp.GetRequiredService<IEmailService>();
        var emailSettings = sp.GetRequiredService<IOptions<EmailSettings>>().Value;

        if (!emailSettings.Enabled)
        {
            _logger.LogInformation(
                "WeeklyReport | Email disabled in config — skipping send");
            return;
        }

        var recipients = emailSettings.WeeklyReportRecipients;

        if (recipients.Length == 0)
        {
            _logger.LogWarning(
                "WeeklyReport | No recipients configured — skipping send");
            return;
        }

        _logger.LogInformation(
            "WeeklyReport | Attempt {A}/{Max} — fetching 7-day data for {Count} recipient(s)",
            attempt, MaxSendRetries, recipients.Length);

        var dashboardData = await mediator.Send(
            new GetDashboardDataQuery(Days: 7), ct);

        var subject = BuildSubject();

        _logger.LogInformation(
            "WeeklyReport | Sending '{Subject}' to: {Recipients}",
            subject, string.Join(", ", recipients));

        await emailService.SendWeeklySummaryAsync(recipients, dashboardData, ct);

        _logger.LogInformation(
            "WeeklyReport | Delivered successfully. Commits this week: {Commits}",
            dashboardData.DailySummaries.Sum(d => d.TotalCommits));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string BuildSubject()
    {
        var weekEnd   = DateTime.UtcNow;
        var weekStart = weekEnd.AddDays(-7);
        return $"DevMetrics Weekly Report — {weekStart:MMM d}–{weekEnd:MMM d, yyyy}";
    }

    /// <summary>
    /// Parses the configured cron expression and returns the delay to the
    /// next scheduled occurrence in UTC. Returns <c>null</c> when the
    /// expression has no future occurrences (should not happen with valid cron).
    /// </summary>
    private TimeSpan? CalculateDelayToNextOccurrence()
    {
        try
        {
            // Cronos standard format: minute hour day month weekday
            var expr = CronExpression.Parse(
                _cronOptions.Value.WeeklyReport,
                CronFormat.Standard);

            // TimeZoneInfo.Utc makes the calculation DST-safe — the occurrence
            // is always expressed in UTC regardless of the host's local timezone.
            var next = expr.GetNextOccurrence(DateTimeOffset.UtcNow, TimeZoneInfo.Utc);

            if (!next.HasValue) return null;

            var delay = next.Value.UtcDateTime - DateTime.UtcNow;
            return delay > TimeSpan.Zero ? delay : TimeSpan.FromMinutes(1);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "WeeklyReport | Invalid cron '{Cron}' — defaulting to 7-day interval",
                _cronOptions.Value.WeeklyReport);

            return TimeSpan.FromDays(7);
        }
    }
}
