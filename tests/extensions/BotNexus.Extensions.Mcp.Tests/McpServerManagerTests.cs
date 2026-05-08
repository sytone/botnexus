using System.Runtime.InteropServices;
using BotNexus.Extensions.Mcp.Transport;

namespace BotNexus.Extensions.Mcp.Tests;

public class McpServerManagerTests
{
    [Fact]
    public async Task StartServersAsync_SkipsServers_WithNoCommand()
    {
        var manager = new McpServerManager();
        var config = new McpExtensionConfig
        {
            Servers = new Dictionary<string, McpServerConfig>
            {
                ["empty"] = new McpServerConfig { Command = null },
            },
        };

        var tools = await manager.StartServersAsync(config);
        tools.ShouldBeEmpty();

        await manager.DisposeAsync();
    }

    [Fact]
    public async Task StopAllAsync_CanBeCalledMultipleTimes()
    {
        var manager = new McpServerManager();
        await manager.StopAllAsync();
        await manager.StopAllAsync();

        // Should not throw
        await manager.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_CanBeCalledMultipleTimes()
    {
        var manager = new McpServerManager();
        await manager.DisposeAsync();
        await manager.DisposeAsync();

        // Should not throw
    }

    [Fact]
    public void GetClients_ReturnsEmptyList_Initially()
    {
        var manager = new McpServerManager();
        manager.GetClients().ShouldBeEmpty();
    }

    [Fact]
    public async Task StartServersAsync_EmptyConfig_ReturnsEmptyTools()
    {
        var manager = new McpServerManager();
        var config = new McpExtensionConfig { Servers = new Dictionary<string, McpServerConfig>() };

        var tools = await manager.StartServersAsync(config);

        tools.ShouldBeEmpty();
        manager.GetClients().ShouldBeEmpty();

        await manager.DisposeAsync();
    }

    [Fact]
    public async Task StartServersAsync_SkipsServers_WithEmptyCommand()
    {
        var manager = new McpServerManager();
        var config = new McpExtensionConfig
        {
            Servers = new Dictionary<string, McpServerConfig>
            {
                ["blank"] = new McpServerConfig { Command = "" },
                ["whitespace"] = new McpServerConfig { Command = "   " },
            },
        };

        var tools = await manager.StartServersAsync(config);

        tools.ShouldBeEmpty();
        manager.GetClients().ShouldBeEmpty();

        await manager.DisposeAsync();
    }

    [Fact]
    public async Task StopAllAsync_WhenNoServersRunning_DoesNotThrow()
    {
        var manager = new McpServerManager();

        // No servers started, StopAll should be a no-op
        await manager.StopAllAsync();
        manager.GetClients().ShouldBeEmpty();

        await manager.DisposeAsync();
    }

    [Fact]
    public async Task GetClients_ThrowsAfterDispose()
    {
        var manager = new McpServerManager();
        await manager.DisposeAsync();

        var act = () => manager.GetClients();
        act.ShouldThrow<ObjectDisposedException>();
    }

    [Fact]
    public async Task StartServersAsync_ThrowsAfterDispose()
    {
        var manager = new McpServerManager();
        await manager.DisposeAsync();

        var config = new McpExtensionConfig { Servers = new Dictionary<string, McpServerConfig>() };
        var act = () => manager.StartServersAsync(config);
        await act.ShouldThrowAsync<ObjectDisposedException>();
    }

    // --- Transport creation tests ---

    [Fact]
    public void CreateTransport_WithUrl_ReturnsHttpSseTransport()
    {
        var config = new McpServerConfig { Url = "http://localhost:8080/mcp" };
        var transport = McpServerManager.CreateTransport(config);
        transport.ShouldNotBeNull();
        transport.ShouldBeOfType<HttpSseMcpTransport>();
    }

    [Fact]
    public void CreateTransport_WithCommand_ReturnsStdioTransport()
    {
        var config = new McpServerConfig { Command = "node", Args = ["server.js"] };
        var transport = McpServerManager.CreateTransport(config);
        transport.ShouldNotBeNull();
        transport.ShouldBeOfType<StdioMcpTransport>();
    }

    [Fact]
    public void CreateTransport_WithNoCommandOrUrl_ReturnsNull()
    {
        var config = new McpServerConfig();
        var transport = McpServerManager.CreateTransport(config);
        transport.ShouldBeNull();
    }

    [Fact]
    public void CreateTransport_WithEmptyUrl_ReturnsNull()
    {
        var config = new McpServerConfig { Url = "" };
        var transport = McpServerManager.CreateTransport(config);
        transport.ShouldBeNull();
    }

    [Fact]
    public void CreateTransport_WithWhitespaceUrl_ReturnsNull()
    {
        var config = new McpServerConfig { Url = "   " };
        var transport = McpServerManager.CreateTransport(config);
        transport.ShouldBeNull();
    }

    [Fact]
    public void CreateTransport_PrefersUrlOverCommand()
    {
        // When both URL and command are set, URL takes precedence
        var config = new McpServerConfig
        {
            Url = "http://localhost:8080/mcp",
            Command = "node",
            Args = ["server.js"]
        };
        var transport = McpServerManager.CreateTransport(config);
        transport.ShouldBeOfType<HttpSseMcpTransport>();
    }

    [Fact]
    public async Task StartServersAsync_WithInvalidCommand_SkipsAndContinues()
    {
        var manager = new McpServerManager();
        var config = new McpExtensionConfig
        {
            Servers = new Dictionary<string, McpServerConfig>
            {
                ["bad"] = new McpServerConfig
                {
                    Command = "nonexistent_program_that_does_not_exist_abc_xyz",
                    InitTimeoutMs = 2000
                },
            },
        };

        // Should not throw — bad servers are skipped with warnings
        var tools = await manager.StartServersAsync(config);
        tools.ShouldBeEmpty();
        manager.GetClients().ShouldBeEmpty();

        await manager.DisposeAsync();
    }

    [Fact]
    public async Task StartServersAsync_WithShortTimeout_SkipsSlowServer()
    {
        var manager = new McpServerManager();
        var config = new McpExtensionConfig
        {
            Servers = new Dictionary<string, McpServerConfig>
            {
                ["slow"] = new McpServerConfig
                {
                    // Use a command that exists but will never complete MCP init
                    Command = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cmd.exe" : "/bin/cat",
                    Args = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ["/c", "ping -n 30 127.0.0.1"] : [],
                    InitTimeoutMs = 500
                },
            },
        };

        var tools = await manager.StartServersAsync(config);
        tools.ShouldBeEmpty("server should time out during init");

        await manager.DisposeAsync();
    }
}
