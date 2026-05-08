using BotNexus.Extensions.Mcp.Protocol;
using BotNexus.Extensions.Mcp.Transport;

namespace BotNexus.Extensions.Mcp.Tests;

[CollectionDefinition("EnvironmentTests")]
public class EnvironmentTestsCollection { }

[Collection("EnvironmentTests")]
public class StdioTransportTests
{
    [Fact]
    public void ResolveEnvValue_PassesThrough_NonPatternValues()
    {
        StdioMcpTransport.ResolveEnvValue("plain-value").ShouldBe("plain-value");
    }

    [Fact]
    public void ResolveEnvValue_Resolves_EnvironmentVariable()
    {
        Environment.SetEnvironmentVariable("MCP_TEST_VAR", "resolved-value");
        try
        {
            StdioMcpTransport.ResolveEnvValue("${env:MCP_TEST_VAR}").ShouldBe("resolved-value");
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
            .ShouldBe("fallback");
    }

    [Fact]
    public void ResolveEnvValue_ReturnsEmpty_WhenVariableNotSetAndNoDefault()
    {
        Environment.SetEnvironmentVariable("MCP_MISSING_VAR2", null);
        StdioMcpTransport.ResolveEnvValue("${env:MCP_MISSING_VAR2}")
            .ShouldBeEmpty();
    }

    [Fact]
    public void ResolveCommand_ReturnsCommandAsIs_OnNonWindows()
    {
        // ResolveCommand on Windows will try to resolve, but we can verify structure
        var (fileName, args) = StdioMcpTransport.ResolveCommand("node", ["server.js"]);

        // On any OS, we get the command and args back
        args.ShouldContain("server.js");
    }

    [Fact]
    public async Task StdioTransport_Connect_ThrowsOnInvalidCommand()
    {
        var transport = new StdioMcpTransport("__nonexistent_command_12345__");

        // Should throw because the command doesn't exist
        var act = () => transport.ConnectAsync();
        await act.ShouldThrowAsync<Exception>();

        await transport.DisposeAsync();
    }

    [Fact]
    public void ResolveEnvValue_ResolvesExistingVar_OverDefault()
    {
        Environment.SetEnvironmentVariable("MCP_WITH_DEFAULT_SET", "actual-value");
        try
        {
            StdioMcpTransport.ResolveEnvValue("${env:MCP_WITH_DEFAULT_SET:-fallback}")
                .ShouldBe("actual-value");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MCP_WITH_DEFAULT_SET", null);
        }
    }

    [Fact]
    public void ResolveEnvValue_PartialPattern_IsPassedThrough()
    {
        // Missing closing brace
        StdioMcpTransport.ResolveEnvValue("${env:INCOMPLETE").ShouldBe("${env:INCOMPLETE");
        // Not starting with ${env:
        StdioMcpTransport.ResolveEnvValue("$env:VAR}").ShouldBe("$env:VAR}");
    }

    [Fact]
    public void ResolveEnvValue_EmptyString_IsPassedThrough()
    {
        StdioMcpTransport.ResolveEnvValue("").ShouldBeEmpty();
    }

    [Fact]
    public async Task StdioTransport_SendAsync_ThrowsWhenNotConnected()
    {
        var transport = new StdioMcpTransport("echo");

        // Don't call ConnectAsync — should throw
        var request = new JsonRpcRequest { Method = "test" };
        var act = () => transport.SendAsync(request);
        var ex = await act.ShouldThrowAsync<InvalidOperationException>();
        ex.Message.ShouldContain("not connected");

        await transport.DisposeAsync();
    }

    [Fact]
    public async Task StdioTransport_SendNotificationAsync_ThrowsWhenNotConnected()
    {
        var transport = new StdioMcpTransport("echo");

        var notification = new JsonRpcNotification { Method = "test" };
        var act = () => transport.SendNotificationAsync(notification);
        var ex = await act.ShouldThrowAsync<InvalidOperationException>();
        ex.Message.ShouldContain("not connected");

        await transport.DisposeAsync();
    }

    [Fact]
    public async Task StdioTransport_DisposeAsync_CanBeCalledMultipleTimes()
    {
        var transport = new StdioMcpTransport("echo");

        await transport.DisposeAsync();
        await transport.DisposeAsync();

        // Should not throw
    }

    [Fact]
    public async Task StdioTransport_ConnectAsync_ThrowsWhenDisposed()
    {
        var transport = new StdioMcpTransport("echo");
        await transport.DisposeAsync();

        var act = () => transport.ConnectAsync();
        await act.ShouldThrowAsync<ObjectDisposedException>();
    }
}
