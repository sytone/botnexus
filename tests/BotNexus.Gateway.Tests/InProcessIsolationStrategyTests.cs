using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Core;
using BotNexus.Gateway.Abstractions.Isolation;
using BotNexus.Gateway.Abstractions.Agents;
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
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO.Abstractions;
using System.Reflection;
using AgentCoreUserMessage = BotNexus.Agent.Core.Types.UserMessage;

namespace BotNexus.Gateway.Tests;

public sealed class InProcessIsolationStrategyTests
{
    [Fact]
    public async Task CreateAsync_WithValidDescriptor_ReturnsAgentHandle()
    {
        var strategy = CreateStrategyWithRegisteredModel();

        var handle = await strategy.CreateAsync(
            CreateDescriptor(),
            new AgentExecutionContext { SessionId = BotNexus.Domain.Primitives.SessionId.From("session-1") });

        handle.Should().NotBeNull();
    }

    [Fact]
    public async Task CreateAsync_WhenModelNotRegistered_ThrowsInvalidOperationException()
    {
        var llmClient = new LlmClient(new ApiProviderRegistry(), new ModelRegistry());
        var strategy = new InProcessIsolationStrategy(
            llmClient,
            new GatewayAuthManager(new PlatformConfig(), NullLogger<GatewayAuthManager>.Instance, new FileSystem()),
            new PassthroughContextBuilder(),
            new StaticAgentToolFactory(),
            new TestWorkspaceManager(),
            new DefaultToolRegistry(Array.Empty<IAgentTool>()),
            new StubMemoryStoreFactory(),
            new ServiceCollection().BuildServiceProvider(),
            NullLogger<InProcessIsolationStrategy>.Instance);

        var act = () => strategy.CreateAsync(
            CreateDescriptor(modelId: "missing-model"),
            new AgentExecutionContext { SessionId = BotNexus.Domain.Primitives.SessionId.From("session-1") });

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task CreateAsync_WithValidDescriptor_SetsAgentAndSessionIds()
    {
        var strategy = CreateStrategyWithRegisteredModel();

        var handle = await strategy.CreateAsync(
            CreateDescriptor(),
            new AgentExecutionContext { SessionId = BotNexus.Domain.Primitives.SessionId.From("session-123") });

        handle.AgentId.Should().Be("agent-a");
        handle.SessionId.Should().Be("session-123");
    }

    [Fact]
    public void Name_WhenRead_ReturnsInProcess()
    {
        var strategy = CreateStrategyWithRegisteredModel();

        strategy.Name.Should().Be("in-process");
    }

    [Fact]
    public async Task CreateAsync_WithHistoryInContext_SeedsOnlyUserAndAssistantMessages()
    {
        var strategy = CreateStrategyWithRegisteredModel();
        var context = new AgentExecutionContext
        {
            SessionId = BotNexus.Domain.Primitives.SessionId.From("session-history"),
            History =
            [
                new SessionEntry { Role = BotNexus.Domain.Primitives.MessageRole.User, Content = "hi" },
                new SessionEntry { Role = BotNexus.Domain.Primitives.MessageRole.Assistant, Content = "hello" },
                new SessionEntry { Role = BotNexus.Domain.Primitives.MessageRole.System, Content = "rules" },
                new SessionEntry
                {
                    Role = BotNexus.Domain.Primitives.MessageRole.Tool,
                    Content = "tool output",
                    ToolName = "read",
                    ToolCallId = "call-1"
                }
            ]
        };

        var handle = await strategy.CreateAsync(CreateDescriptor(), context);
        var messages = GetMessages(handle);

        // Tool and system entries are filtered out to prevent orphaned tool results
        // from causing LLM provider rejection.
        messages.Should().HaveCount(2);
        messages[0].Should().BeEquivalentTo(new AgentCoreUserMessage("hi"));
        messages[1].Should().BeEquivalentTo(new AssistantAgentMessage("hello"));
    }

    [Fact]
    public async Task CreateAsync_WithMixedHistoryTypes_FiltersToolAndSystemEntries()
    {
        var strategy = CreateStrategyWithRegisteredModel();
        var context = new AgentExecutionContext
        {
            SessionId = BotNexus.Domain.Primitives.SessionId.From("session-mixed"),
            History =
            [
                new SessionEntry { Role = BotNexus.Domain.Primitives.MessageRole.User, Content = "search for cats" },
                new SessionEntry { Role = BotNexus.Domain.Primitives.MessageRole.Assistant, Content = "I'll search for that." },
                new SessionEntry
                {
                    Role = BotNexus.Domain.Primitives.MessageRole.Tool,
                    Content = "Tool 'search' started.",
                    ToolName = "search",
                    ToolCallId = "call-1"
                },
                new SessionEntry
                {
                    Role = BotNexus.Domain.Primitives.MessageRole.Tool,
                    Content = "Found 3 results.",
                    ToolName = "search",
                    ToolCallId = "call-1"
                },
                new SessionEntry { Role = BotNexus.Domain.Primitives.MessageRole.Assistant, Content = "Here are the results." },
                new SessionEntry { Role = BotNexus.Domain.Primitives.MessageRole.System, Content = "Agent stream error: timeout" },
                new SessionEntry { Role = BotNexus.Domain.Primitives.MessageRole.User, Content = "thanks" },
                new SessionEntry { Role = BotNexus.Domain.Primitives.MessageRole.Assistant, Content = "You're welcome!" },
            ]
        };

        var handle = await strategy.CreateAsync(CreateDescriptor(), context);
        var messages = GetMessages(handle);

        messages.Should().HaveCount(5);
        messages[0].Should().BeEquivalentTo(new AgentCoreUserMessage("search for cats"));
        messages[1].Should().BeEquivalentTo(new AssistantAgentMessage("I'll search for that."));
        messages[2].Should().BeEquivalentTo(new AssistantAgentMessage("Here are the results."));
        messages[3].Should().BeEquivalentTo(new AgentCoreUserMessage("thanks"));
        messages[4].Should().BeEquivalentTo(new AssistantAgentMessage("You're welcome!"));
    }

    private static InProcessIsolationStrategy CreateStrategyWithRegisteredModel()
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
            new GatewayAuthManager(new PlatformConfig(), NullLogger<GatewayAuthManager>.Instance, new FileSystem()),
            new PassthroughContextBuilder(),
            new StaticAgentToolFactory(),
            new TestWorkspaceManager(),
            new DefaultToolRegistry(Array.Empty<IAgentTool>()),
            new StubMemoryStoreFactory(),
            new ServiceCollection().BuildServiceProvider(),
            NullLogger<InProcessIsolationStrategy>.Instance);
    }

    private static AgentDescriptor CreateDescriptor(string modelId = "test-model")
        => new()
        {
            AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a"),
            DisplayName = "Agent A",
            ModelId = modelId,
            ApiProvider = "test-provider",
            SystemPrompt = "You are a test agent."
        };

    private static IReadOnlyList<AgentMessage> GetMessages(IAgentHandle handle)
    {
        var agentField = handle.GetType().GetField("_agent", BindingFlags.Instance | BindingFlags.NonPublic);
        agentField.Should().NotBeNull();
        var agent = agentField!.GetValue(handle) as BotNexus.Agent.Core.Agent;
        agent.Should().NotBeNull();
        return agent!.State.Messages;
    }

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

    private sealed class TestWorkspaceManager : IAgentWorkspaceManager
    {
        public Task<AgentWorkspace> LoadWorkspaceAsync(string agentName, CancellationToken cancellationToken = default)
            => Task.FromResult(new AgentWorkspace(agentName, string.Empty, string.Empty, string.Empty, string.Empty));

        public Task SaveMemoryAsync(string agentName, string content, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public string GetWorkspacePath(string agentName)
            => AppContext.BaseDirectory;
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
}
