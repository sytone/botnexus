using System.Diagnostics;
using System.Net;
using System.Text.Json;
using BotNexus.Gateway.Api;
using BotNexus.Gateway.Api.ReleaseHistory;
using BotNexus.Gateway.Configuration;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Tests;

[Collection("IntegrationTests")]
public sealed class ReleaseHistoryControllerTests
{
    [Fact]
    public async Task Get_reads_manifest_and_git_distance_from_configured_checkout()
    {
        using var repository = TestRepository.Create();
        var releaseCommit = repository.Commit("release", "seed");
        repository.Run("tag", "v1.2.3");
        repository.Commit("publish history", Manifest("1.2.3", releaseCommit));
        var runningCommit = repository.Run("rev-parse", "HEAD").Trim();
        var headCommit = repository.Commit("next", repository.ManifestText);
        repository.Run("update-ref", "refs/remotes/origin/main", headCommit);

        await using var factory = CreateFactory(repository.Path, runningCommit);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/release-history");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var json = document.RootElement;

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        json.GetProperty("releases")[0].GetProperty("version").GetString().ShouldBe("1.2.3");
        json.GetProperty("sourceStatus").GetProperty("runningCommit").GetString().ShouldBe(runningCommit);
        json.GetProperty("sourceStatus").GetProperty("checkoutHead").GetString().ShouldBe(headCommit);
        json.GetProperty("sourceStatus").GetProperty("remoteDistance").GetProperty("behind").GetInt32().ShouldBe(1);
        json.GetProperty("sourceStatus").GetProperty("releaseDistance").GetProperty("ahead").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task Get_preserves_running_identity_when_checkout_head_differs()
    {
        using var repository = TestRepository.Create();
        var runningCommit = repository.Commit("release", "seed");
        repository.Commit("publish history", Manifest("1.2.3", runningCommit));
        var checkoutHead = repository.Run("rev-parse", "HEAD").Trim();
        repository.Run("update-ref", "refs/remotes/origin/main", checkoutHead);

        await using var factory = CreateFactory(repository.Path, runningCommit);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/release-history");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var status = document.RootElement.GetProperty("sourceStatus");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        status.GetProperty("runningCommit").GetString().ShouldBe(runningCommit);
        status.GetProperty("checkoutHead").GetString().ShouldBe(checkoutHead);
        checkoutHead.ShouldNotBe(runningCommit);
    }

    [Fact]
    public async Task Get_uses_local_history_when_remote_tracking_ref_is_unavailable()
    {
        using var repository = TestRepository.Create();
        var runningCommit = repository.Commit("release", "seed");
        repository.Commit("publish history", Manifest("1.2.3", runningCommit));

        await using var factory = CreateFactory(repository.Path, runningCommit);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/release-history");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        document.RootElement.GetProperty("releases")[0].GetProperty("version").GetString().ShouldBe("1.2.3");
        document.RootElement.GetProperty("sourceStatus").GetProperty("remoteDistance").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Get_refresh_failure_retains_local_history_and_reports_cached_remote_state()
    {
        using var repository = TestRepository.Create();
        var runningCommit = repository.Commit("release", "seed");
        repository.Commit("publish history", Manifest("1.2.3", runningCommit));
        var checkoutHead = repository.Run("rev-parse", "HEAD").Trim();
        repository.Run("update-ref", "refs/remotes/origin/main", checkoutHead);

        await using var factory = CreateFactory(repository.Path, runningCommit);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/release-history?refresh=true");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        root.GetProperty("releases")[0].GetProperty("version").GetString().ShouldBe("1.2.3");
        root.GetProperty("sourceStatus").GetProperty("remoteDistance").ValueKind.ShouldBe(JsonValueKind.Object);
        root.GetProperty("sourceStatus").GetProperty("remoteRefreshError").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Get_returns_service_unavailable_for_missing_checkout()
    {
        await using var factory = CreateFactory(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N")),
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/release-history");

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Get_returns_service_unavailable_for_invalid_repository()
    {
        using var directory = new TemporaryDirectory();
        await using var factory = CreateFactory(directory.Path, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/release-history");

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    private static WebApplicationFactory<Program> CreateFactory(string sourcePath, string runningCommit) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Environment", "Testing");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:AutoUpdate:SourcePath"] = sourcePath,
                ["Gateway:AutoUpdate:Branch"] = "main"
            }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<LocalReleaseHistoryService>();
                services.AddSingleton(provider => new LocalReleaseHistoryService(
                    provider.GetRequiredService<IOptions<PlatformConfig>>(),
                    NullLogger<LocalReleaseHistoryService>.Instance,
                    runningCommit));
            });
        });

    private static string Manifest(string version, string commit) => $$"""
        {"schemaVersion":"1.0.0","releases":[{"version":"{{version}}","tag":"v{{version}}","commit":"{{commit}}","releasedAt":"2026-10-01","releaseUrl":"https://github.com/Sytone/botnexus/releases/tag/v{{version}}","documentationUrl":"https://sytone.github.io/botnexus/releases/v{{version}}/","categories":[]}]}
        """;

    private sealed class TestRepository : IDisposable
    {
        public string Path { get; }
        public string ManifestText { get; private set; } = string.Empty;

        private TestRepository(string path) => Path = path;

        public static TestRepository Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "botnexus-release-history-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            var result = new TestRepository(path);
            result.Run("init", "-b", "main");
            result.Run("config", "user.name", "BotNexus Test");
            result.Run("config", "user.email", "botnexus-test@botnexus.invalid");
            return result;
        }

        public string Commit(string message, string manifest)
        {
            ManifestText = manifest;
            var target = System.IO.Path.Combine(Path, "docs", "public", "releases", "release-history.json");
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            File.WriteAllText(target, manifest);
            Run("add", "--force", "docs/public/releases/release-history.json");
            Run("commit", "--allow-empty", "-m", message);
            return Run("rev-parse", "HEAD").Trim();
        }

        public string Run(params string[] arguments)
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = Path, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Git failed to start.");
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0) throw new InvalidOperationException(error);
            return output;
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "botnexus-release-history-invalid-" + Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
