using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests;

public sealed class FileAgentConfigurationWriterTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "botnexus-file-config-writer-tests", Guid.NewGuid().ToString("N"));
    private readonly string _configDirectory;
    private readonly BotNexusHome _home;

    public FileAgentConfigurationWriterTests()
    {
        _home = new BotNexusHome(_rootPath);
        _configDirectory = Path.Combine(_rootPath, "agent-config");
        Directory.CreateDirectory(_configDirectory);
    }

    [Fact]
    public async Task SaveAsync_WritesConfigAndCreatesWorkspace()
    {
        var writer = new FileAgentConfigurationWriter(_configDirectory, _home);
        var descriptor = CreateDescriptor("nova");

        await writer.SaveAsync(descriptor);

        var configPath = Path.Combine(_configDirectory, "nova.json");
        File.Exists(configPath).Should().BeTrue();
        Directory.Exists(Path.Combine(_home.AgentsPath, "nova")).Should().BeTrue();
        File.Exists(Path.Combine(_home.AgentsPath, "nova", "workspace", "SOUL.md")).Should().BeTrue();
    }

    [Fact]
    public async Task SaveAsync_UsesCamelCaseAndOmitsNulls()
    {
        var writer = new FileAgentConfigurationWriter(_configDirectory, _home);
        var descriptor = CreateDescriptor("nova") with
        {
            Description = null,
            SystemPromptFile = null,
            SubAgentIds = ["child-a"],
            ToolIds = ["read"]
        };

        await writer.SaveAsync(descriptor);

        var json = await File.ReadAllTextAsync(Path.Combine(_configDirectory, "nova.json"));
        json.Should().Contain("\"agentId\": \"nova\"");
        json.Should().Contain("\"subAgentIds\": [");
        json.Should().Contain("\"toolIds\": [");
        json.Should().NotContain("\"description\"");
        json.Should().NotContain("\"systemPromptFile\"");
    }

    [Fact]
    public async Task DeleteAsync_RemovesConfigFile()
    {
        var writer = new FileAgentConfigurationWriter(_configDirectory, _home);
        await writer.SaveAsync(CreateDescriptor("nova"));

        await writer.DeleteAsync("nova");

        File.Exists(Path.Combine(_configDirectory, "nova.json")).Should().BeFalse();
    }

    [Fact]
    public async Task SaveAsync_PreservesRoundTripCompatibilityWithSource()
    {
        var descriptor = CreateDescriptor("nova") with
        {
            Metadata = new Dictionary<string, object?> { ["owner"] = "gateway" },
            IsolationOptions = new Dictionary<string, object?> { ["timeoutMs"] = 1000 }
        };

        var writer = new FileAgentConfigurationWriter(_configDirectory, _home);
        await writer.SaveAsync(descriptor);

        var source = new FileAgentConfigurationSource(
            _configDirectory,
            new NullLogger<FileAgentConfigurationSource>());
        var loaded = (await source.LoadAsync()).Should().ContainSingle().Subject;

        loaded.AgentId.Should().Be(descriptor.AgentId);
        loaded.DisplayName.Should().Be(descriptor.DisplayName);
        loaded.ModelId.Should().Be(descriptor.ModelId);
        loaded.ApiProvider.Should().Be(descriptor.ApiProvider);
        loaded.Metadata["owner"].Should().Be("gateway");
        loaded.IsolationOptions["timeoutMs"].Should().Be(1000L);
    }

    public void Dispose()
    {
        if (!Directory.Exists(_rootPath))
            return;

        for (var i = 0; i < 3; i++)
        {
            try
            {
                Directory.Delete(_rootPath, recursive: true);
                return;
            }
            catch (IOException) when (i < 2)
            {
                Thread.Sleep(100);
            }
            catch
            {
                break;
            }
        }
    }

    private static AgentDescriptor CreateDescriptor(string agentId)
        => new()
        {
            AgentId = agentId,
            DisplayName = agentId,
            ModelId = "claude-sonnet-4.5",
            ApiProvider = "github-copilot",
            SystemPrompt = "You are helpful.",
            IsolationStrategy = "in-process",
            MaxConcurrentSessions = 0
        };
}
