using System.Net;
using System.Net.Http.Json;
using BotNexus.Gateway.Api;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Tests.Helpers;
using BotNexus.Gateway.Updates;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Gateway.Tests.Updates;

/// <summary>
/// Integration tests for <c>UpdateController</c> (GET /api/gateway/update/status,
/// POST /api/gateway/update/check, POST /api/gateway/update/start).
/// All tests FAIL until Farnsworth implements UpdateController and IUpdateCheckService.
/// </summary>
[Trait("Category", "Integration")]
[Collection("IntegrationTests")]
public sealed class UpdateControllerTests
{
    // ──────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────

    private static UpdateStatusResult MakeStatus(
        bool enabled = true,
        bool isUpdateAvailable = false,
        bool isUpdateInProgress = false,
        string currentSha = "aaaa0000aaaa0000aaaa0000aaaa0000aaaa0000",
        string? latestSha = null) =>
        new(
            Enabled: enabled,
            IsChecking: false,
            IsUpdateAvailable: isUpdateAvailable,
            IsUpdateInProgress: isUpdateInProgress,
            CurrentCommitSha: currentSha,
            CurrentCommitShort: currentSha[..7],
            LatestCommitSha: latestSha,
            LatestCommitShort: latestSha?[..7],
            LastCheckedAt: DateTimeOffset.UtcNow.AddMinutes(-5),
            NextCheckAt: DateTimeOffset.UtcNow.AddMinutes(55),
            RepositoryOwner: "sytone",
            RepositoryName: "botnexus",
            Branch: "main",
            CompareUrl: null,
            Error: null);

    private static WebApplicationFactory<Program> CreateFactory(
        Mock<IUpdateCheckService> mockSvc,
        Action<IServiceCollection>? extra = null)
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.UseUrls("http://127.0.0.1:0");
                builder.ConfigureServices(services =>
                {
                    // Remove hosted services so we don't try to start background work
                    var toRemove = services
                        .Where(d => d.ServiceType == typeof(IHostedService))
                        .ToList();
                    foreach (var d in toRemove) services.Remove(d);

                    services.AddSignalRChannelForTests();
                });
                builder.ConfigureTestServices(services =>
                {
                    // Register mock IUpdateCheckService
                    services.RemoveAll<IUpdateCheckService>();
                    services.AddSingleton(mockSvc.Object);
                    extra?.Invoke(services);
                });
            });
    }

    // ──────────────────────────────────────────────────────────────────
    // GET /api/gateway/update/status
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetStatus_Returns200_WithStatusPayload()
    {
        var mock = new Mock<IUpdateCheckService>();
        mock.Setup(s => s.GetCurrentStatus()).Returns(MakeStatus());

        await using var factory = CreateFactory(mock);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/gateway/update/status");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetStatus_PayloadHasCurrentCommitSha()
    {
        const string sha = "deadbeef1234deadbeef1234deadbeef1234dead";
        var mock = new Mock<IUpdateCheckService>();
        mock.Setup(s => s.GetCurrentStatus()).Returns(MakeStatus(currentSha: sha));

        await using var factory = CreateFactory(mock);
        using var client = factory.CreateClient();

        var json = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/gateway/update/status");

        json.GetProperty("currentCommitSha").GetString().ShouldBe(sha);
    }

    [Fact]
    public async Task GetStatus_PayloadHasIsUpdateAvailableField()
    {
        var mock = new Mock<IUpdateCheckService>();
        mock.Setup(s => s.GetCurrentStatus()).Returns(MakeStatus(isUpdateAvailable: true));

        await using var factory = CreateFactory(mock);
        using var client = factory.CreateClient();

        var json = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/gateway/update/status");

        json.GetProperty("isUpdateAvailable").GetBoolean().ShouldBeTrue();
    }

    // ──────────────────────────────────────────────────────────────────
    // POST /api/gateway/update/check
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CheckNow_Post_Returns200_WithRefreshedStatus()
    {
        var refreshed = MakeStatus(isUpdateAvailable: true,
            latestSha: "bbbb1111bbbb1111bbbb1111bbbb1111bbbb1111");
        var mock = new Mock<IUpdateCheckService>();
        mock.Setup(s => s.CheckNowAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(refreshed);

        await using var factory = CreateFactory(mock);
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/gateway/update/check", null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        json.GetProperty("isUpdateAvailable").GetBoolean().ShouldBeTrue();
    }

    // ──────────────────────────────────────────────────────────────────
    // POST /api/gateway/update/start
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Start_WhenAutoUpdateDisabled_Returns412()
    {
        var mock = new Mock<IUpdateCheckService>();
        mock.Setup(s => s.StartUpdateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UpdateStartResult(false, null, "412: Auto-update is not enabled"));

        await using var factory = CreateFactory(mock);
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/gateway/update/start", null);

        response.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
    }

    [Fact]
    public async Task Start_WhenCliPathNotConfigured_Returns412()
    {
        var mock = new Mock<IUpdateCheckService>();
        mock.Setup(s => s.StartUpdateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UpdateStartResult(false, null, "412: CliPath is not configured"));

        await using var factory = CreateFactory(mock);
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/gateway/update/start", null);

        response.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
    }

    [Fact]
    public async Task Start_WhenUpdateAlreadyInProgress_Returns409()
    {
        var mock = new Mock<IUpdateCheckService>();
        mock.Setup(s => s.StartUpdateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UpdateStartResult(false, null, "409: Update already in progress"));

        await using var factory = CreateFactory(mock);
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/gateway/update/start", null);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Start_WhenUpdateAvailableAndConfigured_Returns202()
    {
        var mock = new Mock<IUpdateCheckService>();
        mock.Setup(s => s.StartUpdateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UpdateStartResult(true, 12345, "Update started"));

        await using var factory = CreateFactory(mock);
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/gateway/update/start", null);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }
}
