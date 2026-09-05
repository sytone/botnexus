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

    /// <summary>
    /// #3237 AC1: every assembly the packed CLI binds against at startup must be present in the
    /// install layout. The fixture already fails by name when one is absent; this asserts the
    /// guard's conclusion directly so the requirement is visible as a test, not only as a
    /// precondition.
    /// </summary>
    [Fact]
    public void LocalInstallLayout_ContainsEveryStartupCriticalAssembly()
    {
        AssertFixture();

        var missing = CliInstallLayout.FindMissingAssemblies(_fixture.InstalledFiles);
        missing.ShouldBeEmpty(
            CliInstallLayout.FormatMissingAssemblyFailure(_fixture.ToolPath, missing, _fixture.InstalledFiles));
    }

    /// <summary>
    /// #3255: the pack that produced this fixture's package must have been isolated from the
    /// repo's shared build trees. Asserted against the LIVE fixture, so a real run proves the
    /// redirect took effect rather than only that the switch was typed.
    /// </summary>
    [Fact]
    public void LocalPack_WasIsolatedFromTheSharedRepoBuildTrees()
    {
        AssertFixture();

        CliPackIsolation.FindMissingIsolationSwitches(_fixture.PackArguments).ShouldBeEmpty(
            CliPackIsolation.DescribeIsolationFailure(_fixture.PackArguments, _fixture.PackArtifactsDir));
        CliPackIsolation.ArtifactsDirWasPopulated(_fixture.PackArtifactsDir).ShouldBeTrue(
            CliPackIsolation.DescribeIsolationFailure(_fixture.PackArguments, _fixture.PackArtifactsDir));
        _fixture.PackIsolationFailure.ShouldBeNull();
    }

    /// <summary>
    /// #3237 root-cause pin, asserted against the LIVE fixture: the synthetic <c>99.99.99</c> stamp
    /// must identify the package only, and every startup-critical assembly in the real install
    /// layout must carry the one repo assembly version the CLI was actually compiled against.
    /// Before this fix the pack stamped MSBuild <c>Version</c> too, so the CLI bound
    /// <c>99.99.99.0</c> while its dependencies could carry the repo version - the intermittent
    /// <c>Could not load file or assembly</c> this issue records.
    /// </summary>
    [Fact]
    public void LocalInstallLayout_BindsOneConsistentAssemblyVersion()
    {
        AssertFixture();

        _fixture.PackArguments.ShouldContain($"/p:PackageVersion={_fixture.PackVersion}");
        _fixture.PackArguments.ShouldNotContain("/p:Version=99.99.99",
            customMessage: "The synthetic stamp must not be MSBuild Version (#3237).\n" + _fixture.PackArguments);
        _fixture.ExpectedBoundVersion.ShouldBe(
            CliPackIsolation.ExpectedBoundVersion(_fixture.AssemblyVersion));

        var mismatches = CliInstallLayout.FindVersionMismatches(
            _fixture.ToolPath, _fixture.ExpectedBoundVersion);
        mismatches.ShouldBeEmpty(CliInstallLayout.FormatVersionMismatchFailure(
            _fixture.ToolPath, _fixture.ExpectedBoundVersion, mismatches, _fixture.InstalledFiles));
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
        initResult.ExitCode.ShouldBe(0, CliInstallLayout.FormatCliFailure(
            $"init --target {_home}", initResult.ExitCode, initResult.StdOut, initResult.StdErr,
            _fixture.ToolPath, _fixture.InstalledFiles));

        // 2. non-interactive provider add for the integration-mock provider
        var addResult = await ProcessRunner.RunAsync(
            _fixture.CliExecutablePath,
            $"provider add --name integration-mock --api integration-mock --default-model integration-mock-echo --target \"{_home}\"",
            environment: new Dictionary<string, string?> { ["BOTNEXUS_HOME"] = null },
            timeout: CommandTimeout);
        addResult.ExitCode.ShouldBe(0, CliInstallLayout.FormatCliFailure(
            "provider add --name integration-mock", addResult.ExitCode, addResult.StdOut, addResult.StdErr,
            _fixture.ToolPath, _fixture.InstalledFiles));

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
        listResult.ExitCode.ShouldBe(0, CliInstallLayout.FormatCliFailure(
            "provider list", listResult.ExitCode, listResult.StdOut, listResult.StdErr,
            _fixture.ToolPath, _fixture.InstalledFiles));
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
        initResult.ExitCode.ShouldBe(0, CliInstallLayout.FormatCliFailure(
            $"init --target {_home}", initResult.ExitCode, initResult.StdOut, initResult.StdErr,
            _fixture.ToolPath, _fixture.InstalledFiles));

        // Removing a never-added provider must still return 0.
        var removeResult = await ProcessRunner.RunAsync(
            _fixture.CliExecutablePath,
            $"provider remove --name never-existed --target \"{_home}\"",
            environment: new Dictionary<string, string?> { ["BOTNEXUS_HOME"] = null },
            timeout: CommandTimeout);
        removeResult.ExitCode.ShouldBe(0,
            "provider remove must be idempotent." + Environment.NewLine +
            CliInstallLayout.FormatCliFailure(
                "provider remove --name never-existed", removeResult.ExitCode, removeResult.StdOut,
                removeResult.StdErr, _fixture.ToolPath, _fixture.InstalledFiles));
    }

    private void AssertFixture()
    {
        _fixture.Succeeded.ShouldBeTrue(
            $"Local pack/install fixture did not succeed.\n" +
            $"PackExitCode={_fixture.PackExitCode}\nInstallExitCode={_fixture.InstallExitCode}\n" +
            (_fixture.LayoutFailure is { } layout ? layout + "\n\n" : string.Empty) +
            (_fixture.PackIsolationFailure is { } iso ? iso + "\n\n" : string.Empty) +
            $"PackOutput:\n{_fixture.PackOutput}\n\nInstallOutput:\n{_fixture.InstallOutput}\n\nError:\n{_fixture.Error}");
    }
}
