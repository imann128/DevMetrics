using System.Reflection;
using System.Text;
using DevMetrics.Application.DTOs;
using DevMetrics.Application.Settings;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace DevMetrics.Application.Services;

/// <summary>
/// MailKit-based <see cref="IEmailService"/> that loads the weekly summary
/// HTML from a template, substitutes placeholders, and delivers via SMTP.
/// </summary>
/// <remarks>
/// <b>Template resolution order:</b>
/// <list type="number">
///   <item>File path from <c>Email:WeeklySummaryTemplatePath</c> (if set and the file exists).</item>
///   <item>Embedded resource <c>DevMetrics.Application.Templates.WeeklySummary.html</c>.</item>
///   <item>Inline fallback string (ensures the email always renders something).</item>
/// </list>
/// A new <see cref="SmtpClient"/> is created per send call — MailKit clients
/// are not thread-safe, and long-lived connections time out silently.
/// </remarks>
public sealed class EmailService : IEmailService
{
    // ── Embedded resource name — must match the file path and assembly name exactly.
    private const string EmbeddedResourceName =
        "DevMetrics.Application.Templates.WeeklySummary.html";

    private readonly EmailSettings             _settings;
    private readonly ILogger<EmailService>     _logger;

    // Cached template string — loaded once and reused for all sends.
    private string? _cachedTemplate;

    /// <inheritdoc cref="EmailService"/>
    public EmailService(IOptions<EmailSettings> options, ILogger<EmailService> logger)
    {
        _settings = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger   = logger         ?? throw new ArgumentNullException(nameof(logger));
    }

    // ── IEmailService ─────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task SendWeeklySummaryAsync(
        string[]         recipients,
        DashboardDataDto data,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(recipients, nameof(recipients));
        ArgumentNullException.ThrowIfNull(data, nameof(data));

        if (recipients.Length == 0)
            throw new ArgumentException(
                "At least one recipient is required.", nameof(recipients));

        if (!_settings.Enabled)
        {
            _logger.LogInformation(
                "Email | Sending disabled in configuration — weekly summary suppressed");
            return;
        }

        var template = await LoadTemplateAsync();
        var html     = ApplyTemplate(template, data);
        var plain    = BuildPlainText(data);
        var subject  = BuildSubject();

        var message = BuildMessage(recipients, subject, html, plain);

        _logger.LogInformation(
            "Email | Sending '{Subject}' to {Count} recipient(s): {Recipients}",
            subject, recipients.Length, string.Join(", ", recipients));

        await SendMessageAsync(message, ct);

        _logger.LogInformation(
            "Email | Delivered — MessageId={MsgId}", message.MessageId);
    }

    // ── Template loading ──────────────────────────────────────────────────────

    private async Task<string> LoadTemplateAsync()
    {
        if (_cachedTemplate is not null)
            return _cachedTemplate;

        // 1. File system override
        if (!string.IsNullOrWhiteSpace(_settings.WeeklySummaryTemplatePath)
            && File.Exists(_settings.WeeklySummaryTemplatePath))
        {
            _logger.LogDebug(
                "Email | Loading template from file: {Path}",
                _settings.WeeklySummaryTemplatePath);

            _cachedTemplate = await File.ReadAllTextAsync(
                _settings.WeeklySummaryTemplatePath);
            return _cachedTemplate;
        }

        // 2. Embedded resource
        var assembly    = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(EmbeddedResourceName);

        if (stream is not null)
        {
            _logger.LogDebug(
                "Email | Loading template from embedded resource: {Name}",
                EmbeddedResourceName);

            using var reader = new StreamReader(stream, Encoding.UTF8);
            _cachedTemplate  = await reader.ReadToEndAsync();
            return _cachedTemplate;
        }

        // 3. Inline fallback — ensures the email is always renderable
        _logger.LogWarning(
            "Email | Template not found at '{Path}' and embedded resource '{Resource}' " +
            "is missing. Using inline fallback.",
            _settings.WeeklySummaryTemplatePath, EmbeddedResourceName);

        _cachedTemplate = InlineFallbackTemplate();
        return _cachedTemplate;
    }

    // ── Template substitution ─────────────────────────────────────────────────

    private string ApplyTemplate(string template, DashboardDataDto data)
    {
        var weekEnd   = DateTime.UtcNow;
        var weekStart = weekEnd.AddDays(-7);

        var totalCommits = data.DailySummaries.Sum(d => d.TotalCommits);
        var totalAdded   = data.DailySummaries.Sum(d => d.LinesAdded);
        var totalDeleted = data.DailySummaries.Sum(d => d.LinesDeleted);

        return template
            .Replace("{{DATE_RANGE}}",
                $"{weekStart:MMMM d} – {weekEnd:MMMM d, yyyy}")
            .Replace("{{TOTAL_COMMITS}}",  totalCommits.ToString("N0"))
            .Replace("{{TOTAL_ADDED}}",    totalAdded.ToString("N0"))
            .Replace("{{TOTAL_DELETED}}",  totalDeleted.ToString("N0"))
            .Replace("{{DAILY_TABLE}}",    BuildDailyTableHtml(data))
            .Replace("{{REPO_TAGS}}",      BuildRepoTagsHtml(data))
            .Replace("{{DASHBOARD_URL}}",  _settings.DashboardBaseUrl);
    }

    private static string BuildDailyTableHtml(DashboardDataDto data)
    {
        if (data.DailySummaries.Count == 0)
            return """
                <div class="empty-state">
                  <p>No commits were recorded in the past 7 days.<br>
                  Add a repository and trigger a scan to start tracking.</p>
                </div>
                """;

        var sb = new StringBuilder();
        sb.Append("""
            <table class="data-table" role="presentation">
              <thead>
                <tr>
                  <th>Date</th>
                  <th>Commits</th>
                  <th class="d-hide">Lines Added</th>
                  <th class="d-hide">Lines Deleted</th>
                  <th>Net Change</th>
                </tr>
              </thead>
              <tbody>
            """);

        foreach (var day in data.DailySummaries)
        {
            var net     = day.LinesAdded - day.LinesDeleted;
            var netCss  = net >= 0 ? "added" : "deleted";
            var netSign = net >= 0 ? "+" : "";

            sb.Append($"""
                  <tr>
                    <td>{day.Date:ddd, MMM d}</td>
                    <td>{day.TotalCommits:N0}</td>
                    <td class="d-hide added">+{day.LinesAdded:N0}</td>
                    <td class="d-hide deleted">-{day.LinesDeleted:N0}</td>
                    <td class="{netCss}">{netSign}{net:N0}</td>
                  </tr>
                """);
        }

        var totalCommits = data.DailySummaries.Sum(d => d.TotalCommits);
        var totalAdded   = data.DailySummaries.Sum(d => d.LinesAdded);
        var totalDeleted = data.DailySummaries.Sum(d => d.LinesDeleted);
        var totalNet     = totalAdded - totalDeleted;
        var totalNetSign = totalNet >= 0 ? "+" : "";
        var totalNetCss  = totalNet >= 0 ? "added" : "deleted";

        sb.Append($"""
              </tbody>
              <tfoot>
                <tr>
                  <td>Total</td>
                  <td>{totalCommits:N0}</td>
                  <td class="d-hide added">+{totalAdded:N0}</td>
                  <td class="d-hide deleted">-{totalDeleted:N0}</td>
                  <td class="{totalNetCss}">{totalNetSign}{totalNet:N0}</td>
                </tr>
              </tfoot>
            </table>
            """);

        return sb.ToString();
    }

    private static string BuildRepoTagsHtml(DashboardDataDto data)
    {
        if (data.Repositories.Count == 0)
            return "<span style=\"color:#9ca3af\">No repositories tracked</span>";

        return string.Join("",
            data.Repositories.Select(r =>
                $"<span class=\"repo-tag\">{HtmlEncode(r.Name)}</span>"));
    }

    // ── MIME message builder ──────────────────────────────────────────────────

    private MimeMessage BuildMessage(
        string[] recipients,
        string   subject,
        string   html,
        string   plain)
    {
        var message = new MimeMessage();

        message.From.Add(new MailboxAddress(_settings.FromName, _settings.FromAddress));

        foreach (var r in recipients)
            message.To.Add(MailboxAddress.Parse(r));

        message.Subject = subject;

        message.Body = new BodyBuilder
        {
            HtmlBody = html,
            TextBody = plain
        }.ToMessageBody();

        return message;
    }

    // ── Plain-text fallback ───────────────────────────────────────────────────

    private static string BuildPlainText(DashboardDataDto data)
    {
        var sb = new StringBuilder();
        sb.AppendLine("DevMetrics — Weekly Productivity Report");
        sb.AppendLine(new string('─', 44));

        if (data.DailySummaries.Count == 0)
        {
            sb.AppendLine("No commits recorded in the past 7 days.");
            return sb.ToString();
        }

        sb.AppendLine($"{"Date",-14} {"Commits",8} {"Added",10} {"Deleted",10}");
        sb.AppendLine(new string('─', 44));

        foreach (var d in data.DailySummaries)
            sb.AppendLine(
                $"{d.Date:yyyy-MM-dd,-14} {d.TotalCommits,8} " +
                $"+{d.LinesAdded,9} -{d.LinesDeleted,9}");

        sb.AppendLine(new string('─', 44));
        sb.AppendLine(
            $"{"Total",-14} {data.DailySummaries.Sum(d => d.TotalCommits),8} " +
            $"+{data.DailySummaries.Sum(d => d.LinesAdded),9} " +
            $"-{data.DailySummaries.Sum(d => d.LinesDeleted),9}");

        return sb.ToString();
    }

    // ── SMTP delivery ─────────────────────────────────────────────────────────

    private async Task SendMessageAsync(MimeMessage message, CancellationToken ct)
    {
        using var client = new SmtpClient();

        try
        {
            _logger.LogDebug(
                "Email | Connecting to {Host}:{Port} UseSsl={Ssl}",
                _settings.Host, _settings.Port, _settings.UseSsl);

            var socketOpts = _settings.UseSsl
                ? SecureSocketOptions.SslOnConnect
                : SecureSocketOptions.StartTls;

            await client.ConnectAsync(_settings.Host, _settings.Port, socketOpts, ct);

            if (!string.IsNullOrWhiteSpace(_settings.Username))
            {
                _logger.LogDebug("Email | Authenticating as {User}", _settings.Username);
                await client.AuthenticateAsync(
                    _settings.Username, _settings.Password, ct);
            }

            await client.SendAsync(message, ct);
        }
        finally
        {
            if (client.IsConnected)
                await client.DisconnectAsync(quit: true, ct);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string BuildSubject()
    {
        var to   = DateTime.UtcNow;
        var from = to.AddDays(-7);
        return $"DevMetrics Weekly — {from:MMM d}–{to:MMM d, yyyy}";
    }

    private static string HtmlEncode(string s) =>
        System.Net.WebUtility.HtmlEncode(s);

    private static string InlineFallbackTemplate() => """
        <!DOCTYPE html>
        <html><head><meta charset="utf-8"/></head>
        <body style="font-family:sans-serif;color:#111;padding:32px">
          <h1 style="color:#4f46e5">DevMetrics Weekly Report</h1>
          <p>{{DATE_RANGE}}</p>
          <p>Total commits: <strong>{{TOTAL_COMMITS}}</strong></p>
          {{DAILY_TABLE}}
          {{REPO_TAGS}}
          <p><a href="{{DASHBOARD_URL}}">Open Dashboard</a></p>
        </body></html>
        """;
}
