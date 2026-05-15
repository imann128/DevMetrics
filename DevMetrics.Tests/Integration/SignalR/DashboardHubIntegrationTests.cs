using DevMetrics.Application.DTOs;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DevMetrics.Tests.Integration.SignalR;

/// <summary>
/// Integration tests for <see cref="DevMetrics.Api.Hubs.DashboardHub"/>.
/// Uses <see cref="Microsoft.AspNetCore.SignalR.Client.HubConnection"/> pointed at the
/// <see cref="Microsoft.AspNetCore.TestHost.TestServer"/>'s in-process transport,
/// so no real TCP socket is opened — tests are fast and CI-friendly.
/// </summary>
public sealed class DashboardHubIntegrationTests
    : IClassFixture<CustomWebApplicationFactory>, IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;
    private HubConnection? _connection;

    public DashboardHubIntegrationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // ── IAsyncLifetime ────────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        // Build a HubConnection that uses the TestServer's in-process handler,
        // bypassing the network stack entirely.
        _connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "dashboardHub"), options =>
            {
                // Wire the HttpMessageHandler to the test server — no real HTTP is used.
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
            })
            .Build();

        await _connection.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.StopAsync();
            await _connection.DisposeAsync();
        }
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Client_Can_Connect_And_Start_Connection()
    {
        // Assert — the connection was started in InitializeAsync.
        _connection!.State.Should().Be(HubConnectionState.Connected,
            "the hub should be reachable via the in-process TestServer transport");
    }

    [Fact]
    public async Task Client_Can_Join_Dashboard_Group_Without_Error()
    {
        // Act — should not throw; the hub method returns Task.
        var act = async () =>
            await _connection!.InvokeAsync("JoinDashboardGroup", "dashboard");

        await act.Should().NotThrowAsync(
            "JoinDashboardGroup adds the connection to a group and returns cleanly");
    }

    [Fact]
    public async Task Client_Receives_ScanCompleted_Event_When_Broadcast_From_Server()
    {
        // Arrange — set up a message capture with a timeout.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var tcs       = new TaskCompletionSource<ScanResultDto>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        _connection!.On<ScanResultDto>("ScanCompleted", payload =>
        {
            tcs.TrySetResult(payload);
        });

        await _connection.InvokeAsync("JoinDashboardGroup", "dashboard", cts.Token);

        // Act — use the hub context to broadcast from the server side.
        // Resolve IHubContext from the factory's service provider.
        using var scope = _factory.Services.CreateScope();
        var hubContext  = scope.ServiceProvider
            .GetRequiredService<
                Microsoft.AspNetCore.SignalR.IHubContext<DevMetrics.Api.Hubs.DashboardHub>>();

        var expectedResult = new ScanResultDto(
            RepositoriesScanned: 3,
            NewCommitsFound:     12,
            DurationMs:          450,
            Status:              "Completed");

        await hubContext.Clients.All.SendCoreAsync("ScanCompleted", new object[] { expectedResult }, cts.Token);

        // Await the client receiving the broadcast.
        cts.Token.Register(() => tcs.TrySetCanceled());
        var received = await tcs.Task;

        // Assert
        received.Should().NotBeNull();
        received.RepositoriesScanned.Should().Be(3);
        received.NewCommitsFound.Should().Be(12);
        received.Status.Should().Be("Completed");
    }

    [Fact]
    public async Task Client_Receives_RepositoryActivityDetected_When_Notified()
    {
        // Arrange
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var tcs       = new TaskCompletionSource<dynamic>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        _connection!.On<string, string>("RepositoryActivityDetected",
            (path, name) => tcs.TrySetResult(new { path, name }));

        await _connection.InvokeAsync("JoinDashboardGroup", "dashboard", cts.Token);

        using var scope = _factory.Services.CreateScope();
        var hubContext  = scope.ServiceProvider
            .GetRequiredService<
                Microsoft.AspNetCore.SignalR.IHubContext<DevMetrics.Api.Hubs.DashboardHub>>();

        // Act — server broadcasts the repository activity event.
        await hubContext.Clients.All.SendCoreAsync("RepositoryActivityDetected", new object[] { new { repositoryPath = "/repos/my-project", repositoryName = "my-project" } }, cts.Token);

        cts.Token.Register(() => tcs.TrySetCanceled());

        // Assert — the client receives the event without timing out.
        var act = async () => await tcs.Task;
        await act.Should().NotThrowAsync(
            "the client should receive RepositoryActivityDetected within the timeout");
    }

    [Fact]
    public async Task Client_Can_Leave_Dashboard_Group()
    {
        // Arrange
        await _connection!.InvokeAsync("JoinDashboardGroup", "dashboard");

        // Act
        var act = async () =>
            await _connection.InvokeAsync("LeaveDashboardGroup", "dashboard");

        // Assert — removing from a group should not throw.
        await act.Should().NotThrowAsync();
    }
}
