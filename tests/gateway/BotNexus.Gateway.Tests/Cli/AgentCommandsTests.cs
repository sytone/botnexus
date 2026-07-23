
namespace BotNexus.Gateway.Tests.Cli;

public sealed class AgentCommandsTests
{
    [Fact]
    public async Task AgentList_WithConfiguredAgents_ReturnsZero()
    {
        await using var fixture = await CliTestFixture.CreateAsync("""
            {
              "agents": {
                "assistant": {
                  "provider": "copilot",
                  "model": "gpt-4.1",
                  "enabled": true
                }
              }
            }
            """);

        var result = await fixture.RunCliAsync("agent", "list");

        result.ExitCode.ShouldBe(0);
        result.StdOut.ShouldContain("assistant");
        result.StdOut.ShouldContain("copilot");
    }

    [Fact]
    public async Task AgentAdd_AddsAgentAndReturnsZero()
    {
        await using var fixture = await CliTestFixture.CreateAsync("""{"agents":{}}""");

        // #2136: 'reviewer' is a reserved sub-agent archetype id, so a non-reserved id is used here.
        var result = await fixture.RunCliAsync("agent", "add", "helper", "--provider", "copilot", "--model", "gpt-5", "--enabled", "true");
        var config = await fixture.LoadConfigAsync();

        result.ExitCode.ShouldBe(0);
        config.Agents.ShouldNotBeNull();
        var agents = config.Agents ?? throw new InvalidOperationException("Expected agents config.");
        agents.ShouldContainKey("helper");
        agents["helper"].Model.ShouldBe("gpt-5");
    }

    [Fact]
    public async Task AgentAdd_ScaffoldsWorkspaceAfterAdd()
    {
        await using var fixture = await CliTestFixture.CreateAsync("""{ "agents": {} }""");

        var result = await fixture.RunCliAsync("agent", "add", "my-bot", "--provider", "copilot", "--model", "gpt-4.1");

        result.ExitCode.ShouldBe(0);
        var workspacePath = Path.Combine(fixture.RootPath, "agents", "my-bot", "workspace");
        Directory.Exists(workspacePath).ShouldBeTrue("workspace directory should be created");
        File.Exists(Path.Combine(workspacePath, "SOUL.md")).ShouldBeTrue("SOUL.md should be scaffolded");
    }

    [Fact]
    public async Task AgentAdd_WhenAgentExists_ReturnsOne()
    {
        await using var fixture = await CliTestFixture.CreateAsync("""
            {
              "agents": {
                "assistant": {
                  "provider": "copilot",
                  "model": "gpt-4.1"
                }
              }
            }
            """);

        var result = await fixture.RunCliAsync("agent", "add", "assistant");

        result.ExitCode.ShouldBe(1);
        result.CombinedOutput.ShouldContain("already exists");
    }

    [Fact]
    public async Task AgentRemove_RemovesAgentAndReturnsZero()
    {
        await using var fixture = await CliTestFixture.CreateAsync("""
            {
              "agents": {
                "assistant": {
                  "provider": "copilot",
                  "model": "gpt-4.1"
                },
                "reviewer": {
                  "provider": "copilot",
                  "model": "gpt-5"
                }
              }
            }
            """);

        var result = await fixture.RunCliAsync("agent", "remove", "reviewer");
        var config = await fixture.LoadConfigAsync();

        result.ExitCode.ShouldBe(0);
        config.Agents.ShouldNotBeNull();
        var agents = config.Agents ?? throw new InvalidOperationException("Expected agents config.");
        agents.ShouldNotContainKey("reviewer");
    }

    [Fact]
    public async Task AgentRemove_WithMissingConfig_ReturnsOne()
    {
        await using var fixture = await CliTestFixture.CreateAsync();

        var result = await fixture.RunCliAsync("agent", "remove", "assistant");

        result.ExitCode.ShouldBe(1);
        result.CombinedOutput.ShouldContain("config file not found");
    }
}
