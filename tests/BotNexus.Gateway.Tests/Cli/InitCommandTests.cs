using FluentAssertions;

namespace BotNexus.Gateway.Tests.Cli;

public sealed class InitCommandTests
{
    [Fact]
    public async Task Init_CreatesConfigAndRequiredDirectories()
    {
        await using var fixture = await CliTestFixture.CreateAsync();

        var result = await fixture.RunCliAsync("init");

        result.ExitCode.Should().Be(0);
        File.Exists(fixture.ConfigPath).Should().BeTrue();
        Directory.Exists(Path.Combine(fixture.RootPath, "extensions")).Should().BeTrue();
        Directory.Exists(Path.Combine(fixture.RootPath, "tokens")).Should().BeTrue();
        Directory.Exists(Path.Combine(fixture.RootPath, "sessions")).Should().BeTrue();
        Directory.Exists(Path.Combine(fixture.RootPath, "logs")).Should().BeTrue();
        Directory.Exists(Path.Combine(fixture.RootPath, "agents")).Should().BeTrue();

        var config = await fixture.LoadConfigAsync();
        config.Agents.Should().ContainKey("assistant");
        config.Agents!["assistant"].Provider.Should().Be("copilot");
    }

    [Fact]
    public async Task Init_WithoutForce_DoesNotOverwriteExistingConfig()
    {
        await using var fixture = await CliTestFixture.CreateAsync("""{"gateway":{"listenUrl":"http://localhost:5999"}}""");

        var result = await fixture.RunCliAsync("init");
        var config = await fixture.LoadConfigAsync();

        result.ExitCode.Should().Be(0);
        result.CombinedOutput.Should().Contain("Use --force to overwrite");
        config.Gateway?.ListenUrl.Should().Be("http://localhost:5999");
    }

    [Fact]
    public async Task Init_WithForce_OverwritesExistingConfig()
    {
        await using var fixture = await CliTestFixture.CreateAsync("""{"gateway":{"listenUrl":"http://localhost:5999"}}""");

        var result = await fixture.RunCliAsync("init", "--force");
        var config = await fixture.LoadConfigAsync();

        result.ExitCode.Should().Be(0);
        config.Gateway?.ListenUrl.Should().Be("http://localhost:5005");
        config.Agents.Should().ContainKey("assistant");
    }
}

