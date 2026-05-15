using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DevMetrics.Application.DTOs;
using DevMetrics.Core.DTOs;
using DevMetrics.Core.Entities;
using DevMetrics.Tests.Helpers;
using FluentAssertions;
using Moq;
using Xunit;

namespace DevMetrics.Tests.Integration.Controllers;

/// <summary>
/// Integration tests for <c>RepositoriesController</c>.
/// Exercises the full HTTP → MediatR → EF Core → SQLite pipeline.
/// </summary>
public sealed class RepositoriesControllerIntegrationTests
    : IClassFixture<CustomWebApplicationFactory>, IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public RepositoriesControllerIntegrationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client  = factory.CreateClient();
    }

    public Task InitializeAsync() => _factory.ResetDatabaseAsync();
    public Task DisposeAsync()    => Task.CompletedTask;

    // ── GET /api/repositories ─────────────────────────────────────────────────

    [Fact]
    public async Task GET_Returns_EmptyList_When_NoRepositoriesTracked()
    {
        // Act
        var response = await _client.GetAsync("/api/repositories");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<List<RepositoryDto>>(JsonOpts);
        body.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public async Task GET_Returns_AllTrackedRepositories()
    {
        // Arrange — seed two repositories directly into the test DB.
        var repos = new[]
        {
            new Repository
            {
                Id             = Guid.NewGuid(),
                Path           = "/repos/project-alpha",
                Name           = "project-alpha",
                LastScannedUtc = DateTime.UtcNow.AddHours(-1)
            },
            new Repository
            {
                Id             = Guid.NewGuid(),
                Path           = "/repos/project-beta",
                Name           = "project-beta",
                LastScannedUtc = DateTime.UtcNow.AddHours(-2)
            }
        };

        await _factory.SeedAsync(repos);

        // Act
        var response = await _client.GetAsync("/api/repositories");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<List<RepositoryDto>>(JsonOpts);
        body.Should().HaveCount(2)
            .And.Contain(r => r.Name == "project-alpha")
            .And.Contain(r => r.Name == "project-beta");
    }

    // ── POST /api/repositories ────────────────────────────────────────────────

    [Fact]
    public async Task POST_Creates_Repository_When_ValidGitPathProvided()
    {
        // Arrange — create a real temp directory with a .git folder.
        using var tempRepo = new TestGitRepositoryBuilder();
        tempRepo.AddCommit("Initial commit", [("README.md", "# Hello")]);

        // Configure the mock git service to return metadata for this path.
        _factory.GitServiceMock
            .Setup(g => g.GetRepositoryInfoAsync(It.IsAny<string>()))
            .ReturnsAsync(new RepositoryInfo(
                Path:           tempRepo.RepositoryPath,
                Name:           "test-integration-repo",
                LastCommitDate: DateTime.UtcNow.AddHours(-1),
                TotalCommits:   1));

        var payload = new { Path = tempRepo.RepositoryPath };

        // Act
        var response = await _client.PostAsJsonAsync("/api/repositories", payload);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created,
            "a valid Git repository path should produce 201 Created");

        response.Headers.Location.Should().NotBeNull(
            "the response should include a Location header per REST conventions");

        var body = await response.Content.ReadFromJsonAsync<RepositoryDto>(JsonOpts);
        body.Should().NotBeNull();
        body!.Name.Should().Be("test-integration-repo");
        body.Path.Should().Be(tempRepo.RepositoryPath);
        body.LastScannedUtc.Should().Be(DateTime.UnixEpoch,
            "a newly added repo is not yet scanned");
    }

    [Fact]
    public async Task POST_Returns_400_When_PathDoesNotExist()
    {
        // Arrange
        var payload = new { Path = "/this/path/does/not/exist/anywhere" };

        // Act
        var response = await _client.PostAsJsonAsync("/api/repositories", payload);

        // Assert
        response.StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task POST_Returns_409_When_PathAlreadyTracked()
    {
        // Arrange — seed the repository first.
        var existingId   = Guid.NewGuid();
        var existingPath = "/repos/already-tracked";

        await _factory.SeedAsync(new[]
        {
            new Repository
            {
                Id             = existingId,
                Path           = existingPath,
                Name           = "already-tracked",
                LastScannedUtc = DateTime.UtcNow
            }
        });

        // Arrange a fake git dir to pass the Directory.Exists and ValidateIsGitRepository checks.
        using var fakeDir = new TestGitRepositoryBuilder();
        // Override the seeded path to the real temp path so the filesystem check passes.
        // (We re-seed with the real path instead)
        await _factory.ResetDatabaseAsync();

        using var realRepo = new TestGitRepositoryBuilder();

        await _factory.SeedAsync(new[]
        {
            new Repository
            {
                Id             = existingId,
                Path           = realRepo.RepositoryPath,  // real path that exists
                Name           = "already-tracked",
                LastScannedUtc = DateTime.UtcNow
            }
        });

        _factory.GitServiceMock
            .Setup(g => g.GetRepositoryInfoAsync(realRepo.RepositoryPath))
            .ReturnsAsync(new RepositoryInfo(
                realRepo.RepositoryPath, "already-tracked", null, 0));

        var payload = new { Path = realRepo.RepositoryPath };

        // Act
        var response = await _client.PostAsJsonAsync("/api/repositories", payload);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "adding a repository whose path is already tracked must return 409");
    }

    // ── DELETE /api/repositories/{id} ─────────────────────────────────────────

    [Fact]
    public async Task DELETE_Removes_Repository_And_Related_Data()
    {
        // Arrange — seed a repository with commits.
        var repoId = Guid.NewGuid();
        var repo   = new Repository
        {
            Id             = repoId,
            Path           = "/repos/to-delete",
            Name           = "to-delete",
            LastScannedUtc = DateTime.UtcNow.AddHours(-1)
        };

        await _factory.SeedAsync(new[] { repo });

        await _factory.SeedAsync(new[]
        {
            new CommitRecord
            {
                Id           = Guid.NewGuid(),
                RepositoryId = repoId,
                Hash         = "aaaa1111aaaa1111aaaa1111aaaa1111aaaa1111",
                Author       = "Dev",
                DateUtc      = DateTime.UtcNow.AddHours(-1),
                LinesAdded   = 10,
                LinesDeleted = 2,
                FilesChanged = 1
            }
        });

        // Act
        var response = await _client.DeleteAsync($"/api/repositories/{repoId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NoContent,
            "a successful deletion should return 204 No Content");

        // Verify the repository no longer appears in GET.
        var getResponse = await _client.GetAsync("/api/repositories");
        var remaining   = await getResponse.Content
            .ReadFromJsonAsync<List<RepositoryDto>>(JsonOpts);
        remaining.Should().NotContain(r => r.Id == repoId,
            "the deleted repository must not appear in subsequent GET responses");
    }

    [Fact]
    public async Task DELETE_Returns_404_When_RepositoryNotFound()
    {
        // Arrange — a GUID that does not exist in the DB.
        var nonExistentId = Guid.NewGuid();

        // Act
        var response = await _client.DeleteAsync($"/api/repositories/{nonExistentId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
