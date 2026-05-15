namespace DevMetrics.Application.Settings;

/// <summary>
/// Strongly-typed SMTP and email template configuration.
/// Bound from the <c>Email</c> section of <c>appsettings.json</c>.
/// </summary>
public sealed class EmailSettings
{
    /// <summary>Configuration section key.</summary>
    public const string SectionName = "Email";

    // ── SMTP ──────────────────────────────────────────────────────────────────

    /// <summary>SMTP server hostname. Default: <c>smtp.gmail.com</c>.</summary>
    public string Host { get; set; } = "smtp.gmail.com";

    /// <summary>SMTP server port. Default: <c>587</c> (STARTTLS).</summary>
    public int Port { get; set; } = 587;

    /// <summary>SMTP authentication username.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// SMTP authentication password.
    /// Supply via environment variable or secrets manager in production.
    /// </summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>Sender address in the <c>From</c> header.</summary>
    public string FromAddress { get; set; } = string.Empty;

    /// <summary>Sender display name. Default: <c>"DevMetrics"</c>.</summary>
    public string FromName { get; set; } = "DevMetrics";

    /// <summary>
    /// <c>true</c>: SSL/TLS on connect (port 465).
    /// <c>false</c>: STARTTLS (port 587). Default: <c>false</c>.
    /// </summary>
    public bool UseSsl { get; set; } = false;

    // ── Feature flags ─────────────────────────────────────────────────────────

    /// <summary>
    /// Set <c>false</c> to suppress all outbound mail (e.g., in development).
    /// Default: <c>false</c> — explicit opt-in required.
    /// </summary>
    public bool Enabled { get; set; } = false;

    // ── Recipients ────────────────────────────────────────────────────────────

    /// <summary>
    /// Weekly report recipient list (primary key name in appsettings.json).
    /// </summary>
    public string[] Recipients { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Alias kept for backward compatibility with the previous settings shape.
    /// When <see cref="Recipients"/> is non-empty it takes precedence.
    /// </summary>
    public string[] WeeklyReportRecipients
    {
        get => Recipients.Length > 0 ? Recipients : _legacy;
        set => _legacy = value;
    }

    private string[] _legacy = Array.Empty<string>();

    // ── Template and branding ─────────────────────────────────────────────────

    /// <summary>
    /// Optional override path to the weekly summary HTML template.
    /// When empty the embedded resource <c>Templates/WeeklySummary.html</c> is used.
    /// </summary>
    public string WeeklySummaryTemplatePath { get; set; } = string.Empty;

    /// <summary>
    /// Dashboard URL embedded in email footers.
    /// Default: <c>"http://localhost:5000"</c>.
    /// </summary>
    public string DashboardBaseUrl { get; set; } = "http://localhost:5000";
}
