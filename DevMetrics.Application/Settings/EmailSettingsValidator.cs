using Microsoft.Extensions.Options;

namespace DevMetrics.Application.Settings;

/// <summary>
/// Validates <see cref="EmailSettings"/> at application startup via the
/// <see cref="IValidateOptions{TOptions}"/> pipeline.
/// When <c>Email:Enabled = true</c>, all required SMTP fields must be populated.
/// A failed validation throws <see cref="OptionsValidationException"/> during
/// the first resolution of <c>IOptions&lt;EmailSettings&gt;</c>, which
/// (in ASP.NET Core) occurs during host startup — giving a clear, early failure
/// rather than a cryptic SMTP exception when the first email is attempted.
/// </summary>
/// <remarks>
/// Register with:
/// <code>
/// services.AddSingleton&lt;IValidateOptions&lt;EmailSettings&gt;, EmailSettingsValidator&gt;();
/// </code>
/// </remarks>
public sealed class EmailSettingsValidator : IValidateOptions<EmailSettings>
{
    /// <inheritdoc/>
    public ValidateOptionsResult Validate(string? name, EmailSettings options)
    {
        // When email is disabled, no SMTP fields are required.
        if (!options.Enabled)
            return ValidateOptionsResult.Success;

        var failures = new List<string>();

        // ── SMTP host ─────────────────────────────────────────────────────────
        if (string.IsNullOrWhiteSpace(options.Host))
            failures.Add("Email:Host must not be empty when Email:Enabled is true.");

        // ── Port ──────────────────────────────────────────────────────────────
        if (options.Port is < 1 or > 65535)
            failures.Add($"Email:Port '{options.Port}' is invalid. Common values: 25, 465 (SSL), 587 (STARTTLS).");

        // ── From address ──────────────────────────────────────────────────────
        if (string.IsNullOrWhiteSpace(options.FromAddress))
            failures.Add("Email:FromAddress must not be empty when Email:Enabled is true.");
        else if (!IsValidEmail(options.FromAddress))
            failures.Add($"Email:FromAddress '{options.FromAddress}' is not a valid email address.");

        // ── Authentication ────────────────────────────────────────────────────
        // If username is provided, password must also be present.
        if (!string.IsNullOrWhiteSpace(options.Username)
            && string.IsNullOrWhiteSpace(options.Password))
        {
            failures.Add(
                "Email:Password must not be empty when Email:Username is provided. " +
                "Use an environment variable or a secrets manager to supply the password.");
        }

        // ── Recipients ────────────────────────────────────────────────────────
        var recipients = options.WeeklyReportRecipients;

        if (recipients.Length == 0)
            failures.Add(
                "Email:Recipients must contain at least one address when Email:Enabled is true.");
        else
        {
            var invalid = recipients.Where(r => !IsValidEmail(r)).ToList();
            if (invalid.Count > 0)
                failures.Add(
                    $"Email:Recipients contains invalid address(es): {string.Join(", ", invalid)}");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Lightweight email address format check — intentionally permissive.
    /// Relies on the SMTP server for definitive validation.
    /// </summary>
    private static bool IsValidEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email)) return false;

        var atIndex = email.IndexOf('@');
        return atIndex > 0
            && atIndex < email.Length - 2
            && email.LastIndexOf('@') == atIndex;  // exactly one '@'
    }
}
