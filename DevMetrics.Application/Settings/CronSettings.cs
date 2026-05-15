namespace DevMetrics.Application.Settings;

/// <summary>
/// Strongly-typed cron expressions for recurring background services.
/// Bound from the <c>CronExpressions</c> section of <c>appsettings.json</c>.
/// All expressions follow the five-field POSIX cron format supported by Cronos:
/// <c>minute hour day-of-month month day-of-week</c>.
/// </summary>
/// <example>
/// <code lang="json">
/// {
///   "CronExpressions": {
///     "HourlyScan":   "0 * * * *",
///     "WeeklyReport": "0 9 * * 1"
///   }
/// }
/// </code>
/// </example>
public sealed class CronSettings
{
    /// <summary>Configuration section key for <c>IConfiguration.GetSection</c>.</summary>
    public const string SectionName = "CronExpressions";

    /// <summary>
    /// Cron expression for the hourly repository scan.
    /// Default: <c>"0 * * * *"</c> — fires at minute 0 of every hour.
    /// </summary>
    public string HourlyScan { get; set; } = "0 * * * *";

    /// <summary>
    /// Cron expression for the weekly productivity email report.
    /// Default: <c>"0 9 * * 1"</c> — Monday at 09:00 UTC.
    /// </summary>
    public string WeeklyReport { get; set; } = "0 9 * * 1";
}
