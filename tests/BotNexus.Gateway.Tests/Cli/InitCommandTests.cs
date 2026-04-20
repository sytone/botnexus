
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
        result.CombinedOutput.ShouldContain("Use --force to overwrite");
        config.Gateway?.ListenUrl.ShouldBe("http://localhost:5999");
    }

    [Fact]
    public async Task Init_WithForce_OverwritesExistingConfig()
    {
        await using var fixture = await CliTestFixture.CreateAsync("""{"gateway":{"listenUrl":"http://localhost:5999"}}""");

        var result = await fixture.RunCliAsync("init", "--force");
        var config = await fixture.LoadConfigAsync();

        result.ExitCode.ShouldBe(0);
        config.Gateway?.ListenUrl.ShouldBe("http://localhost:5005");
        config.Agents.ShouldContainKey("assistant");
    }
}

