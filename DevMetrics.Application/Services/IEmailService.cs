using DevMetrics.Application.DTOs;

namespace DevMetrics.Application.Services;

/// <summary>
/// Abstraction for sending outbound email from the Application layer.
/// The concrete implementation (<see cref="EmailService"/>) uses MailKit;
/// tests can substitute a fake without an SMTP server.
/// </summary>
public interface IEmailService
{
    /// <summary>
    /// Sends the weekly productivity summary email to the specified recipients.
    /// </summary>
    /// <param name="recipients">
    /// One or more email addresses to send the report to.
    /// Must not be null or empty.
    /// </param>
    /// <param name="data">
    /// The dashboard payload to render into the email body.
    /// The method uses <see cref="DashboardDataDto.DailySummaries"/>
    /// to build the commit-activity table.
    /// </param>
    /// <param name="ct">Cancellation token for the SMTP operation.</param>
    /// <returns>
    /// A completed <see cref="Task"/> on success.
    /// Throws <see cref="InvalidOperationException"/> when email is disabled
    /// via <see cref="Settings.EmailSettings.Enabled"/>.
    /// </returns>
    Task SendWeeklySummaryAsync(
        string[]        recipients,
        DashboardDataDto data,
        CancellationToken ct = default);
}
