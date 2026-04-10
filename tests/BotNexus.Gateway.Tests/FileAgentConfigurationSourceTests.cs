using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Tests;

public sealed class FileAgentConfigurationSourceTests : IDisposable
{
    private readonly string _directoryPath;

    public FileAgentConfigurationSourceTests()
    {
        _directoryPath = Path.Combine(Path.GetTempPath(), "botnexus-file-config-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directoryPath);
    }

    [Fact]
    public async Task LoadAsync_WithNoJsonFiles_ReturnsEmptyList()
    {
        var logger = new ListLogger<FileAgentConfigurationSource>();
        var source = new FileAgentConfigurationSource(_directoryPath, logger);

        var descriptors = await source.LoadAsync();

        descriptors.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadAsync_WithValidJson_MapsAllFields()
    {
        File.WriteAllText(
            Path.Combine(_directoryPath, "agent-a.json"),
            """
            {
              "agentId": "agent-a",
              "displayName": "Agent A",
              "description": "Primary agent",
              "modelId": "model-x",
              "apiProvider": "provider-x",
              "systemPrompt": "Be concise",
              "toolIds": ["tool-1", "tool-2"],
              "isolationStrategy": "process",
              "maxConcurrentSessions": 3,
              "metadata": {
                "owner": "gateway",
                "priority": 7,
                "enabled": true,
                "labels": ["alpha", "beta"],
                "nested": { "kind": "test" }
              },
              "isolationOptions": {
                "timeoutMs": 2500,
                "sandbox": false
              }
            }
            """);

        var source = new FileAgentConfigurationSource(_directoryPath, new ListLogger<FileAgentConfigurationSource>());

        var descriptor = (await source.LoadAsync()).Should().ContainSingle().Subject;

        descriptor.AgentId.Should().Be("agent-a");
        descriptor.DisplayName.Should().Be("Agent A");
        descriptor.Description.Should().Be("Primary agent");
        descriptor.ModelId.Should().Be("model-x");
        descriptor.ApiProvider.Should().Be("provider-x");
        descriptor.SystemPrompt.Should().Be("Be concise");
        descriptor.ToolIds.Should().Equal("tool-1", "tool-2");
        descriptor.IsolationStrategy.Should().Be("process");
        descriptor.MaxConcurrentSessions.Should().Be(3);
        descriptor.Metadata["owner"].Should().Be("gateway");
        descriptor.Metadata["priority"].Should().Be(7L);
        descriptor.Metadata["enabled"].Should().Be(true);
        ((object?[])descriptor.Metadata["labels"]!).Should().Equal("alpha", "beta");
        ((IReadOnlyDictionary<string, object?>)descriptor.Metadata["nested"]!)["kind"].Should().Be("test");
        descriptor.IsolationOptions["timeoutMs"].Should().Be(2500L);
        descriptor.IsolationOptions["sandbox"].Should().Be(false);
    }

    [Fact]
    public async Task LoadAsync_WithRelativeSystemPromptFile_LoadsPromptContent()
    {
        var promptDirectory = Path.Combine(_directoryPath, "prompts");
        Directory.CreateDirectory(promptDirectory);
        File.WriteAllText(Path.Combine(promptDirectory, "system.txt"), "Prompt from file");
        File.WriteAllText(
            Path.Combine(_directoryPath, "agent-a.json"),
            """
            {
              "agentId": "agent-a",
              "displayName": "Agent A",
              "modelId": "model-x",
              "apiProvider": "provider-x",
              "systemPromptFile": "prompts/system.txt"
            }
            """);

        var source = new FileAgentConfigurationSource(_directoryPath, new ListLogger<FileAgentConfigurationSource>());

        var descriptor = (await source.LoadAsync()).Should().ContainSingle().Subject;

        descriptor.SystemPrompt.Should().Be("Prompt from file");
        descriptor.SystemPromptFile.Should().BeNull();
    }

    [Fact]
    public async Task LoadAsync_WithPathTraversalSystemPromptFile_RejectsDescriptor()
    {
        var logger = new ListLogger<FileAgentConfigurationSource>();
        File.WriteAllText(
            Path.Combine(_directoryPath, "agent-a.json"),
            """
            {
              "agentId": "agent-a",
              "displayName": "Agent A",
              "modelId": "model-x",
              "apiProvider": "provider-x",
              "systemPromptFile": "../../etc/passwd"
            }
            """);

        var source = new FileAgentConfigurationSource(_directoryPath, logger);

        var descriptors = await source.LoadAsync();

        descriptors.Should().BeEmpty();
        logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Warning &&
            e.Message.Contains("Path traversal blocked", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoadAsync_WithAbsoluteSystemPromptFileOutsideConfigDirectory_RejectsDescriptor()
    {
        var logger = new ListLogger<FileAgentConfigurationSource>();
        var outsideDirectory = Path.Combine(_directoryPath, "..", "outside");
        Directory.CreateDirectory(outsideDirectory);
        var outsidePromptPath = Path.GetFullPath(Path.Combine(outsideDirectory, "outside-prompt.txt"));

        try
        {
            File.WriteAllText(outsidePromptPath, "Outside prompt");
            File.WriteAllText(
                Path.Combine(_directoryPath, "agent-a.json"),
                $$"""
                {
                  "agentId": "agent-a",
                  "displayName": "Agent A",
                  "modelId": "model-x",
                  "apiProvider": "provider-x",
                  "systemPromptFile": "{{outsidePromptPath.Replace("\\", "\\\\")}}"
                }
                """);

            var source = new FileAgentConfigurationSource(_directoryPath, logger);

            var descriptors = await source.LoadAsync();

            descriptors.Should().BeEmpty();
            logger.Entries.Should().Contain(e =>
                e.Level == LogLevel.Warning &&
                e.Message.Contains("Path traversal blocked", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(outsideDirectory))
                Directory.Delete(outsideDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_WithAbsoluteSystemPromptFileWithinConfigDirectory_LoadsPromptContent()
    {
        var promptDirectory = Path.Combine(_directoryPath, "prompts");
        Directory.CreateDirectory(promptDirectory);
        var promptPath = Path.GetFullPath(Path.Combine(promptDirectory, "system-absolute.txt"));
        File.WriteAllText(promptPath, "Absolute prompt from file");
        File.WriteAllText(
            Path.Combine(_directoryPath, "agent-a.json"),
            $$"""
            {
              "agentId": "agent-a",
              "displayName": "Agent A",
              "modelId": "model-x",
              "apiProvider": "provider-x",
              "systemPromptFile": "{{promptPath.Replace("\\", "\\\\")}}"
            }
            """);

        var source = new FileAgentConfigurationSource(_directoryPath, new ListLogger<FileAgentConfigurationSource>());

        var descriptor = (await source.LoadAsync()).Should().ContainSingle().Subject;

        descriptor.SystemPrompt.Should().Be("Absolute prompt from file");
        descriptor.SystemPromptFile.Should().BeNull();
    }

    [Fact]
    public async Task LoadAsync_WithMalformedJson_SkipsFileAndLogsWarning()
    {
        var logger = new ListLogger<FileAgentConfigurationSource>();
        File.WriteAllText(Path.Combine(_directoryPath, "bad.json"), "{ \"agentId\": ");
        File.WriteAllText(
            Path.Combine(_directoryPath, "good.json"),
            """
            {
              "agentId": "good",
              "displayName": "Good",
              "modelId": "model",
              "apiProvider": "provider"
            }
            """);

        var source = new FileAgentConfigurationSource(_directoryPath, logger);

        var descriptors = await source.LoadAsync();

        descriptors.Should().ContainSingle(d => d.AgentId == "good");
        logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Warning &&
            e.Message.Contains("Skipping malformed agent config file", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoadAsync_WithInvalidDescriptor_SkipsDescriptorAndLogsWarning()
    {
        var logger = new ListLogger<FileAgentConfigurationSource>();
        File.WriteAllText(
            Path.Combine(_directoryPath, "invalid.json"),
            """
            {
              "displayName": "No Id",
              "modelId": "model",
              "apiProvider": "provider"
            }
            """);

        var source = new FileAgentConfigurationSource(_directoryPath, logger);

        var descriptors = await source.LoadAsync();

        descriptors.Should().BeEmpty();
        logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Warning &&
            e.Message.Contains("validation errors", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoadAsync_WithSubAgentsAndSubAgentIds_MergesDistinctValues()
    {
        File.WriteAllText(
            Path.Combine(_directoryPath, "agent-a.json"),
            """
            {
              "agentId": "agent-a",
              "displayName": "Agent A",
              "modelId": "model",
              "apiProvider": "provider",
              "subAgents": ["child-1", "child-2"],
              "subAgentIds": ["CHILD-2", "child-3"]
            }
            """);

        var source = new FileAgentConfigurationSource(_directoryPath, new ListLogger<FileAgentConfigurationSource>());

        var descriptor = (await source.LoadAsync()).Should().ContainSingle().Subject;

        descriptor.SubAgentIds.Should().Equal("child-1", "child-2", "child-3");
    }

    [Fact]
    public void Watch_WithExistingDirectory_ReturnsDisposableWatcher()
    {
        var source = new FileAgentConfigurationSource(_directoryPath, new ListLogger<FileAgentConfigurationSource>());

        var watcher = source.Watch(_ => { });

        watcher.Should().NotBeNull();
        watcher!.Dispose();
    }

    [Fact]
    public async Task Watch_WhenConfigFileChanges_InvokesCallbackWithUpdatedDescriptors()
    {
        var source = new FileAgentConfigurationSource(_directoryPath, new ListLogger<FileAgentConfigurationSource>());
        var callback = new TaskCompletionSource<IReadOnlyList<AgentDescriptor>>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = source.Watch(descriptors => callback.TrySetResult(descriptors));

        var configPath = Path.Combine(_directoryPath, "agent-a.json");
        File.WriteAllText(
            configPath,
            """
            {
              "agentId": "agent-a",
              "displayName": "Agent A",
              "modelId": "model",
              "apiProvider": "provider"
            }
            """);
        await Task.Delay(100);
        File.AppendAllText(configPath, Environment.NewLine);

        var completed = await Task.WhenAny(callback.Task, Task.Delay(TimeSpan.FromSeconds(5)));

        completed.Should().Be(callback.Task);
        var descriptors = await callback.Task;
        descriptors.Should().ContainSingle(d => d.AgentId == "agent-a");
    }

    public void Dispose()
    {
        if (!Directory.Exists(_directoryPath))
            return;

        for (var i = 0; i < 3; i++)
        {
            try
            {
                Directory.Delete(_directoryPath, recursive: true);
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

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel)
            => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
