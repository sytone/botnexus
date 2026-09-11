using System.IO.Abstractions;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.Core.Streaming;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Dispatching;
using BotNexus.Gateway.Isolation;
using BotNexus.Gateway.Sessions;
using BotNexus.Gateway.Tools;
using BotNexus.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Gateway.Tests.Dispatching;

/// <summary>
/// Composed regression for #4121. The original deadlock crossed the conversation queue, real
/// supervisor, real in-process handle, mid-loop hook, and real compaction coordinator; testing any
/// one of those seams in isolation cannot detect a handle synchronously disposing its own run.
/// </summary>
public sealed class MidLoopCompactionQueueRegressionTests
{
    private static readonly TimeSpan SignalBudget = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ConversationQueue_MidLoopCompaction_ReleasesWorkerAndPersistsQueuedMessageExactlyOnce()
    {
        var agentId = AgentId.From("agent-compaction-queue");
        var sessionId = SessionId.From("session-compaction-queue");
        var conversationId = ConversationId.From("conversation-compaction-queue");
        var sessionStore = new InMemorySessionStore();
        var session = await sessionStore.GetOrCreateAsync(sessionId, agentId);
        session.ConversationId = conversationId;
        session.AddEntries(
        [
            new SessionEntry { Role = MessageRole.User, Content = "old user context" },
            new SessionEntry { Role = MessageRole.Assistant, Content = "old assistant context" }
        ]);
        await sessionStore.SaveAsync(session);

        var compactionEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCompaction = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var shouldCompactCalls = 0;
        var compactor = new Mock<ISessionCompactor>();
        compactor
            .Setup(value => value.ShouldCompact(It.IsAny<Session>(), It.IsAny<CompactionOptions>()))
            .Returns(() => Interlocked.Increment(ref shouldCompactCalls) == 1);
        compactor
            .Setup(value => value.CompactAsync(
                It.IsAny<GatewaySession>(),
                It.IsAny<CompactionOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (GatewaySession liveSession, CompactionOptions _, CancellationToken cancellationToken) =>
            {
                compactionEntered.TrySetResult();
                await releaseCompaction.Task.WaitAsync(cancellationToken);
                var snapshot = liveSession.SnapshotHistoryForCompaction();
                var compacted = liveSession.GetHistorySnapshot()
                    .Select(entry => entry with { IsHistory = true })
                    .ToList();
                compacted.Add(new SessionEntry
                {
                    Role = MessageRole.System,
                    Content = "summary visible to the next provider turn",
                    IsCompactionSummary = true
                });
                return CompactionResult.ForSuccess(
                    summary: "summary visible to the next provider turn",
                    compactedHistory: compacted,
                    entriesSummarized: snapshot.Count,
                    entriesPreserved: 0,
                    tokensBefore: 100,
                    tokensAfter: 10,
                    snapshotDestructiveVersion: snapshot.DestructiveVersion,
                    snapshotHistoryCount: snapshot.Count);
            });

        var provider = new CapturingProvider();
        var models = new ModelRegistry();
        models.Register("test-provider", CapturingProvider.Model);
        var providers = new ApiProviderRegistry();
        providers.Register(provider);
        var llmClient = new LlmClient(providers, models);

        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(new AgentDescriptor
        {
            AgentId = agentId,
            DisplayName = "Compaction queue agent",
            ModelId = CapturingProvider.Model.Id,
            ApiProvider = CapturingProvider.Model.Provider,
            IsolationStrategy = "in-process",
            SystemPrompt = "base prompt"
        });

        var supervisorHolder = new SupervisorHolder();
        var services = new ServiceCollection();
        services.AddSingleton<ISessionStore>(sessionStore);
        services.AddSingleton<ISessionCompactor>(compactor.Object);
        services.AddSingleton<IOptionsMonitor<CompactionOptions>>(
            new FixedOptionsMonitor<CompactionOptions>(new CompactionOptions
            {
                PreservedTurns = 0,
                ContextWindowTokens = 32_000,
                TokenThresholdRatio = 0.5
            }));
        services.AddSingleton<ISessionCompactionCoordinator>(_ => new SessionCompactionCoordinator(
            compactor.Object,
            sessionStore,
            supervisorHolder.Value,
            Mock.Of<IChannelManager>(),
            new FixedOptionsMonitor<CompactionOptions>(new CompactionOptions { PreservedTurns = 0 }),
            NullLogger<SessionCompactionCoordinator>.Instance));
        await using var serviceProvider = services.BuildServiceProvider();

        var contextBuilder = new Mock<IContextBuilder>();
        contextBuilder
            .Setup(value => value.BuildSystemPromptAsync(
                It.IsAny<AgentDescriptor>(),
                It.IsAny<AgentExecutionContext?>(),
                It.IsAny<EffectiveExecutionSettings?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("base prompt");
        var toolFactory = new Mock<IAgentToolFactory>();
        toolFactory
            .Setup(value => value.CreateTools(It.IsAny<WorkingDir>(), It.IsAny<BotNexus.Gateway.Abstractions.Security.IPathValidator?>(), It.IsAny<string[]?>()))
            .Returns([]);
        var workspaceManager = new Mock<IAgentWorkspaceManager>();
        workspaceManager.Setup(value => value.GetWorkspacePath(It.IsAny<string>())).Returns(AppContext.BaseDirectory);

        var strategy = new InProcessIsolationStrategy(
            llmClient,
            new GatewayAuthManager(
                new FixedOptionsMonitor<PlatformConfig>(new PlatformConfig()),
                NullLogger<GatewayAuthManager>.Instance,
                new FileSystem()),
            contextBuilder.Object,
            toolFactory.Object,
            workspaceManager.Object,
            new DefaultToolRegistry([]),
            [],
            Mock.Of<IMemoryStoreFactory>(),
            Mock.Of<BotNexus.Gateway.Contracts.Memory.IAgentMemoryFactory>(),
            serviceProvider,
            NullLogger<InProcessIsolationStrategy>.Instance);
        var supervisor = new DefaultAgentSupervisor(
            registry,
            [strategy],
            sessionStore,
            NullLogger<DefaultAgentSupervisor>.Instance);
        supervisorHolder.Value = supervisor;

        var processor = new HandleBackedProcessor(supervisor, sessionStore, agentId, sessionId);
        var orchestrator = new DefaultInboundMessageOrchestrator(
            processor,
            NullLogger<DefaultInboundMessageOrchestrator>.Instance);

        var first = orchestrator.AcceptAsync(CreateMessage("first", conversationId, sessionId));
        await compactionEntered.Task.WaitAsync(SignalBudget);

        // This is the production recovery attempt: another message enters the same conversation
        // FIFO while the first run is inside mid-loop compaction.
        var queued = orchestrator.AcceptAsync(CreateMessage("queued during compaction", conversationId, sessionId));
        queued.IsCompleted.ShouldBeFalse();
        releaseCompaction.TrySetResult();

        var outcomes = await Task.WhenAll(first, queued).WaitAsync(SignalBudget);
        outcomes.ShouldAllBe(outcome => outcome.Status == InboundDispatchStatus.Accepted);
        processor.RunEndedCount.ShouldBe(2);

        var persisted = await sessionStore.GetAsync(sessionId);
        persisted.ShouldNotBeNull();
        persisted!.History.Count(entry => entry.IsCompactionSummary).ShouldBe(1);
        persisted.History.Count(entry => entry.Content == "queued during compaction").ShouldBe(1);
        provider.Contexts.ShouldNotBeEmpty();
        var compactedPrompt = provider.Contexts[0].SystemPrompt;
        compactedPrompt.ShouldNotBeNull();
        compactedPrompt.ShouldContain("summary visible to the next provider turn");

        // The historical unconditional coordinator eviction reaches the real supervisor here,
        // disposes this exact handle, and awaits the active run. The bounded WhenAll above then
        // throws instead of hanging the test project indefinitely.
        supervisor.GetHandle(agentId, sessionId).ShouldNotBeNull();

        await orchestrator.DisposeAsync().AsTask().WaitAsync(SignalBudget);
        await supervisor.StopAllAsync().WaitAsync(SignalBudget);
    }

    private static InboundMessage CreateMessage(
        string content,
        ConversationId conversationId,
        SessionId sessionId)
        => new()
        {
            ChannelType = ChannelKey.From("test"),
            ChannelAddress = ChannelAddress.From("address"),
            SenderId = "sender",
            Sender = CitizenId.Of(UserId.From("sender")),
            Content = content,
            RoutingHints = InboundMessageRoutingHints.LiftFromStrings(
                null,
                sessionId.Value,
                conversationId.Value)
        };

    private sealed class HandleBackedProcessor(
        IAgentSupervisor supervisor,
        ISessionStore sessions,
        AgentId agentId,
        SessionId sessionId) : IInboundMessageProcessor
    {
        private int _runEndedCount;

        public int RunEndedCount => Volatile.Read(ref _runEndedCount);

        public async Task<InboundProcessingOutcome> ProcessAsync(
            InboundMessage message,
            CancellationToken cancellationToken)
        {
            var session = await sessions.GetAsync(sessionId, cancellationToken)
                ?? throw new InvalidOperationException("The composed test session disappeared.");
            session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = message.Content });
            await sessions.SaveAsync(session, cancellationToken);

            var handle = await supervisor.GetOrCreateAsync(agentId, sessionId, cancellationToken);
            await foreach (var evt in handle.StreamAsync(message.Content, cancellationToken))
            {
                if (evt.Type == AgentStreamEventType.RunEnded)
                {
                    Interlocked.Increment(ref _runEndedCount);
                }
            }

            return new InboundProcessingOutcome(
                [new DispatchResult(
                    new InboundMessageContext(
                        agentId,
                        message,
                        new ChannelSource(message.ChannelType, message.ChannelAddress, message.SenderId, message.BindingId)),
                    new ChannelSource(message.ChannelType, message.ChannelAddress, message.SenderId, message.BindingId),
                    new ConversationSessionResolution(
                        session.ConversationId,
                        sessionId,
                        IsNewConversation: false,
                        IsNewSession: false,
                        OriginatingBindingId: null,
                        DisplayPrefix: null))],
                ShouldClosePerSessionQueue: false);
        }
    }

    private sealed class CapturingProvider : IApiProvider
    {
        public static readonly LlmModel Model = new(
            Id: "test-model",
            Name: "Test model",
            Api: "test-api",
            Provider: "test-provider",
            BaseUrl: "http://localhost",
            Reasoning: false,
            Input: ["text"],
            Cost: new ModelCost(0, 0, 0, 0),
            ContextWindow: 32_000,
            MaxTokens: 1_024);

        private readonly List<Context> _contexts = [];

        public string Api => Model.Api;

        public IReadOnlyList<Context> Contexts
        {
            get { lock (_contexts) { return _contexts.ToArray(); } }
        }

        public LlmStream Stream(LlmModel model, Context context, StreamOptions? options = null)
            => StreamSimple(model, context, null);

        public LlmStream StreamSimple(LlmModel model, Context context, SimpleStreamOptions? options = null)
        {
            lock (_contexts) { _contexts.Add(context); }
            var stream = new LlmStream();
            var message = new AssistantMessage(
                Content: [new TextContent("ok")],
                Api: model.Api,
                Provider: model.Provider,
                ModelId: model.Id,
                Usage: Usage.Empty(),
                StopReason: StopReason.Stop,
                ErrorMessage: null,
                ResponseId: null,
                Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            stream.Push(new StartEvent(message with { Content = [] }));
            stream.Push(new TextDeltaEvent(0, "ok", message));
            stream.Push(new DoneEvent(StopReason.Stop, message));
            return stream;
        }
    }

    private sealed class SupervisorHolder
    {
        public IAgentSupervisor Value { get; set; } = null!;
    }

    private sealed class FixedOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
