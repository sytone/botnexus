using FluentAssertions;

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
        tools.Should().BeEmpty();

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
        manager.GetClients().Should().BeEmpty();
    }
}
