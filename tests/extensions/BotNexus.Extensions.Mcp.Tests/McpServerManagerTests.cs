using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Extensions.Mcp.Transport;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;
using Microsoft.Extensions.Logging.Abstractions;

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

    [Fact]
    public async Task ContributeAsync_WhenServerWarmupIsPending_DoesNotBlockAgentCreation()
    {
        await McpServerWarmupCache.DisposeAllAsync();
        var contributor = new McpToolContributor(NullLoggerFactory.Instance);
        var descriptor = CreateDescriptor(new McpExtensionConfig
        {
            Servers = new Dictionary<string, McpServerConfig>
            {
                ["slow"] = new McpServerConfig
                {
                    Command = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cmd.exe" : "/bin/cat",
                    Args = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ["/c", "ping -n 5 127.0.0.1 > nul"] : [],
                    InitTimeoutMs = 5_000
                }
            }
        });
        var context = CreateContributionContext(descriptor);

        try
        {
            var sw = Stopwatch.StartNew();
            var contribution = await contributor.ContributeAsync(context);
            sw.Stop();

            contribution.Tools.ShouldBeEmpty();
            contribution.ResourcesToDispose.ShouldBeNull();
            sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(1));
        }
        finally
        {
            await McpServerWarmupCache.DisposeAllAsync();
        }
    }

    [Fact]
    public async Task McpServerWarmupHostedService_StartAsync_KicksOffConfiguredAgents()
    {
        await McpServerWarmupCache.DisposeAllAsync();
        var countBefore = McpServerWarmupCache.Count;
        var descriptor = CreateDescriptor(new McpExtensionConfig
        {
            Servers = new Dictionary<string, McpServerConfig>
            {
                ["empty"] = new McpServerConfig()
            }
        });
        var registry = new StubAgentRegistry([descriptor]);
        var service = new McpServerWarmupHostedService(registry, NullLoggerFactory.Instance);

        try
        {
            await service.StartAsync(CancellationToken.None);

            // Assert that the service added exactly one entry to the cache.
            // Use delta instead of absolute count to avoid flakes when other
            // tests running in parallel also interact with the static cache.
            (McpServerWarmupCache.Count - countBefore).ShouldBe(1);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    private static AgentDescriptor CreateDescriptor(McpExtensionConfig config)
    {
        var element = JsonSerializer.SerializeToElement(config, JsonContext.Default.McpExtensionConfig);
        return new AgentDescriptor
        {
            AgentId = AgentId.From("test-agent"),
            DisplayName = "Test Agent",
            ModelId = "test-model",
            ApiProvider = "test-provider",
            ExtensionConfig = new Dictionary<string, JsonElement>
            {
                ["botnexus-mcp"] = element
            }
        };
    }

    private static AgentToolContributionContext CreateContributionContext(AgentDescriptor descriptor)
        => new(
            descriptor,
            new AgentExecutionContext { SessionId = SessionId.Create() },
            Path.GetTempPath(),
            new AllowAllPathValidator(),
            _ => null,
            (_, _) => Task.FromResult<string?>(null));

    private sealed class AllowAllPathValidator : IPathValidator
    {
        public bool CanRead(string absolutePath) => true;
        public bool CanWrite(string absolutePath) => true;
        public string? ValidateAndResolve(string rawPath, FileAccessMode mode) => rawPath;
    }

    private sealed class StubAgentRegistry(IReadOnlyList<AgentDescriptor> descriptors) : IAgentRegistry
    {
        public void Register(AgentDescriptor descriptor) => throw new NotSupportedException();
        public void Unregister(AgentId agentId) => throw new NotSupportedException();
        public AgentDescriptor? Get(AgentId agentId) => descriptors.FirstOrDefault(d => d.AgentId == agentId);
        public IReadOnlyList<AgentDescriptor> GetAll() => descriptors;
        public bool Contains(AgentId agentId) => descriptors.Any(d => d.AgentId == agentId);
    }
}
