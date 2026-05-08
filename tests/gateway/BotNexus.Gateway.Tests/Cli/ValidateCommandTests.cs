
namespace BotNexus.Gateway.Tests.Cli;

public sealed class ValidateCommandTests
{
    [Fact]
    public async Task Validate_WithValidConfig_ReturnsZero()
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

        var result = await fixture.RunCliAsync("validate");

        result.ExitCode.ShouldBe(0);
        result.StdOut.ShouldContain("Result: VALID");
    }

    [Fact]
    public async Task Validate_WithInvalidConfig_ReturnsOne()
    {
        await using var fixture = await CliTestFixture.CreateAsync("""
            {
              "agents": {
                "assistant": {
                  "provider": "",
                  "model": ""
                }
              }
            }
            """);

        var result = await fixture.RunCliAsync("validate");

        result.ExitCode.ShouldBe(1);
        result.CombinedOutput.ShouldContain("agents.assistant.provider");
        result.CombinedOutput.ShouldContain("agents.assistant.model");
    }

    [Fact]
    public async Task Validate_WithMissingConfig_ReturnsOne()
    {
        await using var fixture = await CliTestFixture.CreateAsync();

        var result = await fixture.RunCliAsync("validate");

        result.ExitCode.ShouldBe(1);
        result.CombinedOutput.ShouldContain("Config file not found");
    }
}
