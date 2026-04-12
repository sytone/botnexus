using FluentAssertions;

namespace BotNexus.Gateway.Tests.Cli;

public sealed class ConfigCommandsTests
{
    [Fact]
    public async Task ConfigGetGetSetAndSchema_ReturnExpectedExitCodes()
    {
        await using var fixture = await CliTestFixture.CreateAsync("""
            {
              "gateway": {
                "listenUrl": "http://localhost:5005",
                "defaultAgentId": "assistant"
              },
              "agents": {
                "assistant": {
                  "provider": "copilot",
                  "model": "gpt-4.1"
                }
              }
            }
            """);

        var getResult = await fixture.RunCliAsync("config", "get", "gateway.listenUrl");
        var setResult = await fixture.RunCliAsync("config", "set", "gateway.defaultAgentId", "reviewer");
        var schemaPath = Path.Combine(fixture.RootPath, "schema", "botnexus-config.schema.json");
        var schemaResult = await fixture.RunCliAsync("config", "schema", "--output", schemaPath);

        getResult.ExitCode.Should().Be(0);
        getResult.StdOut.Trim().Should().Be("http://localhost:5005");
        setResult.ExitCode.Should().Be(0);
        schemaResult.ExitCode.Should().Be(0);
        File.Exists(schemaPath).Should().BeTrue();

        var config = await fixture.LoadConfigAsync();
        config.Gateway?.DefaultAgentId.Should().Be("reviewer");
    }

    [Fact]
    public async Task ConfigGet_WithMissingConfig_ReturnsOne()
    {
        await using var fixture = await CliTestFixture.CreateAsync();

        var result = await fixture.RunCliAsync("config", "get", "gateway.listenUrl");

        result.ExitCode.Should().Be(1);
        result.CombinedOutput.Should().Contain("config file not found");
    }

    [Fact]
    public async Task ConfigSet_WithInvalidPath_ReturnsOne()
    {
        await using var fixture = await CliTestFixture.CreateAsync("""{"gateway":{"listenUrl":"http://localhost:5005"}}""");

        var result = await fixture.RunCliAsync("config", "set", "gateway.invalidPath", "value");

        result.ExitCode.Should().Be(1);
        result.CombinedOutput.Should().Contain("Property 'invalidPath' does not exist");
    }
}

