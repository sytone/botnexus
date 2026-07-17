using BotNexus.Domain.Primitives;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Configuration;
using System.IO.Abstractions.TestingHelpers;

namespace BotNexus.Gateway.Tests;

public sealed class PlatformConfigAgentWriterTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "botnexus-platform-agent-writer-tests", Guid.NewGuid().ToString("N"));
    private readonly string _configPath;
    private readonly BotNexusHome _home;
    private readonly MockFileSystem _fileSystem;

    public PlatformConfigAgentWriterTests()
    {
        _fileSystem = new MockFileSystem();
        _home = new BotNexusHome(_fileSystem, _rootPath);
        _fileSystem.Directory.CreateDirectory(_rootPath);
        _configPath = Path.Combine(_rootPath, "config.json");
    }

    [Fact]
    public async Task SaveAsync_WritesAgentIntoConfigAndCreatesWorkspace()
    {
        var writer = new PlatformConfigAgentWriter(new PlatformConfigWriter(_configPath, _fileSystem), _home);
        var descriptor = CreateDescriptor("test-agent") with
        {
            AllowedModelIds = ["claude-sonnet-4.5"],
            ToolIds = ["read"],
            SubAgentIds = ["helper"],
            Metadata = new Dictionary<string, object?> { ["owner"] = "gateway" },
            IsolationOptions = new Dictionary<string, object?> { ["timeoutMs"] = 1000 }
        };

        await writer.SaveAsync(descriptor);

        var root = await ReadConfigAsync();
        var agent = root["agents"]!["test-agent"]!;

        agent["provider"]!.GetValue<string>().ShouldBe("github-copilot");
        agent["model"]!.GetValue<string>().ShouldBe("claude-sonnet-4.5");
        agent["displayName"]!.GetValue<string>().ShouldBe("test-agent");
        agent["enabled"]!.GetValue<bool>().ShouldBeTrue();
        agent["allowedModels"]!.AsArray().ShouldHaveSingleItem()!.GetValue<string>().ShouldBe("claude-sonnet-4.5");
        agent["toolIds"]!.AsArray().ShouldHaveSingleItem()!.GetValue<string>().ShouldBe("read");
        agent["subAgents"]!.AsArray().ShouldHaveSingleItem()!.GetValue<string>().ShouldBe("helper");
        agent["metadata"]!["owner"]!.GetValue<string>().ShouldBe("gateway");
        agent["isolationOptions"]!["timeoutMs"]!.GetValue<int>().ShouldBe(1000);

        _fileSystem.Directory.Exists(Path.Combine(_home.AgentsPath, "test-agent")).ShouldBeTrue();
        _fileSystem.File.Exists(Path.Combine(_home.AgentsPath, "test-agent", "workspace", "SOUL.md")).ShouldBeTrue();
    }

    [Fact]
    public async Task SaveAsync_WithWireThinkingLevel_ProducesReloadableConfig()
    {
        var writer = new PlatformConfigAgentWriter(new PlatformConfigWriter(_configPath, _fileSystem), _home);
        await writer.SaveAsync(CreateDescriptor("thinking-agent") with { Thinking = "xhigh" });

        var config = await PlatformConfigLoader.LoadAsync(
            _configPath,
            CancellationToken.None,
            validateOnLoad: true,
            fileSystem: _fileSystem);

        config.Agents!["thinking-agent"].Thinking.ShouldBe("xhigh");
    }

    [Fact]
    public async Task SaveAsync_PreservesUnknownFieldsAndOmitsEmptyOptionalValues()
    {
        await _fileSystem.File.WriteAllTextAsync(_configPath, """
            {
              "version": 1,
              "customRootField": "preserve-me",
              "agents": {
                "test-agent": {
                  "customAgentField": "keep"
                }
              }
            }
            """);

        var writer = new PlatformConfigAgentWriter(new PlatformConfigWriter(_configPath, _fileSystem), _home);
        await writer.SaveAsync(CreateDescriptor("test-agent") with
        {
            Description = null,
            SystemPromptFile = null,
            AllowedModelIds = [],
            ToolIds = [],
            SubAgentIds = [],
            MaxConcurrentSessions = 0
        });

        var root = await ReadConfigAsync();
        var agent = root["agents"]!["test-agent"]!;

        root["customRootField"]!.GetValue<string>().ShouldBe("preserve-me");
        agent["customAgentField"]!.GetValue<string>().ShouldBe("keep");
        agent["description"].ShouldBeNull();
        agent["systemPromptFile"].ShouldBeNull();
        agent["allowedModels"].ShouldBeNull();
        agent["toolIds"].ShouldBeNull();
        agent["subAgents"].ShouldBeNull();
        agent["maxConcurrentSessions"].ShouldBeNull();
    }

    [Fact]
    public async Task DeleteAsync_RemovesAgentFromConfig()
    {
        await _fileSystem.File.WriteAllTextAsync(_configPath, """
            {
              "agents": {
                "test-agent": { "provider": "github-copilot", "model": "gpt-4.1" },
                "other": { "provider": "openai", "model": "gpt-4.1" }
              }
            }
            """);

        var writer = new PlatformConfigAgentWriter(new PlatformConfigWriter(_configPath, _fileSystem), _home);
        await writer.DeleteAsync("test-agent");

        var root = await ReadConfigAsync();
        root["agents"]!["test-agent"].ShouldBeNull();
        root["agents"]!["other"].ShouldNotBeNull();
    }

    public void Dispose()
    {
        if (_fileSystem.Directory.Exists(_rootPath))
            _fileSystem.Directory.Delete(_rootPath, recursive: true);
    }

    private async Task<JsonObject> ReadConfigAsync()
    {
        await using var stream = _fileSystem.File.OpenRead(_configPath);
        var node = await JsonNode.ParseAsync(stream);
        return node!.AsObject();
    }

    private static AgentDescriptor CreateDescriptor(string agentId)
        => new()
        {
            AgentId = AgentId.From(agentId),
            DisplayName = agentId,
            ModelId = "claude-sonnet-4.5",
            ApiProvider = "github-copilot",
            IsolationStrategy = "in-process",
            MaxConcurrentSessions = 0
        };
}
