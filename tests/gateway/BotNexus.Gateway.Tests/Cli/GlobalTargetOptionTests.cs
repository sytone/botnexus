namespace BotNexus.Gateway.Tests.Cli;

/// <summary>
/// Tests for the global <c>--target</c> option on the root CLI command.
/// Verifies that placing <c>--target</c> before any subcommand correctly routes
/// all commands to the specified BotNexus home directory.
/// </summary>
public sealed class GlobalTargetOptionTests
{
    [Fact]
    public async Task GlobalTarget_Validate_RoutesToCorrectHomeDirectory()
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

        // Use --target before the subcommand (global option style)
        var result = await fixture.RunCliWithTargetFlagAsync("validate");

        result.ExitCode.ShouldBe(0);
        result.StdOut.ShouldContain("Result: VALID");
    }

    [Fact]
    public async Task GlobalTarget_AgentList_RoutesToCorrectHomeDirectory()
    {
        await using var fixture = await CliTestFixture.CreateAsync("""
            {
              "agents": {
                "bot-dev": {
                  "provider": "copilot",
                  "model": "gpt-4.1",
                  "enabled": true
                }
              }
            }
            """);

        var result = await fixture.RunCliWithTargetFlagAsync("agent", "list");

        result.ExitCode.ShouldBe(0);
        result.StdOut.ShouldContain("bot-dev");
    }

    [Fact]
    public async Task GlobalTarget_Init_RoutesToCorrectHomeDirectory()
    {
        await using var fixture = await CliTestFixture.CreateAsync();

        var result = await fixture.RunCliWithTargetFlagAsync("init");

        result.ExitCode.ShouldBe(0);
        File.Exists(fixture.ConfigPath).ShouldBeTrue();
    }

    [Fact]
    public async Task GlobalTarget_ConfigGet_RoutesToCorrectHomeDirectory()
    {
        await using var fixture = await CliTestFixture.CreateAsync("""
            {
              "gateway": { "listenUrl": "http://0.0.0.0:9999" },
              "agents": {}
            }
            """);

        var result = await fixture.RunCliWithTargetFlagAsync("config", "get", "gateway.listenUrl");

        result.ExitCode.ShouldBe(0);
        result.StdOut.ShouldContain("9999");
    }

    [Fact]
    public async Task GlobalTarget_AgentAdd_WritesToCorrectHomeDirectory()
    {
        await using var fixture = await CliTestFixture.CreateAsync("""{"agents":{}}""");

        var result = await fixture.RunCliWithTargetFlagAsync("agent", "add", "my-agent", "--provider", "copilot", "--model", "gpt-4.1");
        var config = await fixture.LoadConfigAsync();

        result.ExitCode.ShouldBe(0);
        config.Agents.ShouldNotBeNull();
        config.Agents!.ShouldContainKey("my-agent");
    }
}
