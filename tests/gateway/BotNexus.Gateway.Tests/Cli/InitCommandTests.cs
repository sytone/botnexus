
using System.Text.Json;

namespace BotNexus.Gateway.Tests.Cli;

public sealed class InitCommandTests
{
    [Fact]
    public async Task Init_CreatesConfigAndRequiredDirectories()
    {
        await using var fixture = await CliTestFixture.CreateAsync();

        var result = await fixture.RunCliAsync("init");

        result.ExitCode.ShouldBe(0);
        File.Exists(fixture.ConfigPath).ShouldBeTrue();
        Directory.Exists(Path.Combine(fixture.RootPath, "extensions")).ShouldBeTrue();
        Directory.Exists(Path.Combine(fixture.RootPath, "tokens")).ShouldBeTrue();
        Directory.Exists(Path.Combine(fixture.RootPath, "sessions")).ShouldBeTrue();
        Directory.Exists(Path.Combine(fixture.RootPath, "logs")).ShouldBeTrue();
        Directory.Exists(Path.Combine(fixture.RootPath, "agents")).ShouldBeTrue();

        var config = await fixture.LoadConfigAsync();
        config.Agents.ShouldNotBeNull();
        var agents = config.Agents ?? throw new InvalidOperationException("Expected agents config.");
        agents.ShouldContainKey("assistant");
        agents["assistant"].Provider.ShouldBe("github-copilot");
    }

    [Fact]
    public async Task Init_WithoutForce_DoesNotOverwriteExistingConfig()
    {
        await using var fixture = await CliTestFixture.CreateAsync("""{"gateway":{"listenUrl":"http://localhost:5999"}}""");

        var result = await fixture.RunCliAsync("init");
        var config = await fixture.LoadConfigAsync();

        result.ExitCode.ShouldBe(0);
        result.CombinedOutput.ShouldContain("Config already exists");
        config.Gateway?.ListenUrl.ShouldBe("http://localhost:5999");
    }

    /// <summary>
    /// Asserts that <c>init --force</c> overwrites an existing config with a freshly generated one.
    ///
    /// #2798: the listenUrl expectation here was INVERTED, not added. It previously asserted
    /// <c>"http://0.0.0.0:5005"</c> — a second, independent pin of the #96 wildcard default,
    /// duplicating the one in <c>BotNexus.Cli.Tests</c>. The inputs are preserved verbatim (an
    /// existing config on a non-default port, one <c>--force</c> run through the REAL CLI process);
    /// only the expected generated value moved to loopback. This test is the stronger of the two
    /// AC1 pins because it exercises the shipped command end-to-end rather than the class directly.
    /// If it fails, the fix is in InitCommand, not here.
    /// </summary>
    [Fact]
    public async Task Init_WithForce_OverwritesExistingConfig()
    {
        await using var fixture = await CliTestFixture.CreateAsync("""{"gateway":{"listenUrl":"http://localhost:5999"}}""");

        var result = await fixture.RunCliAsync("init", "--force");
        var config = await fixture.LoadConfigAsync();

        result.ExitCode.ShouldBe(0);
        config.Gateway?.ListenUrl.ShouldBe("http://localhost:5005");
        config.Agents.ShouldNotBeNull();
        var agents = config.Agents ?? throw new InvalidOperationException("Expected agents config.");
        agents.ShouldContainKey("assistant");
    }

    /// <summary>
    /// #2798 AC2, end-to-end: the explicit opt-in flag produces the wildcard listenUrl through the
    /// real CLI, byte-identical in that field to what init generated before #2798. Pairs with the
    /// inverted test above — together they pin that the capability moved from silent default to
    /// stated choice rather than being removed.
    /// </summary>
    [Fact]
    public async Task Init_WithListenAllInterfaces_WritesWildcardListenUrl()
    {
        await using var fixture = await CliTestFixture.CreateAsync("""{"gateway":{"listenUrl":"http://localhost:5999"}}""");

        var result = await fixture.RunCliAsync("init", "--force", "--listen-all-interfaces");
        var config = await fixture.LoadConfigAsync();

        result.ExitCode.ShouldBe(0);
        config.Gateway?.ListenUrl.ShouldBe("http://0.0.0.0:5005");
    }

    // -------------------------------------------------------------------------
    // Issue #12: InitCommand scaffold — agents.defaults and cron (scenario 11)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Init_ScaffoldEmitsCronEnabledTrue()
    {
        await using var fixture = await CliTestFixture.CreateAsync();

        var result = await fixture.RunCliAsync("init");
        var rawJson = await File.ReadAllTextAsync(fixture.ConfigPath);

        result.ExitCode.ShouldBe(0);
        // cron.enabled defaults to true in the C# model; init scaffold should emit it
        using var doc = JsonDocument.Parse(rawJson);
        doc.RootElement.TryGetProperty("cron", out var cronEl).ShouldBeTrue();
        cronEl.TryGetProperty("enabled", out var cronEnabledEl).ShouldBeTrue();
        cronEnabledEl.GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Init_ScaffoldEmitsAgentsDefaultsMemoryEnabledTrue()
    {
        await using var fixture = await CliTestFixture.CreateAsync();

        var result = await fixture.RunCliAsync("init");
        var rawJson = await File.ReadAllTextAsync(fixture.ConfigPath);

        result.ExitCode.ShouldBe(0);
        // agents.defaults block with memory.enabled = true must be present
        using var doc = JsonDocument.Parse(rawJson);
        doc.RootElement.TryGetProperty("agents", out var agentsEl).ShouldBeTrue();
        agentsEl.TryGetProperty("defaults", out var defaultsEl).ShouldBeTrue();
        defaultsEl.TryGetProperty("memory", out var memoryEl).ShouldBeTrue();
        memoryEl.TryGetProperty("enabled", out var enabledEl).ShouldBeTrue();
        enabledEl.GetBoolean().ShouldBeTrue();
    }
}
