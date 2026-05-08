using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Updates;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using System.Net;
using System.Net.Http;
using System.IO.Abstractions.TestingHelpers;

namespace BotNexus.Gateway.Tests.Updates;

/// <summary>
/// Unit tests for <see cref="IUpdateCheckService"/> / <see cref="UpdateCheckService"/>.
/// All tests will FAIL until Farnsworth implements UpdateCheckService.
/// </summary>
public sealed class UpdateCheckServiceTests
{
    // ──────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────

    private static IOptions<PlatformConfig> BuildConfig(Action<AutoUpdateConfig>? configure = null)
    {
        var autoUpdate = new AutoUpdateConfig
        {
            Enabled = true,
            RepositoryOwner = "sytone",
            RepositoryName = "botnexus",
            Branch = "main",
            CheckIntervalMinutes = 60,
            CliPath = Path.Combine(Path.GetTempPath(), "botnexus", "BotNexus.Cli.dll"),
            SourcePath = Path.Combine(Path.GetTempPath(), "botnexus"),
            ShutdownDelaySeconds = 2,
        };
        configure?.Invoke(autoUpdate);

        return Options.Create(new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                AutoUpdate = autoUpdate,
            },
        });
    }

    private static IOptions<PlatformConfig> BuildDisabledConfig() =>
        Options.Create(new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                AutoUpdate = new AutoUpdateConfig { Enabled = false },
            },
        });

    /// <summary>
    /// Builds an UpdateCheckService with a stubbed HttpMessageHandler that returns
    /// the given JSON body for any request.
    /// </summary>
    private static IUpdateCheckService BuildService(
        IOptions<PlatformConfig> config,
        string githubResponseJson,
        HttpStatusCode githubStatusCode = HttpStatusCode.OK,
        MockFileSystem? fs = null)
    {
        var handler = new StubHttpMessageHandler(githubStatusCode, githubResponseJson);
        var httpClient = new HttpClient(handler);
        var fileSystem = fs ?? new MockFileSystem();

        // UpdateCheckService constructor will need: IOptions<PlatformConfig>, HttpClient,
        // IFileSystem, ILogger<UpdateCheckService>, IHostApplicationLifetime
        // — compile will fail until Farnsworth creates the concrete class.
        return new UpdateCheckService(
            config,
            httpClient,
            fileSystem,
            NullLogger<UpdateCheckService>.Instance,
            new Mock<IHostApplicationLifetime>().Object);
    }

    private static string MakeGitHubCommitJson(string sha) =>
        "{\"sha\":\"" + sha + "\",\"commit\":{\"message\":\"test\"}}";

    // ──────────────────────────────────────────────────────────────────
    // GetCurrentStatus tests
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void GetCurrentStatus_WhenDisabled_ReturnsDisabledStatus()
    {
        var config = BuildDisabledConfig();
        var svc = BuildService(config, MakeGitHubCommitJson("abc123"));

        var status = svc.GetCurrentStatus();

        status.Enabled.ShouldBeFalse();
    }

    [Fact]
    public void GetCurrentStatus_WhenEnabled_AndNoCheckYet_ReturnsUnknownLatest()
    {
        var config = BuildConfig();
        var svc = BuildService(config, MakeGitHubCommitJson("abc123"));

        var status = svc.GetCurrentStatus();

        status.Enabled.ShouldBeTrue();
        status.LatestCommitSha.ShouldBeNull();
        status.LastCheckedAt.ShouldBeNull();
    }

    // ──────────────────────────────────────────────────────────────────
    // CheckNowAsync tests
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CheckNowAsync_WhenGitHubReturnsNewerCommit_SetsIsUpdateAvailable()
    {
        const string currentSha = "aaaa0000aaaa0000aaaa0000aaaa0000aaaa0000";
        const string latestSha = "bbbb1111bbbb1111bbbb1111bbbb1111bbbb1111";

        var config = BuildConfig();
        var svc = BuildService(config, MakeGitHubCommitJson(latestSha));

        // The service must know the current commit; inject via reflection, env, or a separate interface.
        // For test purposes we assume the service resolves CurrentCommitSha from GatewayBuildInfo
        // which will be faked or overridden in the concrete implementation.
        // For now this test asserts the contract: a different latest SHA means IsUpdateAvailable.
        var result = await svc.CheckNowAsync();

        result.IsUpdateAvailable.ShouldBeTrue();
        result.LatestCommitSha.ShouldBe(latestSha);
    }

    [Fact]
    public async Task CheckNowAsync_WhenGitHubReturnsSameCommit_IsUpdateAvailable_IsFalse()
    {
        const string sha = "aaaa0000aaaa0000aaaa0000aaaa0000aaaa0000";

        var config = BuildConfig();
        var svc = BuildService(config, MakeGitHubCommitJson(sha));

        var result = await svc.CheckNowAsync();

        result.IsUpdateAvailable.ShouldBeFalse();
    }

    [Fact]
    public async Task CheckNowAsync_WhenGitHubFails_SetsError_DoesNotCrash()
    {
        var config = BuildConfig();
        var svc = BuildService(config, "", HttpStatusCode.ServiceUnavailable);

        var result = await svc.CheckNowAsync();

        result.Error.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task CheckNowAsync_SetsLastCheckedAt_ToNow()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var config = BuildConfig();
        var svc = BuildService(config, MakeGitHubCommitJson("abc123"));

        var result = await svc.CheckNowAsync();
        var after = DateTimeOffset.UtcNow.AddSeconds(1);

        result.LastCheckedAt.ShouldNotBeNull();
        result.LastCheckedAt!.Value.ShouldBeGreaterThan(before);
        result.LastCheckedAt!.Value.ShouldBeLessThan(after);
    }

    [Fact]
    public async Task CheckNowAsync_SetsNextCheckAt_ToIntervalFromNow()
    {
        var config = BuildConfig(c => c.CheckIntervalMinutes = 30);
        var svc = BuildService(config, MakeGitHubCommitJson("abc123"));

        var result = await svc.CheckNowAsync();

        result.NextCheckAt.ShouldNotBeNull();
        var expectedMin = DateTimeOffset.UtcNow.AddMinutes(29);
        var expectedMax = DateTimeOffset.UtcNow.AddMinutes(31);
        result.NextCheckAt!.Value.ShouldBeGreaterThan(expectedMin);
        result.NextCheckAt!.Value.ShouldBeLessThan(expectedMax);
    }

    // ──────────────────────────────────────────────────────────────────
    // StartUpdateAsync tests
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task StartUpdateAsync_WhenUpdateNotAvailable_ReturnsFailed()
    {
        const string sha = "aaaa0000aaaa0000aaaa0000aaaa0000aaaa0000";
        var config = BuildConfig();
        var svc = BuildService(config, MakeGitHubCommitJson(sha)); // same sha → no update

        // Force a check so status is populated with IsUpdateAvailable = false
        await svc.CheckNowAsync();
        var result = await svc.StartUpdateAsync();

        result.Started.ShouldBeFalse();
    }

    [Fact]
    public async Task StartUpdateAsync_WhenCliPathMissing_Returns412()
    {
        var config = BuildConfig(c => c.CliPath = null);
        var svc = BuildService(config, MakeGitHubCommitJson("latest-sha"));

        var result = await svc.StartUpdateAsync();

        result.Started.ShouldBeFalse();
        result.Message.ShouldContain("412");
    }

    [Fact]
    public async Task StartUpdateAsync_WhenSourcePathMissing_Returns412()
    {
        var config = BuildConfig(c => c.SourcePath = null);
        var svc = BuildService(config, MakeGitHubCommitJson("latest-sha"));

        var result = await svc.StartUpdateAsync();

        result.Started.ShouldBeFalse();
        result.Message.ShouldContain("412");
    }

    [Fact]
    public async Task StartUpdateAsync_WhenAlreadyInProgress_Returns409()
    {
        var config = BuildConfig();
        var fs = new MockFileSystem();

        // Make paths exist so prerequisites pass
        var cliPath = Path.Combine(Path.GetTempPath(), "botnexus", "BotNexus.Cli.dll");
        var sourcePath = Path.Combine(Path.GetTempPath(), "botnexus");
        fs.AddFile(cliPath, new MockFileData(""));
        fs.AddDirectory(sourcePath);
        config = BuildConfig(c => { c.CliPath = cliPath; c.SourcePath = sourcePath; });

        var svc = BuildService(config, MakeGitHubCommitJson("latest-sha"), fs: fs);

        // First start should succeed; second should get 409
        await svc.StartUpdateAsync();
        var result = await svc.StartUpdateAsync();

        result.Started.ShouldBeFalse();
        result.Message.ShouldContain("409");
    }

    [Fact]
    public async Task StartUpdateAsync_WhenPrerequisitesMet_SpawnsProcess_Returns202()
    {
        var config = BuildConfig();
        var fs = new MockFileSystem();

        var cliPath = Path.Combine(Path.GetTempPath(), "botnexus", "BotNexus.Cli.dll");
        var sourcePath = Path.Combine(Path.GetTempPath(), "botnexus");
        fs.AddFile(cliPath, new MockFileData(""));
        fs.AddDirectory(sourcePath);
        config = BuildConfig(c => { c.CliPath = cliPath; c.SourcePath = sourcePath; });

        var svc = BuildService(config, MakeGitHubCommitJson("latest-sha"), fs: fs);

        // Ensure IsUpdateAvailable is true (different sha from current)
        await svc.CheckNowAsync();
        var result = await svc.StartUpdateAsync();

        result.Started.ShouldBeTrue();
        result.ProcessId.ShouldNotBeNull();
    }

    // ──────────────────────────────────────────────────────────────────
    // Hosted service test
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void UpdateCheckService_IsHostedService_StartsPeriodicChecks()
    {
        // UpdateCheckService must implement IHostedService so the DI container can
        // register it both as IUpdateCheckService and as a hosted service.
        var config = BuildConfig();
        var svc = BuildService(config, MakeGitHubCommitJson("abc123"));

        svc.ShouldBeAssignableTo<IHostedService>();
    }

    // ──────────────────────────────────────────────────────────────────
    // Internal stub handler
    // ──────────────────────────────────────────────────────────────────

    private sealed class StubHttpMessageHandler(HttpStatusCode status, string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }
}
