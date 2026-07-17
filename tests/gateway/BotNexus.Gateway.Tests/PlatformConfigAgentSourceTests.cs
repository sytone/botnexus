using System.Reflection;
using System.Text.Json;
using BotNexus.Agent.Core.Tools;
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
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.IO.Abstractions;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Tests.TestInfrastructure;

namespace BotNexus.Gateway.Tests;

public sealed class PlatformConfigAgentSourceTests : IDisposable
{
    private readonly string _configDirectory;
    private static readonly string s_repoBotnexusPath = Path.Combine(Path.GetTempPath(), "repos", "botnexus");
    private static readonly string s_reposSharedPath = Path.Combine(Path.GetTempPath(), "repos", "shared");

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
                    Emoji = "✨",
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
            new TestOptionsMonitor<PlatformConfig>(config),
            _configDirectory,
            new ListLogger<PlatformConfigAgentSource>());

        var descriptor = (await source.LoadAsync()).ShouldHaveSingleItem();

        descriptor.AgentId.Value.ShouldBe("assistant");
        descriptor.DisplayName.ShouldBe("Assistant");
        descriptor.Emoji.ShouldBe("✨");
        descriptor.Description.ShouldBe("Helpful assistant");
        descriptor.ApiProvider.ShouldBe("copilot");
        descriptor.ModelId.ShouldBe("gpt-4.1");
        descriptor.AllowedModelIds.ShouldBe(new[] { "gpt-4.1", "gpt-4o" });
        descriptor.SubAgentIds.ShouldBe(new[] { "helper-agent" });
        descriptor.IsolationStrategy.ShouldBe("remote");
        descriptor.MaxConcurrentSessions.ShouldBe(3);
        descriptor.Metadata.ShouldContainKey("owner");
        descriptor.Metadata["owner"].ShouldBe("team-gateway");
        descriptor.IsolationOptions.ShouldContainKey("timeoutMs");
        descriptor.IsolationOptions["timeoutMs"].ShouldBe(1000L);
        descriptor.SystemPrompt.ShouldBeNull();
        descriptor.SystemPromptFiles.ShouldBe(new[] { "AGENTS.md", "SOUL.md" });
        descriptor.Memory.ShouldNotBeNull();
        descriptor.Memory!.Enabled.ShouldBeTrue();
        descriptor.Memory.Indexing.ShouldBe("auto");
        descriptor.Memory.Search.ShouldNotBeNull();
        descriptor.Memory.Search!.DefaultTopK.ShouldBe(7);
        descriptor.Memory.Search.TemporalDecay.ShouldNotBeNull();
        descriptor.Memory.Search.TemporalDecay!.HalfLifeDays.ShouldBe(21);
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

        var source = new PlatformConfigAgentSource(new TestOptionsMonitor<PlatformConfig>(config), _configDirectory, new ListLogger<PlatformConfigAgentSource>());

        var descriptor = (await source.LoadAsync()).ShouldHaveSingleItem();

        descriptor.SystemPromptFile.ShouldBe(@"prompts\missing.txt");
        descriptor.SystemPromptFiles.ShouldBe(new[] { @"prompts\missing.txt" });
    }

    [Fact]
    public async Task LoadAsync_WithoutKindField_DefaultsToNamed_BackCompat()
    {
        var config = new PlatformConfig
        {
            Agents = new Dictionary<string, AgentDefinitionConfig>
            {
                ["legacy"] = new() { Provider = "copilot", Model = "gpt-4.1" }
            }
        };

        var source = new PlatformConfigAgentSource(new TestOptionsMonitor<PlatformConfig>(config), _configDirectory, new ListLogger<PlatformConfigAgentSource>());

        var descriptor = (await source.LoadAsync()).ShouldHaveSingleItem();

        descriptor.Kind.ShouldBe(AgentKind.Named,
            "Platform-config entries omitting 'kind' must default to Named so existing fleet " +
            "configurations don't shift semantics on upgrade.");
    }

    [Fact]
    public async Task LoadAsync_WithExplicitKindSubAgent_RejectsDescriptorAndLogsWarning()
    {
        // PRIMARY rejection invariant for the platform-config path: Kind = SubAgent must
        // never come from configuration. If an operator (or attacker who can edit
        // botnexus.json) supplies "kind": "SubAgent" the descriptor MUST be dropped at
        // load time so it never reaches the registry — otherwise the sub-agent-internal
        // tool surface would attach to what is, semantically, a named agent.
        var logger = new ListLogger<PlatformConfigAgentSource>();
        var config = new PlatformConfig
        {
            Agents = new Dictionary<string, AgentDefinitionConfig>
            {
                ["smuggled"] = new()
                {
                    Provider = "copilot",
                    Model = "gpt-4.1",
                    Kind = AgentKind.SubAgent
                }
            }
        };

        var source = new PlatformConfigAgentSource(new TestOptionsMonitor<PlatformConfig>(config), _configDirectory, logger);

        var descriptors = await source.LoadAsync();

        descriptors.ShouldBeEmpty(
            "Platform-config 'kind: SubAgent' MUST be rejected. If this fails, the platform " +
            "config path is now a vector to bypass the runtime-only sub-agent invariant.");
        logger.Entries.ShouldContain(e =>
            e.Level == LogLevel.Error &&
            e.Message.Contains("validation", StringComparison.OrdinalIgnoreCase),
            "Rejection must surface as an Error log so operators can detect misconfiguration.");
    }

    [Fact]
    public async Task LoadAsync_WithInvalidThinking_SkipsOnlyInvalidAgentAndLogsError()
    {
        var logger = new ListLogger<PlatformConfigAgentSource>();
        var config = new PlatformConfig
        {
            Agents = new Dictionary<string, AgentDefinitionConfig>
            {
                ["valid"] = new() { Provider = "copilot", Model = "gpt-4.1" },
                ["invalid"] = new() { Provider = "copilot", Model = "gpt-4.1", Thinking = "warp-speed" }
            }
        };

        var source = new PlatformConfigAgentSource(
            new TestOptionsMonitor<PlatformConfig>(config),
            _configDirectory,
            logger);

        var descriptors = await source.LoadAsync();

        descriptors.Select(descriptor => descriptor.AgentId.Value).ShouldBe(["valid"]);
        logger.Entries.ShouldContain(entry =>
            entry.Level == LogLevel.Error &&
            entry.Message.Contains("invalid", StringComparison.Ordinal) &&
            entry.Message.Contains("Skipping", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoadAsync_WithExplicitKindNamed_AcceptsDescriptor()
    {
        var config = new PlatformConfig
        {
            Agents = new Dictionary<string, AgentDefinitionConfig>
            {
                ["named"] = new()
                {
                    Provider = "copilot",
                    Model = "gpt-4.1",
                    Kind = AgentKind.Named
                }
            }
        };

        var source = new PlatformConfigAgentSource(new TestOptionsMonitor<PlatformConfig>(config), _configDirectory, new ListLogger<PlatformConfigAgentSource>());

        var descriptor = (await source.LoadAsync()).ShouldHaveSingleItem();

        descriptor.Kind.ShouldBe(AgentKind.Named);
    }

    [Fact]
    public void LoadAsync_WithKindAsIntegerOneInJson_RejectsDescriptor()
    {
        // End-to-end strict-converter check through JSON deserialization into PlatformConfig.
        // A JSON config supplying "kind": 1 (which would resolve to SubAgent under the
        // default System.Text.Json behaviour) must fail to bind to the strict
        // AgentKindJsonConverter — otherwise integer smuggling could bypass the
        // string-form rejection guard.
        var configDirectory = Path.Combine(Path.GetTempPath(), "botnexus-platform-int-kind", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(configDirectory);
        try
        {
            const string rawJson = """
                {
                  "agents": {
                    "smuggle": {
                      "provider": "copilot",
                      "model": "gpt-4.1",
                      "kind": 1
                    }
                  }
                }
                """;

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var bind = () => JsonSerializer.Deserialize<PlatformConfig>(rawJson, options);

            var ex = Should.Throw<JsonException>(bind);
            ex.Message.ShouldContain("AgentKind");
        }
        finally
        {
            if (Directory.Exists(configDirectory))
                Directory.Delete(configDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_WithWorldDefaults_MergesIntoAgentExtensionConfig()
    {
        var config = JsonSerializer.Deserialize<PlatformConfig>(
            """
            {
              "Gateway": {
                "Extensions": {
                  "Defaults": {
                    "ext": { "a": 1 }
                  }
                }
              },
              "Agents": {
                "assistant": {
                  "Provider": "copilot",
                  "Model": "gpt-4.1",
                  "Enabled": true
                }
              }
            }
            """)!;

        var source = new PlatformConfigAgentSource(
            new TestOptionsMonitor<PlatformConfig>(config),
            _configDirectory,
            new ListLogger<PlatformConfigAgentSource>());

        var descriptor = (await source.LoadAsync()).ShouldHaveSingleItem();

        descriptor.ExtensionConfig.ShouldContainKey("ext");
        AssertJsonEquals(descriptor.ExtensionConfig["ext"], """{"a":1}""");
    }

    [Fact]
    public async Task LoadAsync_WithWorldDefaultsAndAgentOverrides_DeepMerges()
    {
        var config = JsonSerializer.Deserialize<PlatformConfig>(
            """
            {
              "Gateway": {
                "Extensions": {
                  "Defaults": {
                    "ext": { "a": 1, "b": 2 }
                  }
                }
              },
              "Agents": {
                "assistant": {
                  "Provider": "copilot",
                  "Model": "gpt-4.1",
                  "Enabled": true,
                  "Extensions": {
                    "ext": { "b": 3, "c": 4 }
                  }
                }
              }
            }
            """)!;

        var source = new PlatformConfigAgentSource(
            new TestOptionsMonitor<PlatformConfig>(config),
            _configDirectory,
            new ListLogger<PlatformConfigAgentSource>());

        var descriptor = (await source.LoadAsync()).ShouldHaveSingleItem();

        descriptor.ExtensionConfig.ShouldContainKey("ext");
        AssertJsonEquals(descriptor.ExtensionConfig["ext"], """{"a":1,"b":3,"c":4}""");
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

        var source = new PlatformConfigAgentSource(new TestOptionsMonitor<PlatformConfig>(config), _configDirectory, new ListLogger<PlatformConfigAgentSource>());

        var descriptor = (await source.LoadAsync()).ShouldHaveSingleItem();

        descriptor.FileAccess.ShouldNotBeNull();
        descriptor.FileAccess!.AllowedReadPaths.ShouldHaveSingleItem().ShouldBe(@"Q:\repos\botnexus\docs");
        descriptor.FileAccess.AllowedWritePaths.ShouldHaveSingleItem().ShouldBe(@"Q:\repos\botnexus\artifacts");
        descriptor.FileAccess.DeniedPaths.ShouldHaveSingleItem().ShouldBe(@"Q:\repos\botnexus\docs\secrets");
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
            new TestOptionsMonitor<PlatformConfig>(config),
            _configDirectory,
            new ListLogger<PlatformConfigAgentSource>(),
            new StubLocationResolver(new Dictionary<string, string>
            {
                ["repo-botnexus"] = @"Q:\repos\botnexus"
            }));

        var descriptor = (await source.LoadAsync()).ShouldHaveSingleItem();

        descriptor.FileAccess.ShouldNotBeNull();
        descriptor.FileAccess!.AllowedReadPaths.ShouldHaveSingleItem().ShouldBe(Path.GetFullPath(@"Q:\repos\botnexus"));
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
            new TestOptionsMonitor<PlatformConfig>(config),
            _configDirectory,
            new ListLogger<PlatformConfigAgentSource>(),
            new StubLocationResolver(new Dictionary<string, string>
            {
                ["repo-botnexus"] = s_repoBotnexusPath
            }));

        var descriptor = (await source.LoadAsync()).ShouldHaveSingleItem();

        descriptor.FileAccess.ShouldNotBeNull();
        descriptor.FileAccess!.AllowedReadPaths.ShouldHaveSingleItem().ShouldBe(Path.GetFullPath(Path.Combine(s_repoBotnexusPath, "docs", "planning")));
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
            new TestOptionsMonitor<PlatformConfig>(config),
            _configDirectory,
            logger,
            new StubLocationResolver(new Dictionary<string, string>()));

        var descriptor = (await source.LoadAsync()).ShouldHaveSingleItem();

        descriptor.FileAccess.ShouldNotBeNull();
        descriptor.FileAccess!.AllowedReadPaths.ShouldBeEmpty();
        logger.Entries.ShouldContain(entry =>
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
                        AllowedReadPaths = ["@repo-botnexus/docs", s_reposSharedPath],
                        AllowedWritePaths = ["@repo-botnexus/artifacts"],
                        DeniedPaths = ["@repo-botnexus/.env", Path.Combine(s_reposSharedPath, "blocked")]
                    }
                }
            }
        };

        var source = new PlatformConfigAgentSource(
            new TestOptionsMonitor<PlatformConfig>(config),
            _configDirectory,
            new ListLogger<PlatformConfigAgentSource>(),
            new StubLocationResolver(new Dictionary<string, string>
            {
                ["repo-botnexus"] = s_repoBotnexusPath
            }));

        var descriptor = (await source.LoadAsync()).ShouldHaveSingleItem();

        descriptor.FileAccess.ShouldNotBeNull();
        descriptor.FileAccess!.AllowedReadPaths.ShouldBe(new[] {
            Path.GetFullPath(Path.Combine(s_repoBotnexusPath, "docs")),
            s_reposSharedPath }, ignoreOrder: false);
        descriptor.FileAccess.AllowedWritePaths.ShouldHaveSingleItem().ShouldBe(Path.GetFullPath(Path.Combine(s_repoBotnexusPath, "artifacts")));
        descriptor.FileAccess.DeniedPaths.ShouldBe(new[] {
            Path.GetFullPath(Path.Combine(s_repoBotnexusPath, ".env")),
            Path.Combine(s_reposSharedPath, "blocked") }, ignoreOrder: false);
    }

    [Fact]
    public async Task PlatformConfigAgentSource_LoadsAgents_ThatInProcessIsolationCanCreate()
    {
        var source = new PlatformConfigAgentSource(
            new TestOptionsMonitor<PlatformConfig>(new PlatformConfig
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

        var descriptor = (await source.LoadAsync()).ShouldHaveSingleItem();

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
            Array.Empty<IAgentToolContributor>(),
            new StubMemoryStoreFactory(),
            new StubAgentMemoryFactory(),
            new ServiceCollection().BuildServiceProvider(),
            NullLogger<InProcessIsolationStrategy>.Instance);

        var handle = await strategy.CreateAsync(
            descriptor,
            new AgentExecutionContext { SessionId = BotNexus.Domain.Primitives.SessionId.From("session-1") });

        handle.AgentId.Value.ShouldBe("assistant");
        handle.SessionId.Value.ShouldBe("session-1");
    }

    [Fact]
    public async Task PlatformConfigAgentSource_FileAccessPolicy_FlowsToPathValidator()
    {
        var source = new PlatformConfigAgentSource(
            new TestOptionsMonitor<PlatformConfig>(new PlatformConfig
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
                            AllowedReadPaths = [Path.Combine(s_repoBotnexusPath, "docs")],
                            DeniedPaths = [Path.Combine(s_repoBotnexusPath, "docs", "secrets")]
                        },
                        Enabled = true
                    }
                }
            }),
            _configDirectory,
            new ListLogger<PlatformConfigAgentSource>());

        var descriptor = (await source.LoadAsync()).ShouldHaveSingleItem();
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
            Array.Empty<IAgentToolContributor>(),
            new StubMemoryStoreFactory(),
            new StubAgentMemoryFactory(),
            new ServiceCollection().BuildServiceProvider(),
            NullLogger<InProcessIsolationStrategy>.Instance);

        _ = await strategy.CreateAsync(
            descriptor,
            new AgentExecutionContext { SessionId = BotNexus.Domain.Primitives.SessionId.From("session-1") });

        var pathValidator = toolFactory.CapturedPathValidator;
        pathValidator.ShouldNotBeNull();
        var guideFile = Path.Combine(s_repoBotnexusPath, "docs", "guide.md");
        var secretsFile = Path.Combine(s_repoBotnexusPath, "docs", "secrets", "tokens.txt");
        pathValidator!.ValidateAndResolve(guideFile, FileAccessMode.Read)
            .ShouldBe(guideFile);
        pathValidator.ValidateAndResolve(secretsFile, FileAccessMode.Read)
            .ShouldBeNull();
    }

    [Fact]
    public void Watch_ReturnsSubscription()
    {
        var source = new PlatformConfigAgentSource(
            new TestOptionsMonitor<PlatformConfig>(new PlatformConfig()),
            _configDirectory,
            new ListLogger<PlatformConfigAgentSource>());

        using var subscription = source.Watch(_ => { });

        subscription.ShouldNotBeNull();
    }

    [Fact]
    public void Watch_WhenOptionsMonitorFires_InvokesCallback()
    {
        var monitor = new TestOptionsMonitor<PlatformConfig>(new PlatformConfig());
        var source = new PlatformConfigAgentSource(
            monitor,
            _configDirectory,
            new ListLogger<PlatformConfigAgentSource>());

        IReadOnlyList<AgentDescriptor>? received = null;
        using var subscription = source.Watch(descriptors => received = descriptors);

        var updatedConfig = new PlatformConfig
        {
            Agents = new Dictionary<string, AgentDefinitionConfig>
            {
                ["new-agent"] = new AgentDefinitionConfig
                {
                    Provider = "openai",
                    Model = "gpt-4.1",
                    Enabled = true
                }
            }
        };
        monitor.RaiseChanged(updatedConfig);

        received.ShouldNotBeNull();
        received!.ShouldContain(d => d.AgentId.Value == "new-agent");
    }

    public void Dispose()
    {
        if (Directory.Exists(_configDirectory))
            Directory.Delete(_configDirectory, recursive: true);
    }

    private GatewayAuthManager CreateGatewayAuthManagerWithTempAuthPath()
    {
        var authManager = new GatewayAuthManager(new StaticOptionsMonitor<PlatformConfig>(new PlatformConfig()), NullLogger<GatewayAuthManager>.Instance, new FileSystem());
        var authPathField = typeof(GatewayAuthManager).GetField("_authFilePath", BindingFlags.NonPublic | BindingFlags.Instance);
        authPathField.ShouldNotBeNull();
        authPathField!.SetValue(authManager, Path.Combine(_configDirectory, "auth.json"));
        return authManager;
    }

    private static void AssertJsonEquals(JsonElement actual, string expectedJson)
    {
        var actualNode = JsonNode.Parse(actual.GetRawText());
        var expectedNode = JsonNode.Parse(expectedJson);
        JsonNode.DeepEquals(actualNode, expectedNode).ShouldBeTrue();
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
        public Task<string> BuildSystemPromptAsync(
            AgentDescriptor descriptor,
            AgentExecutionContext? executionContext,
            CancellationToken ct = default)
            => Task.FromResult(descriptor.SystemPrompt ?? string.Empty);
    }

    private sealed class StaticAgentToolFactory : IAgentToolFactory
    {
        public IReadOnlyList<IAgentTool> CreateTools(string workingDirectory, IPathValidator? pathValidator = null, string[]? shellCommand = null)
            => [new ReadTool(workingDirectory)];
    }

    private sealed class CapturingAgentToolFactory : IAgentToolFactory
    {
        public IPathValidator? CapturedPathValidator { get; private set; }

        public IReadOnlyList<IAgentTool> CreateTools(string workingDirectory, IPathValidator? pathValidator = null, string[]? shellCommand = null)
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

        public Task SaveMemoryAsync(string agentName, string? filePath, string content, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task SaveMemoryAsync(
            string agentName,
            string? filePath,
            string content,
            string? memoryPathOverride,
            CancellationToken cancellationToken = default)
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

    // -------------------------------------------------------------------------
    // Issue #12: agents.defaults integration tests
    // -------------------------------------------------------------------------

    [Fact]
    public async Task LoadAsync_WithoutAgentsDefaults_LoadsAgentsUnchanged_BackwardCompatibility()
    {
        // Arrange — config without any agents.defaults key
        var config = new PlatformConfig
        {
            Agents = new Dictionary<string, AgentDefinitionConfig>
            {
                ["assistant"] = new()
                {
                    Provider = "copilot",
                    Model = "gpt-4.1",
                    ToolIds = ["tool-a"],
                    Memory = new MemoryAgentConfig { Enabled = true, Indexing = "manual" }
                }
            }
        };
        var source = new PlatformConfigAgentSource(
            new TestOptionsMonitor<PlatformConfig>(config),
            _configDirectory,
            new ListLogger<PlatformConfigAgentSource>());

        // Act
        var descriptor = (await source.LoadAsync()).ShouldHaveSingleItem();

        // Assert — original values preserved; no inheritance side effects
        descriptor.AgentId.Value.ShouldBe("assistant");
        descriptor.ToolIds.ShouldBe(["tool-a"]);
        descriptor.Memory.ShouldNotBeNull();
        descriptor.Memory!.Enabled.ShouldBeTrue();
        descriptor.Memory.Indexing.ShouldBe("manual");
    }

    [Fact]
    public async Task LoadAsync_AgentsDefaultsIsReservedKey_NotRegisteredAsAgentDescriptor()
    {
        // Arrange — simulate ExtractAgentDefaults having been called; defaults key should not appear
        var config = new PlatformConfig
        {
            AgentDefaults = new AgentDefaultsConfig
            {
                Memory = new MemoryAgentConfig { Enabled = true }
            },
            Agents = new Dictionary<string, AgentDefinitionConfig>
            {
                // Safety guard: even if "defaults" leaked into the dictionary, it must be skipped
                ["defaults"] = new()
                {
                    Provider = "copilot",
                    Model = "gpt-4.1",
                    Enabled = true
                },
                ["assistant"] = new()
                {
                    Provider = "copilot",
                    Model = "gpt-4.1",
                    Enabled = true
                }
            }
        };
        var source = new PlatformConfigAgentSource(
            new TestOptionsMonitor<PlatformConfig>(config),
            _configDirectory,
            new ListLogger<PlatformConfigAgentSource>());

        // Act
        var descriptors = await source.LoadAsync();

        // Assert — only "assistant" is registered; "defaults" is excluded
        descriptors.ShouldHaveSingleItem();
        descriptors[0].AgentId.Value.ShouldBe("assistant");
        descriptors.ShouldNotContain(d => d.AgentId.Value.Equals("defaults", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LoadAsync_WithAgentsDefaults_MemoryInheritedIntoAgentDescriptor()
    {
        // Arrange — agent omits memory; defaults provide it
        var config = new PlatformConfig
        {
            AgentDefaults = new AgentDefaultsConfig
            {
                Memory = new MemoryAgentConfig { Enabled = true, Indexing = "semantic" }
            },
            Agents = new Dictionary<string, AgentDefinitionConfig>
            {
                ["assistant"] = new()
                {
                    Provider = "copilot",
                    Model = "gpt-4.1",
                    Enabled = true
                }
            }
        };
        var source = new PlatformConfigAgentSource(
            new TestOptionsMonitor<PlatformConfig>(config),
            _configDirectory,
            new ListLogger<PlatformConfigAgentSource>());

        // Act
        var descriptor = (await source.LoadAsync()).ShouldHaveSingleItem();

        // Assert
        descriptor.Memory.ShouldNotBeNull();
        descriptor.Memory!.Enabled.ShouldBeTrue();
        descriptor.Memory.Indexing.ShouldBe("semantic");
    }

    [Fact]
    public async Task LoadAsync_WithMemoryEnabledAndPromptInjectionOmitted_DefaultsPromptInjectionToFull()
    {
        var config = JsonSerializer.Deserialize<PlatformConfig>(
            """
            {
              "Agents": {
                "assistant": {
                  "Provider": "copilot",
                  "Model": "gpt-4.1",
                  "Enabled": true,
                  "Memory": {
                    "Enabled": true,
                    "Indexing": "auto"
                  }
                }
              }
            }
            """)!;

        var source = new PlatformConfigAgentSource(
            new TestOptionsMonitor<PlatformConfig>(config),
            _configDirectory,
            new ListLogger<PlatformConfigAgentSource>());

        var descriptor = (await source.LoadAsync()).ShouldHaveSingleItem();

        descriptor.Memory.ShouldNotBeNull();
        GetPromptInjection(descriptor.Memory!).ShouldBe("full");
    }

    [Fact]
    public async Task LoadAsync_WithAgentMemoryPathOverride_MapsPathOnDescriptorMemoryConfig()
    {
        var config = JsonSerializer.Deserialize<PlatformConfig>(
            """
            {
              "Agents": {
                "assistant": {
                  "Provider": "copilot",
                  "Model": "gpt-4.1",
                  "Enabled": true,
                  "Memory": {
                    "Enabled": true,
                    "Indexing": "auto",
                    "Path": "memory/custom-notes.md"
                  }
                }
              }
            }
            """)!;

        var source = new PlatformConfigAgentSource(
            new TestOptionsMonitor<PlatformConfig>(config),
            _configDirectory,
            new ListLogger<PlatformConfigAgentSource>());

        var descriptor = (await source.LoadAsync()).ShouldHaveSingleItem();

        descriptor.Memory.ShouldNotBeNull();
        var pathProperty = descriptor.Memory!.GetType().GetProperty("Path");
        pathProperty.ShouldNotBeNull("Wave 1 requires per-agent memory path override support.");
        pathProperty!.GetValue(descriptor.Memory)?.ToString().ShouldBe("memory/custom-notes.md");
    }

    [Fact]
    public async Task LoadAsync_WithAgentsDefaults_ToolIdsInheritedWhenAgentOmitsThem()
    {
        // Arrange
        var config = new PlatformConfig
        {
            AgentDefaults = new AgentDefaultsConfig
            {
                ToolIds = ["read", "write"]
            },
            Agents = new Dictionary<string, AgentDefinitionConfig>
            {
                ["assistant"] = new()
                {
                    Provider = "copilot",
                    Model = "gpt-4.1",
                    Enabled = true
                    // No toolIds
                }
            }
        };
        var source = new PlatformConfigAgentSource(
            new TestOptionsMonitor<PlatformConfig>(config),
            _configDirectory,
            new ListLogger<PlatformConfigAgentSource>());

        // Act
        var descriptor = (await source.LoadAsync()).ShouldHaveSingleItem();

        // Assert — inherited from defaults since no AgentRawElements present (no raw JSON path)
        descriptor.ToolIds.ShouldBe(["read", "write"]);
    }

    [Fact]
    public async Task LoadAsync_WithAgentToolTimeoutSeconds_PreservesTimeoutForRuntimeWiring()
    {
        var config = JsonSerializer.Deserialize<PlatformConfig>(
            """
            {
              "Agents": {
                "assistant": {
                  "Provider": "copilot",
                  "Model": "gpt-4.1",
                  "Enabled": true,
                  "ToolTimeoutSeconds": 9
                }
              }
            }
            """)!;

        var source = new PlatformConfigAgentSource(
            new TestOptionsMonitor<PlatformConfig>(config),
            _configDirectory,
            new ListLogger<PlatformConfigAgentSource>());

        var descriptor = (await source.LoadAsync()).ShouldHaveSingleItem();

        ReadToolTimeoutSeconds(descriptor).ShouldBe(9);
    }

    private static string GetPromptInjection(MemoryAgentConfig config)
    {
        var property = typeof(MemoryAgentConfig).GetProperty("PromptInjection");
        property.ShouldNotBeNull("MemoryAgentConfig.PromptInjection should exist for memory prompt-injection behavior.");
        return property!.GetValue(config)?.ToString() ?? string.Empty;
    }

    [Fact]
    public async Task LoadAsync_WithDefaultsToolTimeoutSeconds_InheritsTimeoutWhenAgentOmitsOverride()
    {
        var config = JsonSerializer.Deserialize<PlatformConfig>(
            """
            {
              "Agents": {
                "defaults": {
                  "ToolTimeoutSeconds": 11
                },
                "assistant": {
                  "Provider": "copilot",
                  "Model": "gpt-4.1",
                  "Enabled": true
                }
              }
            }
            """)!;

        var source = new PlatformConfigAgentSource(
            new TestOptionsMonitor<PlatformConfig>(config),
            _configDirectory,
            new ListLogger<PlatformConfigAgentSource>());

        var descriptor = (await source.LoadAsync()).ShouldHaveSingleItem();

        ReadToolTimeoutSeconds(descriptor).ShouldBe(11);
    }

    private static int? ReadToolTimeoutSeconds(AgentDescriptor descriptor)
    {
        var timeoutProperty = descriptor.GetType().GetProperty("ToolTimeoutSeconds", BindingFlags.Public | BindingFlags.Instance);
        if (timeoutProperty?.GetValue(descriptor) is int seconds)
        {
            return seconds;
        }

        if (!descriptor.Metadata.TryGetValue("toolTimeoutSeconds", out var raw) || raw is null)
        {
            return null;
        }

        return raw switch
        {
            int value => value,
            long value => checked((int)value),
            JsonElement { ValueKind: JsonValueKind.Number } jsonNumber when jsonNumber.TryGetInt32(out var value) => value,
            JsonElement { ValueKind: JsonValueKind.String } jsonString when int.TryParse(jsonString.GetString(), out var value) => value,
            string value when int.TryParse(value, out var parsed) => parsed,
            _ => null
        };
    }

    // #803 — CacheRetention per-agent config

    [Fact]
    public async Task LoadAsync_WithCacheRetentionNone_DescriptorHasCacheRetentionNone()
    {
        var config = JsonSerializer.Deserialize<PlatformConfig>(
            """
            {
              "Agents": {
                "assistant": {
                  "Provider": "copilot",
                  "Model": "gpt-4.1",
                  "Enabled": true,
                  "CacheRetention": "none"
                }
              }
            }
            """)!;

        var source = new PlatformConfigAgentSource(
            new TestOptionsMonitor<PlatformConfig>(config),
            _configDirectory,
            new ListLogger<PlatformConfigAgentSource>());

        var descriptor = (await source.LoadAsync()).ShouldHaveSingleItem();

        descriptor.CacheRetentionMode.ShouldBe("none");
    }

    [Fact]
    public async Task LoadAsync_WithCacheRetentionLong_DescriptorHasCacheRetentionLong()
    {
        var config = JsonSerializer.Deserialize<PlatformConfig>(
            """
            {
              "Agents": {
                "assistant": {
                  "Provider": "copilot",
                  "Model": "gpt-4.1",
                  "Enabled": true,
                  "CacheRetention": "long"
                }
              }
            }
            """)!;

        var source = new PlatformConfigAgentSource(
            new TestOptionsMonitor<PlatformConfig>(config),
            _configDirectory,
            new ListLogger<PlatformConfigAgentSource>());

        var descriptor = (await source.LoadAsync()).ShouldHaveSingleItem();

        descriptor.CacheRetentionMode.ShouldBe("long");
    }

    [Fact]
    public async Task LoadAsync_WithoutCacheRetention_DescriptorCacheRetentionIsNull()
    {
        // When cacheRetention is omitted, descriptor should be null (caller uses provider default).
        var config = JsonSerializer.Deserialize<PlatformConfig>(
            """
            {
              "Agents": {
                "assistant": {
                  "Provider": "copilot",
                  "Model": "gpt-4.1",
                  "Enabled": true
                }
              }
            }
            """)!;

        var source = new PlatformConfigAgentSource(
            new TestOptionsMonitor<PlatformConfig>(config),
            _configDirectory,
            new ListLogger<PlatformConfigAgentSource>());

        var descriptor = (await source.LoadAsync()).ShouldHaveSingleItem();

        descriptor.CacheRetentionMode.ShouldBeNull();
    }

    // --- PBI4 (#1705): agent-level thinking + context mapping and capability gating ---

    private static ModelRegistry MakeThinkingRegistry()
    {
        var registry = new ModelRegistry();
        // github-copilot is the canonical provider for the "copilot" alias used in these configs.
        registry.Register("github-copilot", new LlmModel(
            Id: "reasoning-model",
            Name: "Reasoning Model",
            Api: "github-copilot-responses",
            Provider: "github-copilot",
            BaseUrl: "https://example.invalid",
            Reasoning: true,
            Input: ["text"],
            Cost: new ModelCost(0m, 0m, 0m, 0m),
            ContextWindow: 200_000,
            MaxTokens: 64_000,
            SupportsExtraHighThinking: true));
        registry.Register("github-copilot", new LlmModel(
            Id: "plain-model",
            Name: "Plain Model",
            Api: "github-copilot-completions",
            Provider: "github-copilot",
            BaseUrl: "https://example.invalid",
            Reasoning: false,
            Input: ["text"],
            Cost: new ModelCost(0m, 0m, 0m, 0m),
            ContextWindow: 128_000,
            MaxTokens: 16_000));
        return registry;
    }

    [Fact]
    public async Task LoadAsync_WithSupportedThinkingAndContext_MapsOntoDescriptor()
    {
        var config = JsonSerializer.Deserialize<PlatformConfig>(
            """
            {
              "Agents": {
                "assistant": {
                  "Provider": "copilot",
                  "Model": "reasoning-model",
                  "Enabled": true,
                  "Thinking": "high",
                  "ContextWindow": 200000
                }
              }
            }
            """)!;

        var source = new PlatformConfigAgentSource(
            new TestOptionsMonitor<PlatformConfig>(config),
            _configDirectory,
            new ListLogger<PlatformConfigAgentSource>(),
            locationResolver: null,
            modelRegistry: MakeThinkingRegistry());

        var descriptor = (await source.LoadAsync()).ShouldHaveSingleItem();

        descriptor.Thinking.ShouldBe("high");
        descriptor.ContextWindow.ShouldBe(200000);
    }

    [Fact]
    public async Task LoadAsync_WithThinkingOnNonReasoningModel_SkipsAgent()
    {
        var config = JsonSerializer.Deserialize<PlatformConfig>(
            """
            {
              "Agents": {
                "assistant": {
                  "Provider": "copilot",
                  "Model": "plain-model",
                  "Enabled": true,
                  "Thinking": "high"
                }
              }
            }
            """)!;

        var source = new PlatformConfigAgentSource(
            new TestOptionsMonitor<PlatformConfig>(config),
            _configDirectory,
            new ListLogger<PlatformConfigAgentSource>(),
            locationResolver: null,
            modelRegistry: MakeThinkingRegistry());

        // Invalid-for-model thinking causes the agent to be skipped at load time.
        (await source.LoadAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task LoadAsync_WithoutThinking_DescriptorThinkingIsNull()
    {
        var config = JsonSerializer.Deserialize<PlatformConfig>(
            """
            {
              "Agents": {
                "assistant": {
                  "Provider": "copilot",
                  "Model": "reasoning-model",
                  "Enabled": true
                }
              }
            }
            """)!;

        var source = new PlatformConfigAgentSource(
            new TestOptionsMonitor<PlatformConfig>(config),
            _configDirectory,
            new ListLogger<PlatformConfigAgentSource>(),
            locationResolver: null,
            modelRegistry: MakeThinkingRegistry());

        var descriptor = (await source.LoadAsync()).ShouldHaveSingleItem();

        descriptor.Thinking.ShouldBeNull();
        descriptor.ContextWindow.ShouldBeNull();
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

file sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;
    public T Get(string? name) => value;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
