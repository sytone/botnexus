using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using AgentUserMessage = BotNexus.Agent.Core.Types.UserMessage;
using BotNexus.Agent.Core.Types;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Routing;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Dispatching;
using BotNexus.Gateway.Sessions;
using BotNexus.Gateway.Services;
using Moq;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// #2126: gateway integration coverage for provisional conversation titling. Proves the GatewayHost
/// wiring fires provisional titling from the first user message BEFORE the assistant turn completes,
/// keeps the existing guards (custom title, non-human, cron), notifies portal clients, and applies
/// the refine-once policy end-to-end.
/// </summary>
public sealed partial class GatewayHostTests
{
    private static readonly AgentId ProvAgentId = AgentId.From("agent-a");

    // A human first message on a still-default-titled human-agent conversation must receive a
    // non-default (provisional) title shortly after the user message is persisted - and BEFORE the
    // assistant turn completes. The assistant is gated on a TCS so the poll can observe the title
    // while the turn is still in flight.
    [Fact]
    public async Task DispatchAsync_FirstUserMessage_SetsProvisionalTitle_BeforeAssistantCompletes()
    {
        var (router, supervisor, sessions, conversationStore, dispatcher, convId, sessionId) =
            BuildProvisionalHarness(out var assistantGate);

        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        await using var host = CreateHost(
            supervisor.Object, router.Object, sessions,
            new RecordingActivityBroadcaster(), CreateChannelManager(channel.Object),
            conversationDispatcher: dispatcher.Object,
            conversationStore: conversationStore,
            llmClient: CreateFakeTitleLlmClient("Kyoto Trip"));

        // Do NOT await: the assistant is gated so the turn stays in flight while we poll.
        var dispatch = host.DispatchAsync(
            CreateMessage("Help me plan a trip to Kyoto", channelType: "web", conversationId: "addr-1"));

        string? provisionalTitle = null;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var conv = await conversationStore.GetAsync(convId, CancellationToken.None);
            provisionalTitle = conv?.Title;
            if (conv is not null && !ConversationAutoTitleService.IsDefaultTitle(conv.Title))
            {
                ConversationAutoTitleService.IsProvisionalTitle(conv).ShouldBeTrue();
                break;
            }
            await Task.Delay(50);
        }

        provisionalTitle.ShouldBe("Kyoto Trip");

        // Release the assistant and let the turn finish cleanly.
        assistantGate.TrySetResult(new AgentResponse { Content = "Here is your Kyoto plan." });
        await dispatch;
    }

    // A cancelled/interrupted first turn must still leave a non-default (provisional) title - the
    // conversation is not permanently stuck on "New conversation".
    [Fact]
    public async Task DispatchAsync_FirstUserMessageThenCancel_LeavesProvisionalTitle()
    {
        var (router, supervisor, sessions, conversationStore, dispatcher, convId, _) =
            BuildProvisionalHarness(out var assistantGate);

        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        await using var host = CreateHost(
            supervisor.Object, router.Object, sessions,
            new RecordingActivityBroadcaster(), CreateChannelManager(channel.Object),
            conversationDispatcher: dispatcher.Object,
            conversationStore: conversationStore,
            llmClient: CreateFakeTitleLlmClient("Interrupted Topic"));

        using var cts = new CancellationTokenSource();
        var dispatch = host.DispatchAsync(
            CreateMessage("Tell me about interrupted topic", channelType: "web", conversationId: "addr-1"),
            cts.Token);

        string? title = null;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var conv = await conversationStore.GetAsync(convId, CancellationToken.None);
            title = conv?.Title;
            if (conv is not null && !ConversationAutoTitleService.IsDefaultTitle(conv.Title))
                break;
            await Task.Delay(50);
        }

        title.ShouldBe("Interrupted Topic");

        // Cancel before the assistant produced anything.
        cts.Cancel();
        assistantGate.TrySetCanceled(cts.Token);
        try { await dispatch; } catch (OperationCanceledException) { /* expected */ }

        // Title survives the interruption.
        var final = await conversationStore.GetAsync(convId, CancellationToken.None);
        ConversationAutoTitleService.IsDefaultTitle(final?.Title).ShouldBeFalse();
    }

    // An existing custom (human-assigned) title must never be overwritten by provisional titling.
    [Fact]
    public async Task DispatchAsync_FirstUserMessage_CustomTitle_NotOverwritten()
    {
        var (router, supervisor, sessions, conversationStore, dispatcher, convId, _) =
            BuildProvisionalHarness(out var assistantGate, initialTitle: "My Custom Title");

        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        await using var host = CreateHost(
            supervisor.Object, router.Object, sessions,
            new RecordingActivityBroadcaster(), CreateChannelManager(channel.Object),
            conversationDispatcher: dispatcher.Object,
            conversationStore: conversationStore,
            llmClient: CreateFakeTitleLlmClient("Should Not Appear"));

        var dispatch = host.DispatchAsync(
            CreateMessage("hi there", channelType: "web", conversationId: "addr-1"));

        assistantGate.TrySetResult(new AgentResponse { Content = "hello" });
        await dispatch;

        // Give any background best-effort title task a moment; the title must stay custom.
        await Task.Delay(200);
        var conv = await conversationStore.GetAsync(convId, CancellationToken.None);
        conv!.Title.ShouldBe("My Custom Title");
        ConversationAutoTitleService.IsProvisionalTitle(conv).ShouldBeFalse();
    }

    // Provisional titling notifies connected portal clients when the title is persisted.
    [Fact]
    public async Task DispatchAsync_FirstUserMessage_NotifiesClientsOnProvisionalTitle()
    {
        var notifier = new Mock<IConversationChangeNotifier>();
        notifier.Setup(n => n.NotifyConversationChangedAsync(
                "updated", ProvAgentId.Value, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var (router, supervisor, sessions, conversationStore, dispatcher, convId, _) =
            BuildProvisionalHarness(out var assistantGate);

        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        await using var host = CreateHost(
            supervisor.Object, router.Object, sessions,
            new RecordingActivityBroadcaster(), CreateChannelManager(channel.Object),
            conversationDispatcher: dispatcher.Object,
            conversationStore: conversationStore,
            llmClient: CreateFakeTitleLlmClient("Notified Topic"),
            conversationChangeNotifier: notifier.Object);

        var dispatch = host.DispatchAsync(
            CreateMessage("notify me about this", channelType: "web", conversationId: "addr-1"));

        for (var attempt = 0; attempt < 50; attempt++)
        {
            var conv = await conversationStore.GetAsync(convId, CancellationToken.None);
            if (conv is not null && !ConversationAutoTitleService.IsDefaultTitle(conv.Title))
                break;
            await Task.Delay(50);
        }

        assistantGate.TrySetResult(new AgentResponse { Content = "ok" });
        await dispatch;

        notifier.Verify(n => n.NotifyConversationChangedAsync(
            "updated", ProvAgentId.Value, convId.Value, It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    // A cron-owned turn (non-interactive) must NOT receive a provisional title.
    [Fact]
    public async Task DispatchAsync_CronTurn_NoProvisionalTitle()
    {
        var agentId = AgentId.From("agent-a");
        var sessionId = SessionId.From("cron:job-1:agent-a");
        var convId = ConversationId.From("c_cronprov1");

        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);

        var handle = CreatePromptHandle("agent-a", sessionId.Value, "cron ran");
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(agentId, sessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var sessions = new InMemorySessionStore();
        await sessions.GetOrCreateAsync(sessionId, agentId);

        var conversationStore = new InMemoryConversationStore();
        await conversationStore.SaveAsync(
            new Conversation { ConversationId = convId, AgentId = agentId, Title = ConversationAutoTitleService.DefaultTitle },
            CancellationToken.None);

        var dispatcher = new Mock<IConversationDispatcher>();
        dispatcher
            .Setup(d => d.DispatchAsync(It.IsAny<InboundMessageContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((InboundMessageContext context, CancellationToken _) => new DispatchResult(
                context, context.Source, new ConversationSessionResolution(convId, sessionId, false, false)));

        var channel = CreateChannelAdapter("cron", supportsStreaming: false);
        await using var host = CreateHost(
            supervisor.Object, router.Object, sessions,
            new RecordingActivityBroadcaster(), CreateChannelManager(channel.Object),
            conversationDispatcher: dispatcher.Object,
            conversationStore: conversationStore,
            llmClient: CreateFakeTitleLlmClient("Should Not Appear"));

        await host.DispatchAsync(CreateMessage("run", channelType: "cron", conversationId: "job-1"));

        // A cron turn's session is non-interactive; provisional titling must not fire. The
        // post-response auto-title path may still title after the exchange, so we only assert the
        // provisional flag is never set (a cron title, if any, is a final refine, not provisional).
        await Task.Delay(200);
        var conv = await conversationStore.GetAsync(convId, CancellationToken.None);
        ConversationAutoTitleService.IsProvisionalTitle(conv!).ShouldBeFalse();
    }

    /// <summary>
    /// Builds a non-streaming dispatch harness whose agent handle blocks on <paramref name="assistantGate"/>
    /// so the assistant turn can be held in flight while a test polls for the provisional title. The
    /// conversation store starts with a single default-titled human-agent conversation.
    /// </summary>
    private static (Mock<IMessageRouter> Router, Mock<IAgentSupervisor> Supervisor, InMemorySessionStore Sessions,
        InMemoryConversationStore ConversationStore, Mock<IConversationDispatcher> Dispatcher,
        ConversationId ConvId, SessionId SessionId) BuildProvisionalHarness(
            out TaskCompletionSource<AgentResponse> assistantGate,
            string initialTitle = ConversationAutoTitleService.DefaultTitle)
    {
        var agentId = AgentId.From("agent-a");
        var sessionId = SessionId.From("web:addr-1:agent-a");
        var convId = ConversationId.From("c_prov1");

        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);

        var gate = new TaskCompletionSource<AgentResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        assistantGate = gate;

        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(agentId);
        handle.SetupGet(h => h.SessionId).Returns(sessionId);
        handle.Setup(h => h.IsRunning).Returns(false);
        handle.Setup(h => h.PromptAsync(It.IsAny<AgentUserMessage>(), It.IsAny<CancellationToken>()))
            .Returns<AgentUserMessage, CancellationToken>((_, ct) => gate.Task.WaitAsync(ct));

        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(agentId, sessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var sessions = new InMemorySessionStore();
        sessions.GetOrCreateAsync(sessionId, agentId).GetAwaiter().GetResult();

        var conversationStore = new InMemoryConversationStore();
        conversationStore.SaveAsync(
            new Conversation { ConversationId = convId, AgentId = agentId, Title = initialTitle },
            CancellationToken.None).GetAwaiter().GetResult();

        var dispatcher = new Mock<IConversationDispatcher>();
        dispatcher
            .Setup(d => d.DispatchAsync(It.IsAny<InboundMessageContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((InboundMessageContext context, CancellationToken _) => new DispatchResult(
                context, context.Source, new ConversationSessionResolution(convId, sessionId, false, false)));

        return (router, supervisor, sessions, conversationStore, dispatcher, convId, sessionId);
    }
}
