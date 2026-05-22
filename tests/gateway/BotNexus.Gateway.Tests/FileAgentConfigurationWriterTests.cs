using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO.Abstractions.TestingHelpers;

namespace BotNexus.Gateway.Tests;

public sealed class FileAgentConfigurationWriterTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "botnexus-file-config-writer-tests", Guid.NewGuid().ToString("N"));
    private readonly string _configDirectory;
    private readonly BotNexusHome _home;
    private readonly MockFileSystem _fileSystem;

    public FileAgentConfigurationWriterTests()
    {
        _fileSystem = new MockFileSystem();
        _home = new BotNexusHome(_fileSystem, _rootPath);
        _configDirectory = Path.Combine(_rootPath, "agent-config");
        _fileSystem.Directory.CreateDirectory(_configDirectory);
    }

    [Fact]
    public async Task SaveAsync_WritesConfigAndCreatesWorkspace()
    {
        var writer = new FileAgentConfigurationWriter(_configDirectory, _home, _fileSystem);
        var descriptor = CreateDescriptor("test-agent");

        await writer.SaveAsync(descriptor);

        var configPath = Path.Combine(_configDirectory, "test-agent.json");
        _fileSystem.File.Exists(configPath).ShouldBeTrue();
        _fileSystem.Directory.Exists(Path.Combine(_home.AgentsPath, "test-agent")).ShouldBeTrue();
        _fileSystem.File.Exists(Path.Combine(_home.AgentsPath, "test-agent", "workspace", "SOUL.md")).ShouldBeTrue();
    }

    [Fact]
    public async Task SaveAsync_UsesCamelCaseAndOmitsNulls()
    {
        var writer = new FileAgentConfigurationWriter(_configDirectory, _home, _fileSystem);
        var descriptor = CreateDescriptor("test-agent") with
        {
            Description = null,
            SystemPromptFile = null,
            SubAgentIds = ["child-a"],
            ToolIds = ["read"]
        };

        await writer.SaveAsync(descriptor);

        var json = await _fileSystem.File.ReadAllTextAsync(Path.Combine(_configDirectory, "test-agent.json"));
        json.ShouldContain("\"agentId\": \"test-agent\"");
        json.ShouldContain("\"subAgentIds\": [");
        json.ShouldContain("\"toolIds\": [");
        json.ShouldNotContain("\"description\"");
        json.ShouldNotContain("\"systemPromptFile\"");
    }

    [Fact]
    public async Task DeleteAsync_RemovesConfigFile()
    {
        var writer = new FileAgentConfigurationWriter(_configDirectory, _home, _fileSystem);
        await writer.SaveAsync(CreateDescriptor("test-agent"));

        await writer.DeleteAsync("test-agent");

        _fileSystem.File.Exists(Path.Combine(_configDirectory, "test-agent.json")).ShouldBeFalse();
    }

    [Fact]
    public async Task SaveAsync_PreservesRoundTripCompatibilityWithSource()
    {
        var descriptor = CreateDescriptor("test-agent") with
        {
            Metadata = new Dictionary<string, object?> { ["owner"] = "gateway" },
            IsolationOptions = new Dictionary<string, object?> { ["timeoutMs"] = 1000 }
        };

        var writer = new FileAgentConfigurationWriter(_configDirectory, _home, _fileSystem);
        await writer.SaveAsync(descriptor);

        var source = new FileAgentConfigurationSource(
            _configDirectory,
            new NullLogger<FileAgentConfigurationSource>(),
            _fileSystem);
        var loaded = (await source.LoadAsync()).ShouldHaveSingleItem();

        loaded.AgentId.ShouldBe(descriptor.AgentId);
        loaded.DisplayName.ShouldBe(descriptor.DisplayName);
        loaded.ModelId.ShouldBe(descriptor.ModelId);
        loaded.ApiProvider.ShouldBe(descriptor.ApiProvider);
        loaded.Metadata["owner"].ShouldBe("gateway");
        loaded.IsolationOptions["timeoutMs"].ShouldBe(1000L);
    }

    public void Dispose() { }

    private static AgentDescriptor CreateDescriptor(string agentId)
        => new()
        {
            AgentId = AgentId.From(agentId),
            DisplayName = agentId,
            ModelId = "claude-sonnet-4.5",
            ApiProvider = "github-copilot",
            SystemPrompt = "You are helpful.",
            IsolationStrategy = "in-process",
            MaxConcurrentSessions = 0
        };
}
