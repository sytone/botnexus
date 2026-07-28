using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Options;

namespace BotNexus.Integration.ConfigDiskE2E.Tests;

/// <summary>
/// Rejected-validation and malformed-file behaviour observed against a physical config file.
/// </summary>
/// <remarks>
/// The contract these tests pin is deliberately unglamorous: the platform validates on
/// <em>load</em>, not on write, and a config file that fails validation must degrade rather than
/// take the process down. Asserting that on a real file matters because the failure mode being
/// guarded against - a background reload throwing on the configuration provider's thread - only
/// exists when a real watcher is reading real bytes.
/// </remarks>
public sealed class ConfigValidationRejectionDiskTests
{
    /// <summary>
    /// A structurally invalid value written to disk must be rejected by the production loader
    /// with a validating load, and the invalid document must still be present on disk for an
    /// operator to repair (never silently rewritten).
    /// </summary>
    [Fact]
    public async Task InvalidValue_IsRejectedByValidatingLoad_AndFileIsPreserved()
    {
        using var home = new ConfigHomeFixture(MaximalConfig.Json);

        await home.Writer.UpdateSectionAsync(
            "gateway",
            new JsonObject { ["listenUrl"] = "not-a-url" });

        var failure = Should.Throw<OptionsValidationException>(
            () => PlatformConfigLoader.Load(home.ConfigPath, validateOnLoad: true));
        failure.Failures.ShouldContain(f => f.Contains("gateway.listenUrl", StringComparison.Ordinal));

        home.ReadFromDisk()["gateway"]!["listenUrl"]!.GetValue<string>().ShouldBe("not-a-url");
    }

    /// <summary>
    /// The same invalid document must still load with validation disabled - the startup path
    /// Program.cs uses so a bad config yields a degraded gateway rather than a boot loop.
    /// </summary>
    [Fact]
    public async Task InvalidValue_StillLoadsWhenValidationIsDisabled()
    {
        using var home = new ConfigHomeFixture(MaximalConfig.Json);

        await home.Writer.UpdateSectionAsync(
            "gateway",
            new JsonObject { ["listenUrl"] = "not-a-url" });

        var config = PlatformConfigLoader.Load(home.ConfigPath, validateOnLoad: false);
        config.Gateway!.ListenUrl.ShouldBe("not-a-url");
        config.Providers!.Count.ShouldBe(2);
    }

    /// <summary>
    /// An error scoped to a single named agent must be quarantined rather than failing the global
    /// options result (#2102): a runtime consumer reading a config whose one agent is malformed
    /// must still get the healthy agents, not an exception on every read.
    /// </summary>
    [Fact]
    public async Task MalformedNamedAgent_DoesNotFailTheRuntimeOptionsResult()
    {
        using var home = new ConfigHomeFixture(MaximalConfig.Json);

        await home.Writer.MutateAsync(
            root => root["agents"]!["builder"]!["model"] = string.Empty,
            "test-malformed-agent");

        using var consumer = home.BuildRuntimeConsumer();
        var config = Should.NotThrow(() => consumer.Monitor.CurrentValue);
        config.Agents!.ShouldContainKey("assistant");
    }

    /// <summary>
    /// An invalid <c>agents.defaults</c> seeds every agent and therefore must NOT be quarantined -
    /// it fails the options result hard. Pinning the asymmetry stops a future "just quarantine
    /// everything agent-shaped" simplification from silently disabling this guard.
    /// </summary>
    [Fact]
    public async Task MalformedAgentsDefaults_FailsTheRuntimeOptionsResult()
    {
        using var home = new ConfigHomeFixture(MaximalConfig.Json);

        await home.Writer.MutateAsync(
            root => root["agents"]!["defaults"]!["memory"]!["promptInjection"] = "not-a-mode",
            "test-malformed-defaults");

        using var consumer = home.BuildRuntimeConsumer();
        Should.Throw<OptionsValidationException>(() => consumer.Monitor.CurrentValue);
    }

    /// <summary>
    /// A config file that is not valid JSON must fail the load with a diagnosable
    /// <see cref="OptionsValidationException"/> naming the file and the parse position - not an
    /// opaque <c>JsonException</c> escaping from deep inside the loader - and the unreadable file
    /// must be left on disk untouched for an operator to repair.
    /// </summary>
    /// <remarks>
    /// Hand-corrupting the file is only possible when there is a real file, which is why the
    /// mock-filesystem suite never covered this at all. The complementary guarantee - that a
    /// runtime consumer survives the same corruption rather than crashing the host - is asserted
    /// separately below.
    /// </remarks>
    [Fact]
    public void MalformedJsonOnDisk_FailsLoadWithADiagnosableError()
    {
        using var home = new ConfigHomeFixture(MaximalConfig.Json);
        File.WriteAllText(home.ConfigPath, "{ this is not json ");

        var failure = Should.Throw<OptionsValidationException>(
            () => PlatformConfigLoader.Load(home.ConfigPath, validateOnLoad: false));
        failure.Failures.ShouldContain(f => f.Contains("Invalid JSON", StringComparison.Ordinal));
        failure.Failures.ShouldContain(f => f.Contains(home.ConfigPath, StringComparison.Ordinal));

        home.ReadRawText().ShouldBe("{ this is not json ");
    }

    /// <summary>
    /// A runtime consumer bound to a corrupted config file <em>should</em> degrade to its
    /// last-known-good configuration rather than throwing. Today it does not: the JSON
    /// configuration provider wraps the parse failure in <see cref="InvalidDataException"/> and
    /// rethrows, which on a watcher-driven reload lands on a background thread and can take the
    /// host down. Filed as #2358.
    /// </summary>
    /// <remarks>
    /// This test pins the <em>observed</em> behaviour deliberately, and will fail the moment
    /// #2358 is fixed - at which point the assertion should be inverted to
    /// <c>Should.NotThrow</c> and the consumer asserted to still hold the pre-corruption
    /// providers. The production fix cannot land here: it touches config-source registration
    /// owned by open PRs #2352 / #2348.
    /// </remarks>
    [Fact]
    public void MalformedJsonOnDisk_CurrentlyThrowsOnReload_Pinned2358()
    {
        using var home = new ConfigHomeFixture(MaximalConfig.Json);
        using var consumer = home.BuildRuntimeConsumer();
        consumer.Monitor.CurrentValue.Providers!.ShouldContainKey("github-copilot");

        File.WriteAllText(home.ConfigPath, "{ this is not json ");

        var failure = Should.Throw<InvalidDataException>(() => consumer.ReloadNow());
        failure.Message.ShouldContain("config.json", Case.Insensitive);
    }

    /// <summary>
    /// After the file has been corrupted, the most recent valid backup must be recoverable from
    /// the physical backups directory the writer populated.
    /// </summary>
    [Fact]
    public async Task CorruptedConfig_IsRecoverableFromThePhysicalBackupDirectory()
    {
        using var home = new ConfigHomeFixture(MaximalConfig.Json);

        // Produce a real backup by making an effective write.
        await home.Writer.MutateAsync(
            root => root["cron"]!["tickIntervalSeconds"] = 90,
            "test-before-corruption");
        home.ListBackups().Count.ShouldBe(1);

        File.WriteAllText(home.ConfigPath, "{ corrupted ");

        var recovered = PlatformConfigLoader.TryRecoverFromBackup(home.ConfigPath, out var recoveredPath);
        recovered.ShouldNotBeNull();
        recoveredPath.ShouldNotBeNull();
        recovered!.Providers!.ShouldContainKey("github-copilot");
    }
}
