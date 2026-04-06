using System.Text.Json.Nodes;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Configuration;
using FluentAssertions;

namespace BotNexus.Gateway.Tests;

public sealed class PlatformConfigAgentWriterTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "botnexus-platform-agent-writer-tests", Guid.NewGuid().ToString("N"));
    private readonly string _configPath;
    private readonly BotNexusHome _home;

    public PlatformConfigAgentWriterTests()
    {
        _home = new BotNexusHome(_rootPath);
        Directory.CreateDirectory(_rootPath);
        _configPath = Path.Combine(_rootPath, "config.json");
    }

    [Fact]
    public async Task SaveAsync_WritesAgentIntoConfigAndCreatesWorkspace()
    {
        var writer = new PlatformConfigAgentWriter(_configPath, _home);
        var descriptor = CreateDescriptor("nova") with
        {
            AllowedModelIds = ["claude-sonnet-4.5"],
            ToolIds = ["read"],
            SubAgentIds = ["helper"],
            Metadata = new Dictionary<string, object?> { ["owner"] = "gateway" },
            IsolationOptions = new Dictionary<string, object?> { ["timeoutMs"] = 1000 }
        };

        await writer.SaveAsync(descriptor);

        var root = await ReadConfigAsync();
        var agent = root["agents"]!["nova"]!;

        agent["provider"]!.GetValue<string>().Should().Be("github-copilot");
        agent["model"]!.GetValue<string>().Should().Be("claude-sonnet-4.5");
        agent["displayName"]!.GetValue<string>().Should().Be("nova");
        agent["enabled"]!.GetValue<bool>().Should().BeTrue();
        agent["allowedModels"]!.AsArray().Should().ContainSingle().Which!.GetValue<string>().Should().Be("claude-sonnet-4.5");
        agent["toolIds"]!.AsArray().Should().ContainSingle().Which!.GetValue<string>().Should().Be("read");
        agent["subAgents"]!.AsArray().Should().ContainSingle().Which!.GetValue<string>().Should().Be("helper");
        agent["metadata"]!["owner"]!.GetValue<string>().Should().Be("gateway");
        agent["isolationOptions"]!["timeoutMs"]!.GetValue<int>().Should().Be(1000);

        Directory.Exists(Path.Combine(_home.AgentsPath, "nova")).Should().BeTrue();
        File.Exists(Path.Combine(_home.AgentsPath, "nova", "workspace", "SOUL.md")).Should().BeTrue();
    }

    [Fact]
    public async Task SaveAsync_PreservesUnknownFieldsAndOmitsEmptyOptionalValues()
    {
        await File.WriteAllTextAsync(_configPath, """
            {
              "version": 1,
              "customRootField": "preserve-me",
              "agents": {
                "nova": {
                  "customAgentField": "keep"
                }
              }
            }
            """);

        var writer = new PlatformConfigAgentWriter(_configPath, _home);
        await writer.SaveAsync(CreateDescriptor("nova") with
        {
            Description = null,
            SystemPromptFile = null,
            AllowedModelIds = [],
            ToolIds = [],
            SubAgentIds = [],
            MaxConcurrentSessions = 0
        });

        var root = await ReadConfigAsync();
        var agent = root["agents"]!["nova"]!;

        root["customRootField"]!.GetValue<string>().Should().Be("preserve-me");
        agent["customAgentField"]!.GetValue<string>().Should().Be("keep");
        agent["description"].Should().BeNull();
        agent["systemPromptFile"].Should().BeNull();
        agent["allowedModels"].Should().BeNull();
        agent["toolIds"].Should().BeNull();
        agent["subAgents"].Should().BeNull();
        agent["maxConcurrentSessions"].Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_RemovesAgentFromConfig()
    {
        await File.WriteAllTextAsync(_configPath, """
            {
              "agents": {
                "nova": { "provider": "github-copilot", "model": "gpt-4.1" },
                "other": { "provider": "openai", "model": "gpt-4.1" }
              }
            }
            """);

        var writer = new PlatformConfigAgentWriter(_configPath, _home);
        await writer.DeleteAsync("nova");

        var root = await ReadConfigAsync();
        root["agents"]!["nova"].Should().BeNull();
        root["agents"]!["other"].Should().NotBeNull();
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
            Directory.Delete(_rootPath, recursive: true);
    }

    private async Task<JsonObject> ReadConfigAsync()
    {
        await using var stream = File.OpenRead(_configPath);
        var node = await JsonNode.ParseAsync(stream);
        return node!.AsObject();
    }

    private static AgentDescriptor CreateDescriptor(string agentId)
        => new()
        {
            AgentId = agentId,
            DisplayName = agentId,
            ModelId = "claude-sonnet-4.5",
            ApiProvider = "github-copilot",
            IsolationStrategy = "in-process",
            MaxConcurrentSessions = 0
        };
}
