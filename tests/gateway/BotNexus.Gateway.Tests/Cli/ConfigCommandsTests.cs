
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

        getResult.ExitCode.ShouldBe(0);
        getResult.StdOut.Trim().ShouldBe("http://localhost:5005");
        setResult.ExitCode.ShouldBe(0);
        schemaResult.ExitCode.ShouldBe(0);
        File.Exists(schemaPath).ShouldBeTrue();

        var config = await fixture.LoadConfigAsync();
        config.Gateway?.DefaultAgentId.ShouldBe("reviewer");
    }

    [Fact]
    public async Task ConfigGet_WithMissingConfig_ReturnsOne()
    {
        await using var fixture = await CliTestFixture.CreateAsync();

        var result = await fixture.RunCliAsync("config", "get", "gateway.listenUrl");

        result.ExitCode.ShouldBe(1);
        result.CombinedOutput.ShouldContain("config file not found");
    }

    [Fact]
    public async Task ConfigSet_WithInvalidPath_ReturnsOne()
    {
        await using var fixture = await CliTestFixture.CreateAsync("""{"gateway":{"listenUrl":"http://localhost:5005"}}""");

        var result = await fixture.RunCliAsync("config", "set", "gateway.invalidPath", "value");

        result.ExitCode.ShouldBe(1);
        result.CombinedOutput.ShouldContain("Property 'invalidPath' does not exist");
    }
}

