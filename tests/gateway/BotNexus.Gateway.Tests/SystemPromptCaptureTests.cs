using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Isolation;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Isolation;
using BotNexus.Gateway.Tools;
using BotNexus.Memory;
using BotNexus.Memory.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using System.IO.Abstractions;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Tests for LastRenderedSystemPrompt capture on GatewaySession (Issue #766).
/// </summary>
public sealed class SystemPromptCaptureTests
{
    // ── GatewaySession property smoke tests ───────────────────────────────

    [Fact]
    public void GatewaySession_LastRenderedSystemPrompt_DefaultsToNull()
    {
        var session = new GatewaySession { SessionId = SessionId.From("s1") };
        session.LastRenderedSystemPrompt.ShouldBeNull();
        session.LastRenderedSystemPromptAt.ShouldBeNull();
    }

    [Fact]
    public void GatewaySession_LastRenderedSystemPrompt_CanBeSet()
    {
        var session = new GatewaySession { SessionId = SessionId.From("s2") };
        var before = DateTimeOffset.UtcNow;

        session.LastRenderedSystemPrompt = "## My system prompt";
        session.LastRenderedSystemPromptAt = DateTimeOffset.UtcNow;

        session.LastRenderedSystemPrompt.ShouldBe("## My system prompt");
        session.LastRenderedSystemPromptAt.ShouldNotBeNull();
        session.LastRenderedSystemPromptAt!.Value.ShouldBeGreaterThanOrEqualTo(before);
    }

    // ── InProcessAgentHandle.RenderedSystemPrompt tests ───────────────────

    [Fact]
    public async Task InProcessIsolationStrategy_CreateAsync_SetsRenderedSystemPromptOnHandle()
    {
        // Arrange
        const string expectedPrompt = "## Rendered System Prompt for 766";
        var strategy = CreateStrategy(expectedPrompt);

        // Act
        var handle = await strategy.CreateAsync(
            CreateDescriptor(),
            new AgentExecutionContext { SessionId = SessionId.From("session-766") });

        // Assert – the returned handle is InProcessAgentHandle with RenderedSystemPrompt set
        var inProcessHandle = handle as InProcessAgentHandle;
        inProcessHandle.ShouldNotBeNull();
        inProcessHandle!.RenderedSystemPrompt.ShouldNotBeNullOrWhiteSpace();
        inProcessHandle.RenderedSystemPrompt!.ShouldContain("Rendered System Prompt for 766");
    }

    // ── Supervisor stamping tests ─────────────────────────────────────────

    [Fact]
    public async Task GetOrCreateAsync_WhenHandleIsNotInProcessAgentHandle_DoesNotStampSession()
    {
        // Arrange – strategy returns a plain Mock<IAgentHandle> (not InProcessAgentHandle)
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(CreateDescriptor());

        var plainHandle = new Mock<IAgentHandle>();
        plainHandle.SetupGet(h => h.AgentId).Returns(AgentId.From("agent-a"));
        plainHandle.SetupGet(h => h.SessionId).Returns(SessionId.From("session-plain"));
        plainHandle.Setup(h => h.IsRunning).Returns(false);

        var strategy = new Mock<IIsolationStrategy>();
        strategy.SetupGet(s => s.Name).Returns("in-process");
        strategy.Setup(s => s.CreateAsync(It.IsAny<AgentDescriptor>(), It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(plainHandle.Object);

        var gatewaySession = new GatewaySession { SessionId = SessionId.From("session-plain") };
        var sessionStore = new Mock<ISessionStore>();
        sessionStore.Setup(s => s.GetAsync(SessionId.From("session-plain"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(gatewaySession);

        var supervisor = new DefaultAgentSupervisor(
            registry, [strategy.Object], sessionStore.Object,
            NullLogger<DefaultAgentSupervisor>.Instance);

        // Act
        await supervisor.GetOrCreateAsync(AgentId.From("agent-a"), SessionId.From("session-plain"));

        // Assert – session should NOT be stamped because the handle is not an InProcessAgentHandle
        gatewaySession.LastRenderedSystemPrompt.ShouldBeNull();
        gatewaySession.LastRenderedSystemPromptAt.ShouldBeNull();
    }

    // ─── Helpers ─────────────────────────────────────────────────────────

    private static InProcessIsolationStrategy CreateStrategy(string systemPromptContent = "test prompt")
    {
        var modelRegistry = new ModelRegistry();
        modelRegistry.Register("test-provider", new LlmModel(
            Id: "test-model",
            Name: "test-model",
            Api: "test-api",
            Provider: "test-provider",
            BaseUrl: "http://localhost",
            Reasoning: false,
            Input: ["text"],
            Cost: new ModelCost(0, 0, 0, 0),
            ContextWindow: 8192,
            MaxTokens: 1024));

        var llmClient = new LlmClient(new ApiProviderRegistry(), modelRegistry);
        return new InProcessIsolationStrategy(
            llmClient,
            new GatewayAuthManager(
                new OptionsMonitorStub<PlatformConfig>(new PlatformConfig()),
                NullLogger<GatewayAuthManager>.Instance,
                new FileSystem()),
            new FixedPromptContextBuilder(systemPromptContent),
            new NoOpAgentToolFactory(),
            new NoOpWorkspaceManager(),
            new DefaultToolRegistry(Array.Empty<IAgentTool>()),
            Array.Empty<IAgentToolContributor>(),
            new NoOpMemoryStoreFactory(),
            new ServiceCollection().BuildServiceProvider(),
            NullLogger<InProcessIsolationStrategy>.Instance);
    }

    private static AgentDescriptor CreateDescriptor()
        => new()
        {
            AgentId = AgentId.From("agent-a"),
            DisplayName = "Agent A",
            ModelId = "test-model",
            ApiProvider = "test-provider",
            IsolationStrategy = "in-process",
            SystemPrompt = "base prompt"
        };

    // ─── Fakes ───────────────────────────────────────────────────────────

    private sealed class FixedPromptContextBuilder(string prompt) : IContextBuilder
    {
        public Task<string> BuildSystemPromptAsync(
            AgentDescriptor descriptor,
            AgentExecutionContext? executionContext,
            CancellationToken cancellationToken = default)
            => Task.FromResult(prompt);
    }

    private sealed class NoOpAgentToolFactory : IAgentToolFactory
    {
        public IReadOnlyList<IAgentTool> CreateTools(string workingDirectory, IPathValidator? pathValidator = null, string[]? shellCommand = null)
            => Array.Empty<IAgentTool>();
    }

    private sealed class NoOpWorkspaceManager : IAgentWorkspaceManager
    {
        public string GetWorkspacePath(string agentName) => AppContext.BaseDirectory;

        public Task<AgentWorkspace> LoadWorkspaceAsync(string agentName, CancellationToken cancellationToken = default)
            => Task.FromResult(new AgentWorkspace(agentName, null, null, null, null));

        public Task SaveMemoryAsync(string agentName, string content, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task SaveMemoryAsync(string agentName, string? filePath, string content, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task SaveMemoryAsync(string agentName, string? filePath, string content, string? memoryPathOverride, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class NoOpMemoryStoreFactory : IMemoryStoreFactory
    {
        public IMemoryStore Create(string agentId) => new NoOpMemoryStore();
    }

    private sealed class NoOpMemoryStore : IMemoryStore
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
}

file sealed class OptionsMonitorStub<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;
    public T Get(string? name) => value;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
