using System.Reflection;
using System.Text.Json;
using BotNexus.AgentCore.Tools;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Configuration;
using BotNexus.Gateway.Abstractions.Isolation;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Isolation;
using BotNexus.Gateway.Tools;
using BotNexus.Memory;
using BotNexus.Memory.Models;
using BotNexus.Tools;
using BotNexus.Providers.Core;
using BotNexus.Providers.Core.Models;
using BotNexus.Providers.Core.Registry;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.IO.Abstractions;

namespace BotNexus.Gateway.Tests;

public sealed class PlatformConfigAgentSourceTests : IDisposable
{
    private readonly string _configDirectory;

    public PlatformConfigAgentSourceTests()
    {
        _configDirectory = Path.Combine(Path.GetTempPath(), "botnexus-platform-agent-source-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_configDirectory);
    }

    [Fact]
    public async Task LoadAsync_WithEnabledAgents_MapsDescriptorAndPromptFiles()
    {
        var config = new PlatformConfig
        {
            Agents = new Dictionary<string, AgentDefinitionConfig>
            {
                ["assistant"] = new()
                {
                    Provider = "copilot",
                    DisplayName = "Assistant",
                    Description = "Helpful assistant",
                    Model = "gpt-4.1",
                    AllowedModels = ["gpt-4.1", "gpt-4o"],
                    SystemPromptFiles = ["AGENTS.md", "SOUL.md"],
                    SubAgents = ["helper-agent"],
                    IsolationStrategy = "remote",
                    MaxConcurrentSessions = 3,
                    Memory = new MemoryAgentConfig
                    {
                        Enabled = true,
                        Indexing = "auto",
                        Search = new MemorySearchAgentConfig
                        {
                            DefaultTopK = 7,
                            TemporalDecay = new TemporalDecayAgentConfig
                            {
                                Enabled = true,
                                HalfLifeDays = 21
                            }
                        }
                    },
                    Metadata = JsonSerializer.Deserialize<JsonElement>("{\"owner\":\"team-gateway\"}"),
                    IsolationOptions = JsonSerializer.Deserialize<JsonElement>("{\"timeoutMs\":1000}"),
                    Enabled = true
                },
                ["disabled-agent"] = new()
                {
                    Provider = "copilot",
                    Model = "gpt-4.1",
                    Enabled = false
                }
            }
        };

        var source = new PlatformConfigAgentSource(
            Options.Create(config),
            _configDirectory,
            new ListLogger<PlatformConfigAgentSource>());

        var descriptor = (await source.LoadAsync()).Should().ContainSingle().Subject;

        descriptor.AgentId.Should().Be("assistant");
        descriptor.DisplayName.Should().Be("Assistant");
        descriptor.Description.Should().Be("Helpful assistant");
        descriptor.ApiProvider.Should().Be("copilot");
        descriptor.ModelId.Should().Be("gpt-4.1");
        descriptor.AllowedModelIds.Should().Equal(["gpt-4.1", "gpt-4o"]);
        descriptor.SubAgentIds.Should().Equal(["helper-agent"]);
        descriptor.IsolationStrategy.Should().Be("remote");
        descriptor.MaxConcurrentSessions.Should().Be(3);
        descriptor.Metadata.Should().ContainKey("owner").WhoseValue.Should().Be("team-gateway");
        descriptor.IsolationOptions.Should().ContainKey("timeoutMs").WhoseValue.Should().Be(1000L);
        descriptor.SystemPrompt.Should().BeNull();
        descriptor.SystemPromptFiles.Should().Equal(["AGENTS.md", "SOUL.md"]);
        descriptor.Memory.Should().NotBeNull();
        descriptor.Memory!.Enabled.Should().BeTrue();
        descriptor.Memory.Indexing.Should().Be("auto");
        descriptor.Memory.Search.Should().NotBeNull();
        descriptor.Memory.Search!.DefaultTopK.Should().Be(7);
        descriptor.Memory.Search.TemporalDecay.Should().NotBeNull();
        descriptor.Memory.Search.TemporalDecay!.HalfLifeDays.Should().Be(21);
    }

    [Fact]
    public async Task LoadAsync_WithLegacySystemPromptFile_MapsSinglePromptFile()
    {
        var config = new PlatformConfig
        {
            Agents = new Dictionary<string, AgentDefinitionConfig>
            {
                ["assistant"] = new()
                {
                    Provider = "copilot",
                    Model = "gpt-4.1",
                    SystemPromptFile = @"prompts\missing.txt"
                }
            }
        };

        var source = new PlatformConfigAgentSource(Options.Create(config), _configDirectory, new ListLogger<PlatformConfigAgentSource>());

        var descriptor = (await source.LoadAsync()).Should().ContainSingle().Subject;

        descriptor.SystemPromptFile.Should().Be(@"prompts\missing.txt");
        descriptor.SystemPromptFiles.Should().Equal([@"prompts\missing.txt"]);
    }

    [Fact]
    public async Task LoadAsync_WithFileAccessPolicy_MapsFileAccess()
    {
        var config = new PlatformConfig
        {
            Agents = new Dictionary<string, AgentDefinitionConfig>
            {
                ["assistant"] = new()
                {
                    Provider = "copilot",
                    Model = "gpt-4.1",
                    FileAccess = new FileAccessPolicyConfig
                    {
                        AllowedReadPaths = [@"Q:\repos\botnexus\docs"],
                        AllowedWritePaths = [@"Q:\repos\botnexus\artifacts"],
                        DeniedPaths = [@"Q:\repos\botnexus\docs\secrets"]
                    }
                }
            }
        };

        var source = new PlatformConfigAgentSource(Options.Create(config), _configDirectory, new ListLogger<PlatformConfigAgentSource>());

        var descriptor = (await source.LoadAsync()).Should().ContainSingle().Subject;

        descriptor.FileAccess.Should().NotBeNull();
        descriptor.FileAccess!.AllowedReadPaths.Should().Equal(@"Q:\repos\botnexus\docs");
        descriptor.FileAccess.AllowedWritePaths.Should().Equal(@"Q:\repos\botnexus\artifacts");
        descriptor.FileAccess.DeniedPaths.Should().Equal(@"Q:\repos\botnexus\docs\secrets");
    }

    [Fact]
    public async Task LoadAsync_WithLocationReferenceInAllowedReadPaths_ResolvesLocationPath()
    {
        var config = new PlatformConfig
        {
            Agents = new Dictionary<string, AgentDefinitionConfig>
            {
                ["assistant"] = new()
                {
                    Provider = "copilot",
                    Model = "gpt-4.1",
                    FileAccess = new FileAccessPolicyConfig
                    {
                        AllowedReadPaths = ["@repo-botnexus"]
                    }
                }
            }
        };

        var source = new PlatformConfigAgentSource(
            Options.Create(config),
            _configDirectory,
            new ListLogger<PlatformConfigAgentSource>(),
            new StubLocationResolver(new Dictionary<string, string>
            {
                ["repo-botnexus"] = @"Q:\repos\botnexus"
            }));

        var descriptor = (await source.LoadAsync()).Should().ContainSingle().Subject;

        descriptor.FileAccess.Should().NotBeNull();
        descriptor.FileAccess!.AllowedReadPaths.Should().Equal(Path.GetFullPath(@"Q:\repos\botnexus"));
    }

    [Fact]
    public async Task LoadAsync_WithLocationReferenceSubPath_ResolvesCombinedPath()
    {
        var config = new PlatformConfig
        {
            Agents = new Dictionary<string, AgentDefinitionConfig>
            {
                ["assistant"] = new()
                {
                    Provider = "copilot",
                    Model = "gpt-4.1",
                    FileAccess = new FileAccessPolicyConfig
                    {
                        AllowedReadPaths = ["@repo-botnexus/docs/planning"]
                    }
                }
            }
        };

        var source = new PlatformConfigAgentSource(
            Options.Create(config),
            _configDirectory,
            new ListLogger<PlatformConfigAgentSource>(),
            new StubLocationResolver(new Dictionary<string, string>
            {
                ["repo-botnexus"] = @"Q:\repos\botnexus"
            }));

        var descriptor = (await source.LoadAsync()).Should().ContainSingle().Subject;

        descriptor.FileAccess.Should().NotBeNull();
        descriptor.FileAccess!.AllowedReadPaths.Should().Equal(Path.GetFullPath(@"Q:\repos\botnexus\docs\planning"));
    }

    [Fact]
    public async Task LoadAsync_WithUnknownLocationReference_LogsWarningAndSkipsPath()
    {
        var logger = new ListLogger<PlatformConfigAgentSource>();
        var config = new PlatformConfig
        {
            Agents = new Dictionary<string, AgentDefinitionConfig>
            {
                ["assistant"] = new()
                {
                    Provider = "copilot",
                    Model = "gpt-4.1",
                    FileAccess = new FileAccessPolicyConfig
                    {
                        AllowedReadPaths = ["@missing"]
                    }
                }
            }
        };

        var source = new PlatformConfigAgentSource(
            Options.Create(config),
            _configDirectory,
            logger,
            new StubLocationResolver(new Dictionary<string, string>()));

        var descriptor = (await source.LoadAsync()).Should().ContainSingle().Subject;

        descriptor.FileAccess.Should().NotBeNull();
        descriptor.FileAccess!.AllowedReadPaths.Should().BeEmpty();
        logger.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Warning &&
            entry.Message.Contains("Skipping unresolved location reference '@missing'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoadAsync_WithMixedLocationReferencesAndRawPaths_MapsAllResolvedPaths()
    {
        var config = new PlatformConfig
        {
            Agents = new Dictionary<string, AgentDefinitionConfig>
            {
                ["assistant"] = new()
                {
                    Provider = "copilot",
                    Model = "gpt-4.1",
                    FileAccess = new FileAccessPolicyConfig
                    {
                        AllowedReadPaths = ["@repo-botnexus/docs", @"Q:\repos\shared"],
                        AllowedWritePaths = ["@repo-botnexus/artifacts"],
                        DeniedPaths = ["@repo-botnexus/.env", @"Q:\repos\shared\blocked"]
                    }
                }
            }
        };

        var source = new PlatformConfigAgentSource(
            Options.Create(config),
            _configDirectory,
            new ListLogger<PlatformConfigAgentSource>(),
            new StubLocationResolver(new Dictionary<string, string>
            {
                ["repo-botnexus"] = @"Q:\repos\botnexus"
            }));

        var descriptor = (await source.LoadAsync()).Should().ContainSingle().Subject;

        descriptor.FileAccess.Should().NotBeNull();
        descriptor.FileAccess!.AllowedReadPaths.Should().Equal(
            Path.GetFullPath(@"Q:\repos\botnexus\docs"),
            @"Q:\repos\shared");
        descriptor.FileAccess.AllowedWritePaths.Should().Equal(Path.GetFullPath(@"Q:\repos\botnexus\artifacts"));
        descriptor.FileAccess.DeniedPaths.Should().Equal(
            Path.GetFullPath(@"Q:\repos\botnexus\.env"),
            @"Q:\repos\shared\blocked");
    }

    [Fact]
    public async Task PlatformConfigAgentSource_LoadsAgents_ThatInProcessIsolationCanCreate()
    {
        var source = new PlatformConfigAgentSource(
            Options.Create(new PlatformConfig
            {
                Agents = new Dictionary<string, AgentDefinitionConfig>
                {
                    ["assistant"] = new()
                    {
                        Provider = "test-provider",
                        Model = "test-model",
                        IsolationStrategy = "in-process",
                        Enabled = true
                    }
                }
            }),
            _configDirectory,
            new ListLogger<PlatformConfigAgentSource>());

        var descriptor = (await source.LoadAsync()).Should().ContainSingle().Subject;

        var modelRegistry = new ModelRegistry();
        modelRegistry.Register("test-provider", new LlmModel(
            Id: "test-model",
            Name: "test-model",
            Api: "responses",
            Provider: "test-provider",
            BaseUrl: "https://llm.test",
            Reasoning: false,
            Input: ["text"],
            Cost: new ModelCost(0, 0, 0, 0),
            ContextWindow: 8192,
            MaxTokens: 1024));

        var strategy = new InProcessIsolationStrategy(
            new LlmClient(new ApiProviderRegistry(), modelRegistry),
            CreateGatewayAuthManagerWithTempAuthPath(),
            new PassthroughContextBuilder(),
            new StaticAgentToolFactory(),
            new TestWorkspaceManager(_configDirectory),
            new DefaultToolRegistry(Array.Empty<IAgentTool>()),
            new StubMemoryStoreFactory(),
            new ServiceCollection().BuildServiceProvider(),
            NullLogger<InProcessIsolationStrategy>.Instance);

        var handle = await strategy.CreateAsync(
            descriptor,
            new AgentExecutionContext { SessionId = BotNexus.Domain.Primitives.SessionId.From("session-1") });

        handle.AgentId.Should().Be("assistant");
        handle.SessionId.Should().Be("session-1");
    }

    [Fact]
    public async Task PlatformConfigAgentSource_FileAccessPolicy_FlowsToPathValidator()
    {
        var source = new PlatformConfigAgentSource(
            Options.Create(new PlatformConfig
            {
                Agents = new Dictionary<string, AgentDefinitionConfig>
                {
                    ["assistant"] = new()
                    {
                        Provider = "test-provider",
                        Model = "test-model",
                        IsolationStrategy = "in-process",
                        FileAccess = new FileAccessPolicyConfig
                        {
                            AllowedReadPaths = [@"Q:\repos\botnexus\docs"],
                            DeniedPaths = [@"Q:\repos\botnexus\docs\secrets"]
                        },
                        Enabled = true
                    }
                }
            }),
            _configDirectory,
            new ListLogger<PlatformConfigAgentSource>());

        var descriptor = (await source.LoadAsync()).Should().ContainSingle().Subject;
        var toolFactory = new CapturingAgentToolFactory();

        var modelRegistry = new ModelRegistry();
        modelRegistry.Register("test-provider", new LlmModel(
            Id: "test-model",
            Name: "test-model",
            Api: "responses",
            Provider: "test-provider",
            BaseUrl: "https://llm.test",
            Reasoning: false,
            Input: ["text"],
            Cost: new ModelCost(0, 0, 0, 0),
            ContextWindow: 8192,
            MaxTokens: 1024));

        var strategy = new InProcessIsolationStrategy(
            new LlmClient(new ApiProviderRegistry(), modelRegistry),
            CreateGatewayAuthManagerWithTempAuthPath(),
            new PassthroughContextBuilder(),
            toolFactory,
            new TestWorkspaceManager(_configDirectory),
            new DefaultToolRegistry(Array.Empty<IAgentTool>()),
            new StubMemoryStoreFactory(),
            new ServiceCollection().BuildServiceProvider(),
            NullLogger<InProcessIsolationStrategy>.Instance);

        _ = await strategy.CreateAsync(
            descriptor,
            new AgentExecutionContext { SessionId = BotNexus.Domain.Primitives.SessionId.From("session-1") });

        var pathValidator = toolFactory.CapturedPathValidator;
        pathValidator.Should().NotBeNull();
        pathValidator!.ValidateAndResolve(@"Q:\repos\botnexus\docs\guide.md", FileAccessMode.Read)
            .Should()
            .Be(@"Q:\repos\botnexus\docs\guide.md");
        pathValidator.ValidateAndResolve(@"Q:\repos\botnexus\docs\secrets\tokens.txt", FileAccessMode.Read)
            .Should()
            .BeNull();
    }

    [Fact]
    public void Watch_ReturnsSubscription()
    {
        var source = new PlatformConfigAgentSource(
            Options.Create(new PlatformConfig()),
            _configDirectory,
            new ListLogger<PlatformConfigAgentSource>());

        using var subscription = source.Watch(_ => { });

        subscription.Should().NotBeNull();
    }

    public void Dispose()
    {
        if (Directory.Exists(_configDirectory))
            Directory.Delete(_configDirectory, recursive: true);
    }

    private GatewayAuthManager CreateGatewayAuthManagerWithTempAuthPath()
    {
        var authManager = new GatewayAuthManager(new PlatformConfig(), NullLogger<GatewayAuthManager>.Instance, new FileSystem());
        var authPathField = typeof(GatewayAuthManager).GetField("_authFilePath", BindingFlags.NonPublic | BindingFlags.Instance);
        authPathField.Should().NotBeNull();
        authPathField!.SetValue(authManager, Path.Combine(_configDirectory, "auth.json"));
        return authManager;
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
            => Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class PassthroughContextBuilder : IContextBuilder
    {
        public Task<string> BuildSystemPromptAsync(AgentDescriptor descriptor, CancellationToken ct = default)
            => Task.FromResult(descriptor.SystemPrompt ?? string.Empty);
    }

    private sealed class StaticAgentToolFactory : IAgentToolFactory
    {
        public IReadOnlyList<IAgentTool> CreateTools(string workingDirectory, IPathValidator? pathValidator = null)
            => [new ReadTool(workingDirectory)];
    }

    private sealed class CapturingAgentToolFactory : IAgentToolFactory
    {
        public IPathValidator? CapturedPathValidator { get; private set; }

        public IReadOnlyList<IAgentTool> CreateTools(string workingDirectory, IPathValidator? pathValidator = null)
        {
            CapturedPathValidator = pathValidator;
            return [];
        }
    }

    private sealed class TestWorkspaceManager : IAgentWorkspaceManager
    {
        private readonly string _workspacePath;

        public TestWorkspaceManager(string workspacePath)
        {
            _workspacePath = workspacePath;
        }

        public Task<AgentWorkspace> LoadWorkspaceAsync(string agentName, CancellationToken cancellationToken = default)
            => Task.FromResult(new AgentWorkspace(agentName, string.Empty, string.Empty, string.Empty, string.Empty));

        public Task SaveMemoryAsync(string agentName, string content, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public string GetWorkspacePath(string agentName)
            => _workspacePath;
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }

    private sealed class StubMemoryStoreFactory : IMemoryStoreFactory
    {
        private readonly IMemoryStore _store = new StubMemoryStore();

        public IMemoryStore Create(string agentId) => _store;
    }

    private sealed class StubMemoryStore : IMemoryStore
    {
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<MemoryEntry> InsertAsync(MemoryEntry entry, CancellationToken ct = default) => Task.FromResult(entry);
        public Task<MemoryEntry?> GetByIdAsync(string id, CancellationToken ct = default) => Task.FromResult<MemoryEntry?>(null);
        public Task<IReadOnlyList<MemoryEntry>> GetBySessionAsync(string sessionId, int limit = 20, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<MemoryEntry>>([]);
        public Task<IReadOnlyList<MemoryEntry>> SearchAsync(string query, int topK = 10, MemorySearchFilter? filter = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<MemoryEntry>>([]);
        public Task DeleteAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<MemoryStoreStats> GetStatsAsync(CancellationToken ct = default) => Task.FromResult(new MemoryStoreStats(0, 0, null));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubLocationResolver(IReadOnlyDictionary<string, string> paths) : ILocationResolver
    {
        private readonly IReadOnlyDictionary<string, string> _paths = paths;

        public Location? Resolve(string locationName)
            => null;

        public string? ResolvePath(string locationName)
            => _paths.TryGetValue(locationName, out var path) ? path : null;

        public IReadOnlyList<Location> GetAll()
            => [];
    }
}
