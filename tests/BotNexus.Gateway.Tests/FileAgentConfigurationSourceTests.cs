using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Logging;
using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;

namespace BotNexus.Gateway.Tests;

public sealed class FileAgentConfigurationSourceTests : IDisposable
{
    private readonly string _directoryPath;
    private readonly MockFileSystem _fileSystem;

    public FileAgentConfigurationSourceTests()
    {
        _fileSystem = new MockFileSystem();
        _directoryPath = Path.Combine(Path.GetTempPath(), "botnexus", "file-config-tests");
        _fileSystem.Directory.CreateDirectory(_directoryPath);
    }

    [Fact]
    public async Task LoadAsync_WithNoJsonFiles_ReturnsEmptyList()
    {
        var logger = new ListLogger<FileAgentConfigurationSource>();
        var source = new FileAgentConfigurationSource(_directoryPath, logger, _fileSystem);

        var descriptors = await source.LoadAsync();

        descriptors.ShouldBeEmpty();
    }

    [Fact]
    public async Task LoadAsync_WithValidJson_MapsAllFields()
    {
        _fileSystem.File.WriteAllText(
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

        var source = new FileAgentConfigurationSource(_directoryPath, new ListLogger<FileAgentConfigurationSource>(), _fileSystem);

        var descriptor = (await source.LoadAsync()).ShouldHaveSingleItem();

        descriptor.AgentId.Value.ShouldBe("agent-a");
        descriptor.DisplayName.ShouldBe("Agent A");
        descriptor.Description.ShouldBe("Primary agent");
        descriptor.ModelId.ShouldBe("model-x");
        descriptor.ApiProvider.ShouldBe("provider-x");
        descriptor.SystemPrompt.ShouldBe("Be concise");
        descriptor.ToolIds.ShouldBe(new[] { "tool-1", "tool-2" });
        descriptor.IsolationStrategy.ShouldBe("process");
        descriptor.MaxConcurrentSessions.ShouldBe(3);
        descriptor.Metadata["owner"].ShouldBe("gateway");
        descriptor.Metadata["priority"].ShouldBe(7L);
        descriptor.Metadata["enabled"].ShouldBe(true);
        ((object?[])descriptor.Metadata["labels"]!).ShouldBe(new[] { "alpha", "beta" });
        ((IReadOnlyDictionary<string, object?>)descriptor.Metadata["nested"]!)["kind"].ShouldBe("test");
        descriptor.IsolationOptions["timeoutMs"].ShouldBe(2500L);
        descriptor.IsolationOptions["sandbox"].ShouldBe(false);
    }

    [Fact]
    public async Task LoadAsync_WithFileAccess_MapsFileAccessPolicy()
    {
        var testDocsPath = Path.Combine(Path.GetTempPath(), "repos", "botnexus", "docs");
        var testArtifactsPath = Path.Combine(Path.GetTempPath(), "repos", "botnexus", "artifacts");
        var testSecretsPath = Path.Combine(testDocsPath, "secrets");
        
        _fileSystem.File.WriteAllText(
            Path.Combine(_directoryPath, "agent-a.json"),
            $$"""
            {
              "agentId": "agent-a",
              "displayName": "Agent A",
              "modelId": "model-x",
              "apiProvider": "provider-x",
              "fileAccess": {
                "allowedReadPaths": ["{{testDocsPath.Replace("\\", "\\\\")}}"],
                "allowedWritePaths": ["{{testArtifactsPath.Replace("\\", "\\\\")}}"],
                "deniedPaths": ["{{testSecretsPath.Replace("\\", "\\\\")}}"] 
              }
            }
            """);

        var source = new FileAgentConfigurationSource(_directoryPath, new ListLogger<FileAgentConfigurationSource>(), _fileSystem);

        var descriptor = (await source.LoadAsync()).ShouldHaveSingleItem();

        descriptor.FileAccess.ShouldNotBeNull();
        descriptor.FileAccess!.AllowedReadPaths.ShouldHaveSingleItem().ShouldBe(testDocsPath);
        descriptor.FileAccess.AllowedWritePaths.ShouldHaveSingleItem().ShouldBe(testArtifactsPath);
        descriptor.FileAccess.DeniedPaths.ShouldHaveSingleItem().ShouldBe(testSecretsPath);
    }

    [Fact]
    public async Task LoadAsync_WithRelativeSystemPromptFile_LoadsPromptContent()
    {
        var promptDirectory = Path.Combine(_directoryPath, "prompts");
        _fileSystem.Directory.CreateDirectory(promptDirectory);
        _fileSystem.File.WriteAllText(Path.Combine(promptDirectory, "system.txt"), "Prompt from file");
        _fileSystem.File.WriteAllText(
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

        var source = new FileAgentConfigurationSource(_directoryPath, new ListLogger<FileAgentConfigurationSource>(), _fileSystem);

        var descriptor = (await source.LoadAsync()).ShouldHaveSingleItem();

        descriptor.SystemPrompt.ShouldBe("Prompt from file");
        descriptor.SystemPromptFile.ShouldBeNull();
    }

    [Fact]
    public async Task LoadAsync_WithPathTraversalSystemPromptFile_RejectsDescriptor()
    {
        var logger = new ListLogger<FileAgentConfigurationSource>();
        _fileSystem.File.WriteAllText(
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

        var source = new FileAgentConfigurationSource(_directoryPath, logger, _fileSystem);

        var descriptors = await source.LoadAsync();

        descriptors.ShouldBeEmpty();
        logger.Entries.ShouldContain(e =>
            e.Level == LogLevel.Warning &&
            e.Message.Contains("Path traversal blocked", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoadAsync_WithAbsoluteSystemPromptFileOutsideConfigDirectory_RejectsDescriptor()
    {
        var logger = new ListLogger<FileAgentConfigurationSource>();
        var outsideDirectory = Path.Combine(_directoryPath, "..", "outside");
        _fileSystem.Directory.CreateDirectory(outsideDirectory);
        var outsidePromptPath = Path.GetFullPath(Path.Combine(outsideDirectory, "outside-prompt.txt"));

        try
        {
            _fileSystem.File.WriteAllText(outsidePromptPath, "Outside prompt");
            _fileSystem.File.WriteAllText(
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

            var source = new FileAgentConfigurationSource(_directoryPath, logger, _fileSystem);

            var descriptors = await source.LoadAsync();

            descriptors.ShouldBeEmpty();
            logger.Entries.ShouldContain(e =>
                e.Level == LogLevel.Warning &&
                e.Message.Contains("Path traversal blocked", StringComparison.Ordinal));
        }
        finally
        {
            if (_fileSystem.Directory.Exists(outsideDirectory))
                _fileSystem.Directory.Delete(outsideDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_WithAbsoluteSystemPromptFileWithinConfigDirectory_LoadsPromptContent()
    {
        var promptDirectory = Path.Combine(_directoryPath, "prompts");
        _fileSystem.Directory.CreateDirectory(promptDirectory);
        var promptPath = Path.GetFullPath(Path.Combine(promptDirectory, "system-absolute.txt"));
        _fileSystem.File.WriteAllText(promptPath, "Absolute prompt from file");
        _fileSystem.File.WriteAllText(
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

        var source = new FileAgentConfigurationSource(_directoryPath, new ListLogger<FileAgentConfigurationSource>(), _fileSystem);

        var descriptor = (await source.LoadAsync()).ShouldHaveSingleItem();

        descriptor.SystemPrompt.ShouldBe("Absolute prompt from file");
        descriptor.SystemPromptFile.ShouldBeNull();
    }

    [Fact]
    public async Task LoadAsync_WithMalformedJson_SkipsFileAndLogsWarning()
    {
        var logger = new ListLogger<FileAgentConfigurationSource>();
        _fileSystem.File.WriteAllText(Path.Combine(_directoryPath, "bad.json"), "{ \"agentId\": ");
        _fileSystem.File.WriteAllText(
            Path.Combine(_directoryPath, "good.json"),
            """
            {
              "agentId": "good",
              "displayName": "Good",
              "modelId": "model",
              "apiProvider": "provider"
            }
            """);

        var source = new FileAgentConfigurationSource(_directoryPath, logger, _fileSystem);

        var descriptors = await source.LoadAsync();

        descriptors.Where(d => d.AgentId == "good").ShouldHaveSingleItem();
        logger.Entries.ShouldContain(e =>
            e.Level == LogLevel.Warning &&
            e.Message.Contains("Skipping malformed agent config file", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoadAsync_WithInvalidDescriptor_SkipsDescriptorAndLogsWarning()
    {
        var logger = new ListLogger<FileAgentConfigurationSource>();
        _fileSystem.File.WriteAllText(
            Path.Combine(_directoryPath, "invalid.json"),
            """
            {
              "displayName": "No Id",
              "modelId": "model",
              "apiProvider": "provider"
            }
            """);

        var source = new FileAgentConfigurationSource(_directoryPath, logger, _fileSystem);

        var descriptors = await source.LoadAsync();

        descriptors.ShouldBeEmpty();
        logger.Entries.ShouldContain(e =>
            e.Level == LogLevel.Warning &&
            e.Message.Contains("Failed to load agent config file", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoadAsync_WithSubAgentsAndSubAgentIds_MergesDistinctValues()
    {
        _fileSystem.File.WriteAllText(
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

        var source = new FileAgentConfigurationSource(_directoryPath, new ListLogger<FileAgentConfigurationSource>(), _fileSystem);

        var descriptor = (await source.LoadAsync()).ShouldHaveSingleItem();

        descriptor.SubAgentIds.ShouldBe(new[] { "child-1", "child-2", "child-3" });
    }

    [Fact]
    public void Watch_WithExistingDirectory_ReturnsDisposableWatcher()
    {
        var watchDirectory = Path.Combine(Path.GetTempPath(), "botnexus-file-config-watch-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(watchDirectory);
        var source = new FileAgentConfigurationSource(watchDirectory, new ListLogger<FileAgentConfigurationSource>(), new FileSystem());

        try
        {
            var watcher = source.Watch(_ => { });

            watcher.ShouldNotBeNull();
            watcher!.Dispose();
        }
        finally
        {
            if (Directory.Exists(watchDirectory))
                Directory.Delete(watchDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Watch_WhenConfigFileChanges_InvokesCallbackWithUpdatedDescriptors()
    {
        var watchDirectory = Path.Combine(Path.GetTempPath(), "botnexus-file-config-watch-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(watchDirectory);
        var source = new FileAgentConfigurationSource(watchDirectory, new ListLogger<FileAgentConfigurationSource>(), new FileSystem());
        var callback = new TaskCompletionSource<IReadOnlyList<AgentDescriptor>>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            using var watcher = source.Watch(descriptors => callback.TrySetResult(descriptors));

            var configPath = Path.Combine(watchDirectory, "agent-a.json");
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

            var completed = await Task.WhenAny(callback.Task, Task.Delay(TimeSpan.FromSeconds(10)));

            completed.ShouldBe(callback.Task);
            var descriptors = await callback.Task;
            descriptors.Where(d => d.AgentId == "agent-a").ShouldHaveSingleItem();
        }
        finally
        {
            if (Directory.Exists(watchDirectory))
                Directory.Delete(watchDirectory, recursive: true);
        }
    }

    public void Dispose()
    {
        if (!_fileSystem.Directory.Exists(_directoryPath))
            return;

        for (var i = 0; i < 3; i++)
        {
            try
            {
                _fileSystem.Directory.Delete(_directoryPath, recursive: true);
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

