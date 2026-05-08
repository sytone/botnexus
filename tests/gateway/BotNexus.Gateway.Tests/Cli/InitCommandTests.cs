
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
        config.Agents.ShouldContainKey("assistant");
        config.Agents!["assistant"].Provider.ShouldBe("github-copilot");
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

    [Fact]
    public async Task Init_WithForce_OverwritesExistingConfig()
    {
        await using var fixture = await CliTestFixture.CreateAsync("""{"gateway":{"listenUrl":"http://localhost:5999"}}""");

        var result = await fixture.RunCliAsync("init", "--force");
        var config = await fixture.LoadConfigAsync();

        result.ExitCode.ShouldBe(0);
        config.Gateway?.ListenUrl.ShouldBe("http://0.0.0.0:5005");
        config.Agents.ShouldContainKey("assistant");
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

