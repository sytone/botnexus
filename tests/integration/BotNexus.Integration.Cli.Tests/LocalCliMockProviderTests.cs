using System.Text.Json;

namespace BotNexus.Integration.Cli.Tests;

/// <summary>
/// End-to-end harness validation: packs and installs the in-tree CLI, then drives
/// it through the integration-mock provider bootstrap flow via the new
/// non-interactive <c>provider add</c> command.
///
/// This is the primary regression net for PR-time CLI changes that touch
/// install/init/provider plumbing or the integration-mock provider.
/// </summary>
[Collection(LocalCliCollection.Name)]
public sealed class LocalCliMockProviderTests : IAsyncLifetime
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(2);

    private readonly LocalCliInstallFixture _fixture;
    private string _home = string.Empty;

    public LocalCliMockProviderTests(LocalCliInstallFixture fixture) => _fixture = fixture;

    public Task InitializeAsync()
    {
        _home = Path.Combine(Path.GetTempPath(), "botnexus-local-cli-home", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_home);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_home))
                Directory.Delete(_home, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
        return Task.CompletedTask;
    }

    [Fact]
    public void LocalPackAndInstall_ProducesUsableBinary()
    {
        AssertFixture();
        File.Exists(_fixture.CliExecutablePath).ShouldBeTrue(
            $"Expected CLI binary at {_fixture.CliExecutablePath}.");
    }

    [Fact]
    public async Task Init_ThenProviderAdd_MockProvider_WritesExpectedConfig()
    {
        AssertFixture();

        // 1. init the sandboxed home
        var initResult = await ProcessRunner.RunAsync(
            _fixture.CliExecutablePath,
            $"init --target \"{_home}\"",
            environment: new Dictionary<string, string?> { ["BOTNEXUS_HOME"] = null },
            timeout: CommandTimeout);
        initResult.ExitCode.ShouldBe(0,
            $"init failed.\nStdOut:\n{initResult.StdOut}\nStdErr:\n{initResult.StdErr}");

        // 2. non-interactive provider add for the integration-mock provider
        var addResult = await ProcessRunner.RunAsync(
            _fixture.CliExecutablePath,
            $"provider add --name integration-mock --api integration-mock --default-model integration-mock-echo --target \"{_home}\"",
            environment: new Dictionary<string, string?> { ["BOTNEXUS_HOME"] = null },
            timeout: CommandTimeout);
        addResult.ExitCode.ShouldBe(0,
            $"provider add failed.\nStdOut:\n{addResult.StdOut}\nStdErr:\n{addResult.StdErr}");

        // 3. assert the persisted config matches the contract
        var configPath = Path.Combine(_home, "config.json");
        File.Exists(configPath).ShouldBeTrue($"Expected config.json at {configPath}.");

        await using var stream = File.OpenRead(configPath);
        using var doc = await JsonDocument.ParseAsync(stream);
        var providers = doc.RootElement.GetProperty("providers");
        providers.TryGetProperty("integration-mock", out var prov).ShouldBeTrue(
            "providers.integration-mock missing from config.json.");

        prov.GetProperty("enabled").GetBoolean().ShouldBeTrue();
        prov.GetProperty("api").GetString().ShouldBe("integration-mock");
        prov.GetProperty("defaultModel").GetString().ShouldBe("integration-mock-echo");

        // 4. the CLI itself must see the provider via `provider list`
        var listResult = await ProcessRunner.RunAsync(
            _fixture.CliExecutablePath,
            $"provider list --target \"{_home}\"",
            environment: new Dictionary<string, string?> { ["BOTNEXUS_HOME"] = null },
            timeout: CommandTimeout);
        listResult.ExitCode.ShouldBe(0,
            $"provider list failed.\nStdOut:\n{listResult.StdOut}\nStdErr:\n{listResult.StdErr}");
        listResult.Combined.ShouldContain("integration-mock",
            customMessage: $"provider list did not surface the just-added provider.\nStdOut:\n{listResult.StdOut}");
        listResult.Combined.ShouldContain("integration-mock-echo",
            customMessage: $"provider list did not surface the default model.\nStdOut:\n{listResult.StdOut}");
    }

    [Fact]
    public async Task ProviderRemove_IsIdempotent()
    {
        AssertFixture();

        // Init first so we have a config to operate on.
        var initResult = await ProcessRunner.RunAsync(
            _fixture.CliExecutablePath,
            $"init --target \"{_home}\"",
            environment: new Dictionary<string, string?> { ["BOTNEXUS_HOME"] = null },
            timeout: CommandTimeout);
        initResult.ExitCode.ShouldBe(0);

        // Removing a never-added provider must still return 0.
        var removeResult = await ProcessRunner.RunAsync(
            _fixture.CliExecutablePath,
            $"provider remove --name never-existed --target \"{_home}\"",
            environment: new Dictionary<string, string?> { ["BOTNEXUS_HOME"] = null },
            timeout: CommandTimeout);
        removeResult.ExitCode.ShouldBe(0,
            $"provider remove must be idempotent.\nStdOut:\n{removeResult.StdOut}\nStdErr:\n{removeResult.StdErr}");
    }

    private void AssertFixture()
    {
        _fixture.Succeeded.ShouldBeTrue(
            $"Local pack/install fixture did not succeed.\n" +
            $"PackExitCode={_fixture.PackExitCode}\nInstallExitCode={_fixture.InstallExitCode}\n" +
            $"PackOutput:\n{_fixture.PackOutput}\n\nInstallOutput:\n{_fixture.InstallOutput}\n\nError:\n{_fixture.Error}");
    }
}
