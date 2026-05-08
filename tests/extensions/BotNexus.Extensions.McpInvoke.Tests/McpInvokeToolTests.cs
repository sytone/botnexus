using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Extensions.Mcp;

namespace BotNexus.Extensions.McpInvoke.Tests;

/// <summary>
/// Unit tests for McpInvokeTool metadata, basic validation, and lifecycle.
/// </summary>
public class McpInvokeToolTests
{
    [Fact]
    public void Name_ReturnsInvokeMcp()
    {
        var config = new McpInvokeConfig();
        var tool = new McpInvokeTool(config);

        tool.Name.ShouldBe("invoke_mcp");
    }

    [Fact]
    public void Label_ReturnsMcpInvoke()
    {
        var config = new McpInvokeConfig();
        var tool = new McpInvokeTool(config);

        tool.Label.ShouldBe("MCP Invoke");
    }

    [Fact]
    public void Definition_HasCorrectSchema()
    {
        var config = new McpInvokeConfig();
        var tool = new McpInvokeTool(config);

        var definition = tool.Definition;

        definition.Name.ShouldBe("invoke_mcp");
        definition.Description.ShouldNotBeNullOrEmpty();
        
        var schema = definition.Parameters;
        schema.ValueKind.ShouldBe(JsonValueKind.Object);
        
        schema.GetProperty("type").GetString().ShouldBe("object");
        schema.GetProperty("properties").TryGetProperty("action", out _).ShouldBeTrue();
        schema.GetProperty("properties").TryGetProperty("server", out _).ShouldBeTrue();
        schema.GetProperty("properties").TryGetProperty("tool", out _).ShouldBeTrue();
        schema.GetProperty("properties").TryGetProperty("arguments", out _).ShouldBeTrue();
        
        var actionProperty = schema.GetProperty("properties").GetProperty("action");
        actionProperty.GetProperty("type").GetString().ShouldBe("string");
        actionProperty.TryGetProperty("enum", out var enumValues).ShouldBeTrue();
        
        var enumArray = enumValues.EnumerateArray().Select(e => e.GetString()).ToList();
        enumArray.ShouldContain("call");
        enumArray.ShouldContain("list_tools");
        enumArray.ShouldContain("list_servers");
    }

    [Fact]
    public async Task ListServers_WithNoConfig_ReturnsNoServersMessage()
    {
        var config = new McpInvokeConfig { Servers = new() };
        var tool = new McpInvokeTool(config);

        var args = new Dictionary<string, object?> { ["action"] = "list_servers" };
        var result = await tool.ExecuteAsync("test-call", args);

        result.Content.Count().ShouldBe(1);
        result.Content[0].Type.ShouldBe(AgentToolContentType.Text);
        result.Content[0].Value.ShouldBe("No MCP servers configured.");

        await tool.DisposeAsync();
    }

    [Fact]
    public async Task ListServers_WithConfiguredServers_ReturnsServerList()
    {
        var config = new McpInvokeConfig
        {
            Servers = new Dictionary<string, McpServerConfig>
            {
                ["github"] = new McpServerConfig { Command = "node", Args = ["server.js"] },
                ["web"] = new McpServerConfig { Url = "http://localhost:8080" },
            }
        };
        var tool = new McpInvokeTool(config);

        var args = new Dictionary<string, object?> { ["action"] = "list_servers" };
        var result = await tool.ExecuteAsync("test-call", args);

        result.Content.Count().ShouldBe(1);
        result.Content[0].Type.ShouldBe(AgentToolContentType.Text);
        
        var text = result.Content[0].Value;
        text.ShouldContain("Available MCP Servers");
        text.ShouldContain("github");
        text.ShouldContain("stdio");
        text.ShouldContain("web");
        text.ShouldContain("HTTP/SSE");
        text.ShouldContain("not started");

        await tool.DisposeAsync();
    }

    [Fact]
    public async Task ListTools_WithoutServerParam_ReturnsError()
    {
        var config = new McpInvokeConfig();
        var tool = new McpInvokeTool(config);

        var args = new Dictionary<string, object?> { ["action"] = "list_tools" };
        var result = await tool.ExecuteAsync("test-call", args);

        result.Content.Count().ShouldBe(1);
        result.Content[0].Type.ShouldBe(AgentToolContentType.Text);
        result.Content[0].Value.ShouldContain("'server' is required");

        await tool.DisposeAsync();
    }

    [Fact]
    public async Task Call_WithoutServerParam_ReturnsError()
    {
        var config = new McpInvokeConfig();
        var tool = new McpInvokeTool(config);

        var args = new Dictionary<string, object?> { ["action"] = "call", ["tool"] = "search" };
        var result = await tool.ExecuteAsync("test-call", args);

        result.Content.Count().ShouldBe(1);
        result.Content[0].Type.ShouldBe(AgentToolContentType.Text);
        result.Content[0].Value.ShouldContain("'server' is required");

        await tool.DisposeAsync();
    }

    [Fact]
    public async Task Call_WithoutToolParam_ReturnsError()
    {
        var config = new McpInvokeConfig();
        var tool = new McpInvokeTool(config);

        var args = new Dictionary<string, object?> { ["action"] = "call", ["server"] = "github" };
        var result = await tool.ExecuteAsync("test-call", args);

        result.Content.Count().ShouldBe(1);
        result.Content[0].Type.ShouldBe(AgentToolContentType.Text);
        result.Content[0].Value.ShouldContain("'tool' is required");

        await tool.DisposeAsync();
    }

    [Fact]
    public async Task Call_WithUnconfiguredServer_ReturnsError()
    {
        var config = new McpInvokeConfig { Servers = new() };
        var tool = new McpInvokeTool(config);

        var args = new Dictionary<string, object?>
        {
            ["action"] = "call",
            ["server"] = "nonexistent",
            ["tool"] = "search"
        };
        var result = await tool.ExecuteAsync("test-call", args);

        result.Content.Count().ShouldBe(1);
        result.Content[0].Type.ShouldBe(AgentToolContentType.Text);
        result.Content[0].Value.ShouldContain("not configured");

        await tool.DisposeAsync();
    }

    [Fact]
    public async Task ExecuteAsync_WithUnknownAction_ReturnsError()
    {
        var config = new McpInvokeConfig();
        var tool = new McpInvokeTool(config);

        var args = new Dictionary<string, object?> { ["action"] = "invalid_action" };
        var result = await tool.ExecuteAsync("test-call", args);

        result.Content.Count().ShouldBe(1);
        result.Content[0].Type.ShouldBe(AgentToolContentType.Text);
        result.Content[0].Value.ShouldContain("Unknown action");

        await tool.DisposeAsync();
    }

    [Fact]
    public void GetPromptGuidelines_ReturnsNonEmptyList()
    {
        var config = new McpInvokeConfig();
        var tool = new McpInvokeTool(config);

        var guidelines = tool.GetPromptGuidelines();

        guidelines.ShouldNotBeEmpty();
        guidelines.ShouldContain(g => g.Contains("skill", StringComparison.OrdinalIgnoreCase));
        guidelines.ShouldContain(g => g.Contains("invoke_mcp", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetPromptSnippet_ReturnsNonNullString()
    {
        var config = new McpInvokeConfig();
        var tool = new McpInvokeTool(config);

        var snippet = tool.GetPromptSnippet();

        snippet.ShouldNotBeNullOrEmpty();
        snippet.ShouldContain("invoke_mcp");
    }

    [Fact]
    public async Task DisposeAsync_DisposesWithoutError()
    {
        var config = new McpInvokeConfig();
        var tool = new McpInvokeTool(config);

        await tool.DisposeAsync();
    }

    [Fact]
    public async Task PrepareArgumentsAsync_ReturnsArgumentsUnchanged()
    {
        var config = new McpInvokeConfig();
        var tool = new McpInvokeTool(config);

        var originalArgs = new Dictionary<string, object?>
        {
            ["action"] = "call",
            ["server"] = "test",
            ["tool"] = "search"
        };

        var preparedArgs = await tool.PrepareArgumentsAsync(originalArgs);

        preparedArgs.ShouldBeSameAs(originalArgs);

        await tool.DisposeAsync();
    }

    // --- Negative input / edge case tests ---

    [Fact]
    public async Task Call_WithEmptyServerName_ReturnsError()
    {
        var config = new McpInvokeConfig();
        var tool = new McpInvokeTool(config);

        var args = new Dictionary<string, object?>
        {
            ["action"] = "call",
            ["server"] = "",
            ["tool"] = "search"
        };
        var result = await tool.ExecuteAsync("test-empty-server", args);

        result.Content.Count().ShouldBe(1);
        result.Content[0].Value.ShouldContain("server");

        await tool.DisposeAsync();
    }

    [Fact]
    public async Task Call_WithWhitespaceOnlyServerName_ReturnsError()
    {
        var config = new McpInvokeConfig();
        var tool = new McpInvokeTool(config);

        var args = new Dictionary<string, object?>
        {
            ["action"] = "call",
            ["server"] = "   ",
            ["tool"] = "search"
        };
        var result = await tool.ExecuteAsync("test-ws-server", args);

        result.Content.Count().ShouldBe(1);
        // Should indicate server is missing or not found
        result.Content[0].Value.ShouldNotBeNullOrEmpty();

        await tool.DisposeAsync();
    }

    [Fact]
    public async Task Call_WithNullAction_ReturnsError()
    {
        var config = new McpInvokeConfig();
        var tool = new McpInvokeTool(config);

        var args = new Dictionary<string, object?> { ["action"] = null };
        var result = await tool.ExecuteAsync("test-null-action", args);

        result.Content.Count().ShouldBe(1);
        result.Content[0].Value.ShouldNotBeNullOrEmpty();

        await tool.DisposeAsync();
    }

    [Fact]
    public async Task Call_WithEmptyAction_ReturnsError()
    {
        var config = new McpInvokeConfig();
        var tool = new McpInvokeTool(config);

        var args = new Dictionary<string, object?> { ["action"] = "" };
        var result = await tool.ExecuteAsync("test-empty-action", args);

        result.Content.Count().ShouldBe(1);
        result.Content[0].Value.ShouldContain("Unknown action");

        await tool.DisposeAsync();
    }

    [Fact]
    public async Task ListTools_WithUnconfiguredServer_ReturnsError()
    {
        var config = new McpInvokeConfig { Servers = new() };
        var tool = new McpInvokeTool(config);

        var args = new Dictionary<string, object?>
        {
            ["action"] = "list_tools",
            ["server"] = "nonexistent"
        };
        var result = await tool.ExecuteAsync("test-list-unconfigured", args);

        result.Content.Count().ShouldBe(1);
        result.Content[0].Value.ShouldContain("not configured");

        await tool.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_CanBeCalledMultipleTimes()
    {
        var config = new McpInvokeConfig();
        var tool = new McpInvokeTool(config);

        await tool.DisposeAsync();
        await tool.DisposeAsync(); // should not throw
    }

    [Fact]
    public async Task Call_WithEmptyArguments_ReturnsError()
    {
        var config = new McpInvokeConfig();
        var tool = new McpInvokeTool(config);

        var args = new Dictionary<string, object?>();
        var result = await tool.ExecuteAsync("test-no-args", args);

        result.Content.Count().ShouldBe(1);
        result.Content[0].Value.ShouldNotBeNullOrEmpty();

        await tool.DisposeAsync();
    }

    [Fact]
    public void McpInvokeConfig_DefaultEnabled_IsTrue()
    {
        var config = new McpInvokeConfig();
        config.Enabled.ShouldBeTrue();
    }

    [Fact]
    public void McpInvokeConfig_DefaultServers_IsNotNull()
    {
        var config = new McpInvokeConfig();
        config.Servers.ShouldNotBeNull();
    }
}
