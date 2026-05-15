using System.Net;
using System.Net.Http.Json;
using DevMetrics.Application.DTOs;
using DevMetrics.Core.Entities;
using FluentAssertions;
using Xunit;

namespace DevMetrics.Tests.Integration.Controllers;

/// <summary>
/// Integration tests for <c>DashboardController</c>.
/// </summary>
public sealed class DashboardControllerIntegrationTests
    : IClassFixture<CustomWebApplicationFactory>, IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    private static readonly System.Text.Json.JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public DashboardControllerIntegrationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client  = factory.CreateClient();
    }

    public Task InitializeAsync() => _factory.ResetDatabaseAsync();
    public Task DisposeAsync()    => Task.CompletedTask;

    // ── GET /api/dashboard/summary ────────────────────────────────────────────

    [Fact]
    public async Task GET_Summary_Returns_EmptyData_When_NoRepositoriesTracked()
    {
        // Act
        var response = await _client.GetAsync("/api/dashboard/summary?days=14");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<DashboardDataDto>(JsonOpts);
        body.Should().NotBeNull();
        body!.Repositories.Should().BeEmpty();
        body.DailySummaries.Should().BeEmpty();
        body.LastScanTime.Should().BeNull(
            "no repositories have been scanned yet");
    }

    [Fact]
    public async Task GET_Summary_Returns_DailySummaries_Within_Requested_Window()
    {
        // Arrange — seed a repository with daily summaries inside and outside the window.
        var repoId = Guid.NewGuid();

        await _factory.SeedAsync(new[]
        {
            new Repository
            {
                Id             = repoId,
                Path           = "/repos/charted",
                Name           = "charted",
                LastScannedUtc = DateTime.UtcNow.AddHours(-1)
            }
        });

        var today = DateTime.UtcNow.Date;

        // 5 days of summaries inside a 7-day window.
        var summaries = Enumerable.Range(0, 5)
            .Select(i => new DailySummary
            {
                Id                = Guid.NewGuid(),
                RepositoryId      = repoId,
                Date              = today.AddDays(-i),
                TotalCommits      = i + 1,
                TotalLinesAdded   = (i + 1) * 10,
                TotalLinesDeleted = (i + 1) * 3
            })
            .ToArray();

        // One summary older than the 7-day window — must not appear in results.
        var oldSummary = new DailySummary
        {
            Id                = Guid.NewGuid(),
            RepositoryId      = repoId,
            Date              = today.AddDays(-30),
            TotalCommits      = 999,
            TotalLinesAdded   = 9990,
            TotalLinesDeleted = 999
        };

        await _factory.SeedAsync(summaries.Append(oldSummary));

        // Act — request a 7-day window.
        var response = await _client.GetAsync("/api/dashboard/summary?days=7");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<DashboardDataDto>(JsonOpts);
        body.Should().NotBeNull();
        body!.DailySummaries.Should().HaveCount(5,
            "only the 5 summaries within the last 7 days should be returned");
        body.DailySummaries.Should().NotContain(
            s => s.TotalCommits == 999,
            "the 30-day-old summary is outside the requested window");
        body.DailySummaries.Should().BeInAscendingOrder(s => s.Date,
            "summaries must be sorted by date ascending for chart rendering");
    }

    [Fact]
    public async Task GET_Summary_Aggregates_Across_Multiple_Repositories()
    {
        // Arrange — two repos, each with a summary for today.
        var repo1Id = Guid.NewGuid();
        var repo2Id = Guid.NewGuid();
        var today   = DateTime.UtcNow.Date;

        await _factory.SeedAsync(new[]
        {
            new Repository { Id = repo1Id, Path = "/r1", Name = "repo1",
                             LastScannedUtc = DateTime.UtcNow.AddHours(-1) },
            new Repository { Id = repo2Id, Path = "/r2", Name = "repo2",
                             LastScannedUtc = DateTime.UtcNow.AddHours(-1) }
        });

        await _factory.SeedAsync(new[]
        {
            new DailySummary { Id = Guid.NewGuid(), RepositoryId = repo1Id,
                               Date = today, TotalCommits = 3,
                               TotalLinesAdded = 30, TotalLinesDeleted = 5 },
            new DailySummary { Id = Guid.NewGuid(), RepositoryId = repo2Id,
                               Date = today, TotalCommits = 7,
                               TotalLinesAdded = 70, TotalLinesDeleted = 15 }
        });

        // Act
        var response = await _client.GetAsync("/api/dashboard/summary?days=1");
        var body     = await response.Content.ReadFromJsonAsync<DashboardDataDto>(JsonOpts);

        // Assert — the handler groups by date and sums across repos.
        body!.DailySummaries.Should().HaveCount(1,
            "both repos have a summary for the same day — they should be merged");

        var todaySummary = body.DailySummaries.Single();
        todaySummary.TotalCommits.Should().Be(10,   "3 + 7");
        todaySummary.LinesAdded.Should().Be(100,    "30 + 70");
        todaySummary.LinesDeleted.Should().Be(20,   "5 + 15");
    }

    [Fact]
    public async Task GET_Summary_Returns_400_When_Days_OutOfRange()
    {
        // Act — request 0 days (invalid).
        var responseZero = await _client.GetAsync("/api/dashboard/summary?days=0");
        responseZero.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Act — request 366 days (exceeds maximum of 365).
        var responseHigh = await _client.GetAsync("/api/dashboard/summary?days=366");
        responseHigh.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── GET /api/dashboard/health ─────────────────────────────────────────────

    [Fact]
    public async Task GET_Health_Returns_Ok_With_DbConnected_True()
    {
        // Act
        var response = await _client.GetAsync("/api/dashboard/health");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<HealthResponseBody>(JsonOpts);
        body.Should().NotBeNull();
        body!.DbConnected.Should().BeTrue(
            "the test SQLite database was created and migrated by the factory");
        body.Status.Should().Be("ok");
    }

    // ── Response model (matches HealthResponse in DashboardController) ────────

    private sealed record HealthResponseBody(
        string    Status,
        bool      DbConnected,
        DateTime? LastScanTime);
}
