using System.Text.Json.Nodes;
using BotNexus.Agent.Mcp;
using BotNexus.Agent.Tools;
using BotNexus.Core.Configuration;
using BotNexus.Core.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Tests.Unit.Tests;

/// <summary>
/// Tests for MCP tool wrapping and loading.
/// The McpClient itself requires a live MCP server, so these tests use a fake
/// client stub and test the wrapping/registration logic in isolation.
/// </summary>
public class McpToolTests
{
    // ── McpServerConfig ───────────────────────────────────────────────────────

    [Fact]
    public void McpServerConfig_EffectiveTransport_IsStdio_WhenCommandSet()
    {
        var cfg = new McpServerConfig { Command = "npx", Args = ["-y", "some-server"] };
        cfg.EffectiveTransport.Should().Be(McpTransportType.Stdio);
    }

    [Fact]
    public void McpServerConfig_EffectiveTransport_IsSse_WhenUrlSetAndNoCommand()
    {
        var cfg = new McpServerConfig { Url = "http://localhost:3001/sse" };
        cfg.EffectiveTransport.Should().Be(McpTransportType.Sse);
    }

    [Fact]
    public void McpServerConfig_EffectiveTransport_RespectsExplicitType()
    {
        var cfg = new McpServerConfig { Type = McpTransportType.StreamableHttp, Url = "http://x" };
        cfg.EffectiveTransport.Should().Be(McpTransportType.StreamableHttp);
    }

    [Fact]
    public void McpServerConfig_DefaultValues_AreCorrect()
    {
        var cfg = new McpServerConfig();
        cfg.ToolTimeout.Should().Be(30);
        cfg.EnabledTools.Should().Equal(["*"]);
        cfg.Env.Should().BeEmpty();
        cfg.Args.Should().BeEmpty();
        cfg.Headers.Should().BeEmpty();
    }

    // ── McpTool ───────────────────────────────────────────────────────────────

    [Fact]
    public void McpTool_ExposesCorrectDefinition()
    {
        var def = new ToolDefinition("mcp_server_my_tool", "A remote tool", new Dictionary<string, ToolParameterSchema>
        {
            ["query"] = new("string", "Search query", Required: true)
        });

        // We can't easily construct a McpClient without a server, so just test McpTool definition
        // by wrapping a fake client
        var fakeClient = new FakeMcpClient();
        var tool = new McpTool(fakeClient, "my_tool", def, NullLogger.Instance);

        tool.Definition.Name.Should().Be("mcp_server_my_tool");
        tool.Definition.Parameters.Should().ContainKey("query");
    }

    [Fact]
    public async Task McpTool_CallsThroughToClient()
    {
        var def = new ToolDefinition("mcp_test_echo", "Echo", new Dictionary<string, ToolParameterSchema>
        {
            ["text"] = new("string", "Input", Required: true)
        });

        var fakeClient = new FakeMcpClient { ReturnValue = "hello from mcp" };
        var tool = new McpTool(fakeClient, "echo", def, NullLogger.Instance);

        var result = await tool.ExecuteAsync(new Dictionary<string, object?> { ["text"] = "hello" });
        result.Should().Be("hello from mcp");
        fakeClient.LastCalledTool.Should().Be("echo");
    }

    [Fact]
    public async Task McpTool_ConvertsArgumentTypes()
    {
        var def = new ToolDefinition("mcp_s_t", "T", new Dictionary<string, ToolParameterSchema>());
        var fakeClient = new FakeMcpClient { ReturnValue = "ok" };
        var tool = new McpTool(fakeClient, "t", def);

        await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["s"] = "str",
            ["b"] = true,
            ["i"] = 42,
            ["null_val"] = null
        });

        fakeClient.LastArgs.Should().NotBeNull();
        fakeClient.LastArgs!["s"]?.GetValue<string>().Should().Be("str");
        fakeClient.LastArgs["b"]?.GetValue<bool>().Should().BeTrue();
        fakeClient.LastArgs["i"]?.GetValue<int>().Should().Be(42);
        fakeClient.LastArgs["null_val"].Should().BeNull();
    }

    // ── McpToolLoader filtering ───────────────────────────────────────────────

    [Fact]
    public async Task McpToolLoader_SkipsServers_ThatFailToInitialize()
    {
        var configs = new Dictionary<string, McpServerConfig>
        {
            ["bad"] = new McpServerConfig
            {
                Command = "/nonexistent_binary_xyz_12345",
                Args = []
            }
        };

        var loader = new McpToolLoader(configs, NullLogger<McpToolLoader>.Instance);
        var registry = new ToolRegistry();

        // Should not throw; bad server is logged and skipped
        var act = () => loader.LoadAsync(registry, CancellationToken.None);
        await act.Should().NotThrowAsync();

        registry.GetNames().Should().BeEmpty();
        await loader.DisposeAsync();
    }
}

// ── Fake helpers ──────────────────────────────────────────────────────────────

/// <summary>Test double that simulates McpClient without a real server.</summary>
file sealed class FakeMcpClient : IMcpClient
{
    public string ReturnValue { get; set; } = "fake result";
    public string? LastCalledTool { get; private set; }
    public JsonObject? LastArgs { get; private set; }

    public IReadOnlyDictionary<string, JsonObject> RemoteTools { get; } = new Dictionary<string, JsonObject>();

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<string> CallToolAsync(string toolName, JsonObject arguments, CancellationToken cancellationToken = default)
    {
        LastCalledTool = toolName;
        LastArgs = arguments;
        return Task.FromResult(ReturnValue);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
