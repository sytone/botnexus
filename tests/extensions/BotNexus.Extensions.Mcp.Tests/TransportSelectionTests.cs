using BotNexus.Extensions.Mcp.Transport;

namespace BotNexus.Extensions.Mcp.Tests;

public class TransportSelectionTests
{
    [Fact]
    public void CreateTransport_ReturnsStdio_WhenCommandIsSet()
    {
        var config = new McpServerConfig { Command = "node", Args = ["server.js"] };
        var transport = McpServerManager.CreateTransport(config);

        transport.ShouldNotBeNull();
        transport.ShouldBeOfType<StdioMcpTransport>();
    }

    [Fact]
    public void CreateTransport_ReturnsHttpSse_WhenUrlIsSet()
    {
        var config = new McpServerConfig { Url = "http://localhost:3000/mcp" };
        var transport = McpServerManager.CreateTransport(config);

        transport.ShouldNotBeNull();
        transport.ShouldBeOfType<HttpSseMcpTransport>();
    }

    [Fact]
    public void CreateTransport_PrefersUrl_WhenBothUrlAndCommandAreSet()
    {
        var config = new McpServerConfig
        {
            Command = "node",
            Url = "http://localhost:3000/mcp",
        };

        var transport = McpServerManager.CreateTransport(config);
        transport.ShouldBeOfType<HttpSseMcpTransport>();
    }

    [Fact]
    public void CreateTransport_ReturnsNull_WhenNeitherCommandNorUrlIsSet()
    {
        var config = new McpServerConfig();
        var transport = McpServerManager.CreateTransport(config);

        transport.ShouldBeNull();
    }

    [Fact]
    public void CreateTransport_ReturnsNull_WhenCommandIsEmpty()
    {
        var config = new McpServerConfig { Command = "  " };
        var transport = McpServerManager.CreateTransport(config);

        transport.ShouldBeNull();
    }

    [Fact]
    public void CreateTransport_PassesHeaders_ToHttpSseTransport()
    {
        var config = new McpServerConfig
        {
            Url = "http://localhost:3000/mcp",
            Headers = new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer test-token",
            },
        };

        var transport = McpServerManager.CreateTransport(config);
        transport.ShouldBeOfType<HttpSseMcpTransport>();
    }

    [Fact]
    public async Task StartServersAsync_SkipsServers_WithNoCommandOrUrl()
    {
        var manager = new McpServerManager();
        var config = new McpExtensionConfig
        {
            Servers = new Dictionary<string, McpServerConfig>
            {
                ["empty"] = new McpServerConfig(),
            },
        };

        var tools = await manager.StartServersAsync(config);
        tools.ShouldBeEmpty();

        await manager.DisposeAsync();
    }
}
