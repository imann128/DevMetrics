using DevMetrics.Application.DTOs;
using DevMetrics.Application.Services;

namespace DevMetrics.Tests.Helpers;

/// <summary>
/// In-memory <see cref="IEmailService"/> test double that captures sent emails
/// without contacting an SMTP server.
/// Thread-safe for concurrent test scenarios.
/// </summary>
public sealed class MockEmailService : IEmailService
{
    private readonly List<SentEmail> _sent = new();
    private readonly object _lock = new();

    /// <summary>
    /// All emails sent since this instance was created, in send order.
    /// Thread-safe snapshot is returned.
    /// </summary>
    public IReadOnlyList<SentEmail> SentEmails
    {
        get { lock (_lock) { return _sent.ToArray(); } }
    }

    /// <summary>The number of send calls made so far.</summary>
    public int SendCount
    {
        get { lock (_lock) { return _sent.Count; } }
    }

    /// <summary>
    /// When set to a non-null value, <see cref="SendWeeklySummaryAsync"/>
    /// throws this exception — simulates SMTP failures for retry tests.
    /// </summary>
    public Exception? ThrowOnSend { get; set; }

    /// <inheritdoc/>
    public Task SendWeeklySummaryAsync(
        string[]         recipients,
        DashboardDataDto data,
        CancellationToken ct = default)
    {
        if (ThrowOnSend is not null)
            throw ThrowOnSend;

        lock (_lock)
        {
            _sent.Add(new SentEmail(
                Recipients: recipients.ToArray(),
                Data:       data,
                SentAt:     DateTime.UtcNow));
        }

        return Task.CompletedTask;
    }

    /// <summary>Clears all recorded emails.</summary>
    public void Reset()
    {
        lock (_lock) { _sent.Clear(); }
    }
}

/// <summary>Captures a single email sent via <see cref="MockEmailService"/>.</summary>
/// <param name="Recipients">The recipient addresses.</param>
/// <param name="Data">The dashboard payload that was included in the email.</param>
/// <param name="SentAt">UTC timestamp of the send call.</param>
public sealed record SentEmail(
    string[]         Recipients,
    DashboardDataDto Data,
    DateTime         SentAt);
