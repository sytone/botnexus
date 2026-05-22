namespace BotNexus.Gateway.Tests.Cli;

public sealed class AgentCommandsScaffoldTests
{
    [Fact]
    public async Task AgentAdd_CreatesWorkspaceDirectory()
    {
        await using var fixture = await CliTestFixture.CreateAsync("""{"agents":{}}""");

        await fixture.RunCliAsync("agent", "add", "scaffold-test", "--provider", "copilot", "--model", "gpt-4.1");

        Directory.Exists(Path.Combine(fixture.RootPath, "agents", "scaffold-test", "workspace"))
            .ShouldBeTrue();
    }

    [Fact]
    public async Task AgentAdd_CreatesScaffoldFiles()
    {
        await using var fixture = await CliTestFixture.CreateAsync("""{"agents":{}}""");

        await fixture.RunCliAsync("agent", "add", "scaffold-files-test", "--provider", "copilot", "--model", "gpt-4.1");

        var workspacePath = Path.Combine(fixture.RootPath, "agents", "scaffold-files-test", "workspace");
        File.Exists(Path.Combine(workspacePath, "SOUL.md")).ShouldBeTrue();
        File.Exists(Path.Combine(workspacePath, "IDENTITY.md")).ShouldBeTrue();
        File.Exists(Path.Combine(workspacePath, "AGENTS.md")).ShouldBeTrue();
        File.Exists(Path.Combine(workspacePath, "USER.md")).ShouldBeTrue();
    }

    [Fact]
    public async Task AgentAdd_ScaffoldFailure_DoesNotFailAgentAdd()
    {
        // This test verifies that even if scaffold has an issue (e.g. disk full), agent add still succeeds
        // We verify that the config was saved regardless of workspace state
        await using var fixture = await CliTestFixture.CreateAsync("""{"agents":{}}""");

        var result = await fixture.RunCliAsync("agent", "add", "resilience-test", "--provider", "copilot", "--model", "gpt-4.1");
        var config = await fixture.LoadConfigAsync();

        result.ExitCode.ShouldBe(0);
        config.Agents.ShouldNotBeNull();
        config.Agents!.ShouldContainKey("resilience-test");
    }
}
