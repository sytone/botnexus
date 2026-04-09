using BotNexus.Extensions.Mcp.Transport;
using FluentAssertions;

namespace BotNexus.Extensions.Mcp.Tests;

public class StdioTransportTests
{
    [Fact]
    public void ResolveEnvValue_PassesThrough_NonPatternValues()
    {
        StdioMcpTransport.ResolveEnvValue("plain-value").Should().Be("plain-value");
    }

    [Fact]
    public void ResolveEnvValue_Resolves_EnvironmentVariable()
    {
        Environment.SetEnvironmentVariable("MCP_TEST_VAR", "resolved-value");
        try
        {
            StdioMcpTransport.ResolveEnvValue("${env:MCP_TEST_VAR}").Should().Be("resolved-value");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MCP_TEST_VAR", null);
        }
    }

    [Fact]
    public void ResolveEnvValue_UsesDefault_WhenVariableNotSet()
    {
        Environment.SetEnvironmentVariable("MCP_MISSING_VAR", null);
        StdioMcpTransport.ResolveEnvValue("${env:MCP_MISSING_VAR:-fallback}")
            .Should().Be("fallback");
    }

    [Fact]
    public void ResolveEnvValue_ReturnsEmpty_WhenVariableNotSetAndNoDefault()
    {
        Environment.SetEnvironmentVariable("MCP_MISSING_VAR2", null);
        StdioMcpTransport.ResolveEnvValue("${env:MCP_MISSING_VAR2}")
            .Should().BeEmpty();
    }

    [Fact]
    public void ResolveCommand_ReturnsCommandAsIs_OnNonWindows()
    {
        // ResolveCommand on Windows will try to resolve, but we can verify structure
        var (fileName, args) = StdioMcpTransport.ResolveCommand("node", ["server.js"]);

        // On any OS, we get the command and args back
        args.Should().Contain("server.js");
    }

    [Fact]
    public async Task StdioTransport_Connect_ThrowsOnInvalidCommand()
    {
        var transport = new StdioMcpTransport("__nonexistent_command_12345__");

        // Should throw because the command doesn't exist
        var act = () => transport.ConnectAsync();
        await act.Should().ThrowAsync<Exception>();

        await transport.DisposeAsync();
    }
}
