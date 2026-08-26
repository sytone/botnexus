using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Sessions;
using BotNexus.Gateway.Streaming;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests.Sessions;

/// <summary>
/// #3535: a run that ends on an empty assistant completion because the provider context window is
/// exhausted must produce an explicit, user-visible message instead of a silent blank turn - while
/// the two legitimately-silent shapes that reach the same branch stay silent.
/// </summary>
/// <remarks>
/// <para>
/// The positive assertions are made on the EMITTED PAYLOAD - the stream event the helper hands to
/// <c>OnEventAsync</c>, which is the exact callback <c>GatewayHost</c> routes to the channel adapter
/// and the SignalR observer bindings - plus the persisted transcript row. Neither is a log
/// assertion, and that distinction is the whole point: the pre-fix behaviour ALREADY logged a
/// warning here, so a test that greps a log would pass against the defect.
/// </para>
/// <para>
/// The negative tests are the anti-vacuity guard for AC3. Each drives a stream through the real
/// helper with a notifier that WOULD fire (the session carries an exhausting provider count and a
/// resolvable window), so they fail if the notice is emitted unconditionally rather than only on the
/// contentless-completion branch. Verified by mutation: removing the discriminator and hoisting the
/// call out of the branch reddened exactly these three tests on the remote gate.
/// </para>
/// </remarks>
public sealed class ContextExhaustionNoticeTests
{
    private const string ProviderPromptTokensKey = "lastProviderPromptTokens";

    /// <summary>Captures every stream event the helper emits through the delivery callback.</summary>
    private sealed class EmittedPayloads
    {
        public List<AgentStreamEvent> Events { get; } = [];

        public ValueTask CaptureAsync(AgentStreamEvent evt, CancellationToken ct)
        {
            Events.Add(evt);
            return ValueTask.CompletedTask;
        }

        /// <summary>Content the user would actually see, in emission order.</summary>
        public List<string> DeliveredContent => Events
            .Where(e => e.Type == AgentStreamEventType.ContentDelta && e.ContentDelta is not null)
            .Select(e => e.ContentDelta!)
            .ToList();
    }

    // -------------------------------------------------------------------------------------------
    // AC1 + AC2: the exhausted case emits a user-visible payload naming cause and remedy.
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task EmptyCompletion_WhenPromptFillsResolvedWindow_EmitsUserVisiblePayload()
    {
        var emitted = new EmittedPayloads();
        var session = CreateSession("session-3535-exhausted", promptTokens: 999_306);
        var notifier = CreateNotifier(conversationOverride: null, agentWindow: 200_000);
        var store = new Mock<ISessionStore>();

        await StreamingSessionHelper.ProcessAndSaveAsync(
            ToAsyncEnumerable(
            [
                new AgentStreamEvent { Type = AgentStreamEventType.RunStarted },
                new AgentStreamEvent { Type = AgentStreamEventType.MessageStart },
                new AgentStreamEvent { Type = AgentStreamEventType.MessageEnd },
                new AgentStreamEvent { Type = AgentStreamEventType.TurnEnd },
                new AgentStreamEvent { Type = AgentStreamEventType.RunEnded }
            ]),
            session,
            store.Object,
            new StreamingSessionOptions(
                OnEventAsync: emitted.CaptureAsync,
                ContextExhaustionNotifier: notifier));

        // AC1: asserted on the emitted channel-bound payload, NOT on a log line. The run itself
        // produced no content, so the ONLY delivered content is the notice.
        var delivered = emitted.DeliveredContent.ShouldHaveSingleItem();
        delivered.ShouldContain("context");

        // AC2: the message names concrete remedies.
        delivered.ShouldContain("/compact");
        delivered.ShouldContain("new conversation");

        // The durable half: a Notification transcript row, which SessionContextProjector excludes
        // from the live LLM view so the notice cannot itself consume the exhausted window.
        var notice = session.GetHistorySnapshot().ShouldHaveSingleItem();
        notice.Role.ShouldBe(MessageRole.Notification);
        notice.Content.ShouldBe(delivered);
        SessionContextProjector.IsVisibleInLiveContext(notice).ShouldBeFalse(
            "the exhaustion notice must not re-enter the context window it is reporting on.");
    }

    // -------------------------------------------------------------------------------------------
    // AC3: the two legitimately-silent shapes stay silent. These fail if the notice is emitted
    // unconditionally - both sessions carry an exhausting provider count and a resolvable window.
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task ThinkingOnlyCompletion_EmitsNoNotice_EvenWhenWindowWouldBeExhausted()
    {
        var emitted = new EmittedPayloads();
        var session = CreateSession("session-3535-thinking", promptTokens: 999_306);
        var notifier = CreateNotifier(conversationOverride: null, agentWindow: 200_000);
        var store = new Mock<ISessionStore>();

        await StreamingSessionHelper.ProcessAndSaveAsync(
            ToAsyncEnumerable(
            [
                new AgentStreamEvent { Type = AgentStreamEventType.ThinkingDelta, ThinkingContent = "Only reasoning." },
                new AgentStreamEvent { Type = AgentStreamEventType.MessageEnd }
            ]),
            session,
            store.Object,
            new StreamingSessionOptions(
                OnEventAsync: emitted.CaptureAsync,
                ContextExhaustionNotifier: notifier));

        // #1198: a thinking-only turn is normal model behaviour, not a failure to report.
        emitted.DeliveredContent.ShouldBeEmpty();
        session.GetHistorySnapshot().ShouldNotContain(e => e.Role.Equals(MessageRole.Notification));
        session.GetHistorySnapshot().ShouldHaveSingleItem().Role.ShouldBe(MessageRole.Assistant);
    }

    [Fact]
    public async Task NormalContentTurn_EmitsNoNotice_EvenWhenWindowWouldBeExhausted()
    {
        var emitted = new EmittedPayloads();
        var session = CreateSession("session-3535-normal", promptTokens: 999_306);
        var notifier = CreateNotifier(conversationOverride: null, agentWindow: 200_000);
        var store = new Mock<ISessionStore>();

        await StreamingSessionHelper.ProcessAndSaveAsync(
            ToAsyncEnumerable(
            [
                new AgentStreamEvent { Type = AgentStreamEventType.RunStarted },
                new AgentStreamEvent { Type = AgentStreamEventType.MessageStart },
                new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "Here is the answer." },
                new AgentStreamEvent { Type = AgentStreamEventType.MessageEnd },
                new AgentStreamEvent { Type = AgentStreamEventType.TurnEnd },
                new AgentStreamEvent { Type = AgentStreamEventType.RunEnded }
            ]),
            session,
            store.Object,
            new StreamingSessionOptions(
                OnEventAsync: emitted.CaptureAsync,
                ContextExhaustionNotifier: notifier));

        // #3129: the per-turn buffers are already flushed by TurnEnd, so this run LOOKS contentless
        // at the final write. It is not, and it must not be told the window is exhausted. The only
        // delivered content is the model's own answer, passed straight through.
        emitted.DeliveredContent.ShouldHaveSingleItem().ShouldBe("Here is the answer.");
        session.GetHistorySnapshot().ShouldNotContain(e => e.Role.Equals(MessageRole.Notification));
        session.GetHistorySnapshot().ShouldHaveSingleItem().Content.ShouldBe("Here is the answer.");
    }

    // -------------------------------------------------------------------------------------------
    // AC4: the comparison uses the SCOPE-RESOLVED window, not the raw global ContextWindowTokens.
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task ConversationLevelOverride_NarrowsTheWindow_AndTripsExhaustionTheGlobalWouldMiss()
    {
        // 40,000 prompt tokens is nowhere near the agent's 200,000-token window, nor the 128,000
        // global default, so without the scoped resolver this run stays silent. The conversation
        // pins the window to 40,000 - which it has exhausted.
        var emitted = new EmittedPayloads();
        var session = CreateSession("session-3535-scoped", promptTokens: 40_000);
        var notifier = CreateNotifier(conversationOverride: 40_000, agentWindow: 200_000);
        var store = new Mock<ISessionStore>();

        await StreamingSessionHelper.ProcessAndSaveAsync(
            ToAsyncEnumerable(
            [
                new AgentStreamEvent { Type = AgentStreamEventType.MessageStart },
                new AgentStreamEvent { Type = AgentStreamEventType.MessageEnd }
            ]),
            session,
            store.Object,
            new StreamingSessionOptions(
                OnEventAsync: emitted.CaptureAsync,
                ContextExhaustionNotifier: notifier));

        emitted.DeliveredContent.ShouldHaveSingleItem().ShouldContain(
            "40,000",
            customMessage: "the notice must quote the conversation-scoped window, not the agent or global one.");
    }

    [Fact]
    public async Task WithoutTheConversationOverride_TheSamePromptCountIsNotExhaustion()
    {
        // The exact mirror of the test above with the override removed: 40,000 tokens against the
        // agent's 200,000-token window is not exhaustion, so nothing is emitted. Together the pair
        // proves the decision moved with the SCOPED window rather than with a constant.
        var emitted = new EmittedPayloads();
        var session = CreateSession("session-3535-unscoped", promptTokens: 40_000);
        var notifier = CreateNotifier(conversationOverride: null, agentWindow: 200_000);
        var store = new Mock<ISessionStore>();

        await StreamingSessionHelper.ProcessAndSaveAsync(
            ToAsyncEnumerable(
            [
                new AgentStreamEvent { Type = AgentStreamEventType.MessageStart },
                new AgentStreamEvent { Type = AgentStreamEventType.MessageEnd }
            ]),
            session,
            store.Object,
            new StreamingSessionOptions(
                OnEventAsync: emitted.CaptureAsync,
                ContextExhaustionNotifier: notifier));

        emitted.DeliveredContent.ShouldBeEmpty();
        session.GetHistorySnapshot().ShouldNotContain(e => e.Role.Equals(MessageRole.Notification));
    }

    [Fact]
    public async Task NoRecordedProviderCount_StaysSilent()
    {
        // Absence of a provider count is "unavailable", never zero. A session no provider has ever
        // reported usage for must keep the pre-#3535 behaviour.
        var emitted = new EmittedPayloads();
        var session = CreateSession("session-3535-unmeasured", promptTokens: null);
        var notifier = CreateNotifier(conversationOverride: null, agentWindow: 200_000);
        var store = new Mock<ISessionStore>();

        await StreamingSessionHelper.ProcessAndSaveAsync(
            ToAsyncEnumerable(
            [
                new AgentStreamEvent { Type = AgentStreamEventType.MessageStart },
                new AgentStreamEvent { Type = AgentStreamEventType.MessageEnd }
            ]),
            session,
            store.Object,
            new StreamingSessionOptions(
                OnEventAsync: emitted.CaptureAsync,
                ContextExhaustionNotifier: notifier));

        emitted.DeliveredContent.ShouldBeEmpty();
        session.GetHistorySnapshot().ShouldNotContain(e => e.Role.Equals(MessageRole.Notification));
    }

    // -------------------------------------------------------------------------------------------
    // The pure discriminator.
    // -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(200_000, 200_000, true)]   // at the window
    [InlineData(999_306, 200_000, true)]   // far past it - the observed session
    [InlineData(190_000, 200_000, true)]   // within the 5% margin
    [InlineData(189_999, 200_000, false)]  // one token outside the margin
    [InlineData(100_000, 200_000, false)]  // half full
    public void IsExhausted_AppliesTheFivePercentMargin(int promptTokens, int window, bool expected)
        => ContextExhaustionNotice.IsExhausted(promptTokens, window).ShouldBe(expected);

    [Theory]
    [InlineData(null, 200_000)]
    [InlineData(200_000, null)]
    [InlineData(0, 200_000)]
    [InlineData(200_000, 0)]
    public void IsExhausted_WithAnUnusableInput_IsFalse(int? promptTokens, int? window)
        => ContextExhaustionNotice.IsExhausted(promptTokens, window).ShouldBeFalse();

    // -------------------------------------------------------------------------------------------

    private static GatewaySession CreateSession(string sessionId, int? promptTokens)
    {
        var session = new GatewaySession
        {
            SessionId = SessionId.From(sessionId),
            AgentId = AgentId.From("agent-3535"),
            ConversationId = ConversationId.From("conversation-3535")
        };

        if (promptTokens.HasValue)
        {
            session.Metadata[ProviderPromptTokensKey] = promptTokens.Value;
        }

        return session;
    }

    private static ContextExhaustionNotifier CreateNotifier(int? conversationOverride, int? agentWindow)
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(It.IsAny<AgentId>())).Returns(new AgentDescriptor
        {
            AgentId = AgentId.From("agent-3535"),
            DisplayName = "Agent 3535",
            ApiProvider = "test-provider",
            ModelId = "test-model",
            ContextWindow = agentWindow
        });

        var conversations = new Mock<IConversationStore>();
        conversations.Setup(s => s.GetAsync(It.IsAny<ConversationId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Conversation
            {
                ConversationId = ConversationId.From("conversation-3535"),
                AgentId = AgentId.From("agent-3535"),
                ContextWindowOverride = conversationOverride
            });

        var models = new ModelRegistry();
        models.Register("test-provider", new LlmModel(
            Id: "test-model",
            Name: "Test Model",
            Api: "test-api",
            Provider: "test-provider",
            BaseUrl: "https://example.com",
            Reasoning: false,
            Input: ["text"],
            Cost: new ModelCost(0, 0, 0, 0),
            ContextWindow: 128_000,
            MaxTokens: 4096));

        var resolver = new SessionContextWindowResolver(
            NullLogger<SessionContextWindowResolver>.Instance,
            registry.Object,
            conversations.Object,
            new LlmClient(new ApiProviderRegistry(), models));

        return new ContextExhaustionNotifier(
            NullLogger<ContextExhaustionNotifier>.Instance,
            resolver);
    }

    private static async IAsyncEnumerable<AgentStreamEvent> ToAsyncEnumerable(IEnumerable<AgentStreamEvent> events)
    {
        foreach (var evt in events)
        {
            yield return evt;
            await Task.Yield();
        }
    }
}
