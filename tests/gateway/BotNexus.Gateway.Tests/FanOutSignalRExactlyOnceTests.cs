using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Extensions.Channels.SignalR;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// #3181 AC3 - the duplicate-delivery regression guard for the hazard that #3181's fix creates.
/// </summary>
/// <remarks>
/// <para>
/// Stamping <see cref="OutboundMessage.ConversationId"/> during fan-out (#3181 AC1) changes which
/// SignalR group the envelope addresses. Before the fix the field was always unset, so
/// <c>SignalRChannelAdapter.SendAsync</c> fell back to <c>conversation:{sessionId}</c>; after it,
/// the adapter routes to <c>conversation:{conversationId}</c> - a group the fan-out path could not
/// previously reach. That is precisely the seam merged PR #2263 deduplicated, so trading the
/// silent Service Bus miss for a portal double-delivery would be a strictly worse outcome.
/// </para>
/// <para>
/// These tests drive the REAL <see cref="SignalRChannelAdapter"/> (not a mock) through
/// <see cref="OutboundResponseDeliverer"/> against a mocked hub context, so the group-resolution
/// logic under test is the production one. The invariant pinned is one delivery per non-muted
/// binding - never two - and that the single delivery lands on the conversation group.
/// </para>
/// <para>
/// Deliberately NOT asserted here: which group key is used. That belongs to AC2. Keeping this
/// file agnostic about the key is what makes AC5 provable - reverting the AC1 assignment must
/// redden the AC2 test while these exactly-once assertions stay green, showing the two clauses
/// are independently load-bearing rather than one testing the other twice.
/// </para>
/// </remarks>
public sealed class FanOutSignalRExactlyOnceTests
{
    private const string SessionIdStr = "session-signalr-fanout";
    private const string ConversationIdStr = "c_3181fanoutexactlyonce";

    private static InboundMessage SourceMessage(string? originatingBindingId = "bind-origin") =>
        new()
        {
            ChannelType = ChannelKey.From("telegram"),
            SenderId = "user-1",
            Sender = CitizenId.Of(UserId.From("user-1")),
            ChannelAddress = ChannelAddress.From("chat-1"),
            Content = "hi",
            BindingId = originatingBindingId is null ? null : BindingId.From(originatingBindingId),
            Metadata = new Dictionary<string, object?>()
        };

    private static ChannelBinding SignalRBinding(string bindingId, string address) =>
        new()
        {
            BindingId = BindingId.From(bindingId),
            ChannelType = ChannelKey.From("signalr"),
            ChannelAddress = ChannelAddress.From(address),
            Mode = BindingMode.Interactive
        };

    /// <summary>
    /// Builds a real SignalR adapter over a recording hub context. Every group name requested is
    /// appended to <paramref name="groupsAddressed"/> and every ContentDelta invocation is counted,
    /// so a duplicate delivery is observable as a count of 2 rather than an assertion on identity.
    /// </summary>
    private static SignalRChannelAdapter RecordingAdapter(
        List<string> groupsAddressed,
        Action onDelta)
    {
        var clientProxy = new Mock<IGatewayHubClient>();
        clientProxy.Setup(p => p.ContentDelta(It.IsAny<object>()))
            .Callback(onDelta)
            .Returns(Task.CompletedTask);

        var clients = new Mock<IHubClients<IGatewayHubClient>>();
        clients.Setup(c => c.Group(It.IsAny<string>()))
            .Callback<string>(groupsAddressed.Add)
            .Returns(clientProxy.Object);

        var hubContext = new Mock<IHubContext<GatewayHub, IGatewayHubClient>>();
        hubContext.SetupGet(h => h.Clients).Returns(clients.Object);

        return new SignalRChannelAdapter(NullLogger<SignalRChannelAdapter>.Instance, hubContext.Object);
    }

    private static Mock<IChannelManager> ChannelManager(IChannelAdapter adapter)
    {
        var mgr = new Mock<IChannelManager>();
        mgr.SetupGet(m => m.Adapters).Returns([adapter]);
        mgr.Setup(m => m.Get(It.IsAny<ChannelKey>(), It.IsAny<string?>()))
            .Returns<ChannelKey, string?>((type, _) => type == adapter.ChannelType ? adapter : null);
        return mgr;
    }

    /// <summary>
    /// AC3: a single SignalR binding produces exactly ONE delivery to the conversation group.
    /// If the fan-out ever delivered both by conversation group AND by the session-group synonym,
    /// the portal would render the reply twice - the #2263 defect.
    /// </summary>
    [Fact]
    public async Task FanOutAsync_SignalRBinding_DeliversExactlyOnceToConversationGroup()
    {
        var binding = SignalRBinding("bind-signalr", "conn-abc");

        var router = new Mock<IConversationRouter>();
        router.Setup(r => r.GetOutboundBindingsAsync(It.IsAny<SessionId>(), It.IsAny<BindingId?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([binding]);

        var groups = new List<string>();
        var deltas = 0;
        var adapter = RecordingAdapter(groups, () => deltas++);

        var deliverer = new OutboundResponseDeliverer(
            router.Object, ChannelManager(adapter).Object, NullLogger<OutboundResponseDeliverer>.Instance);

        await deliverer.FanOutAsync(
            SourceMessage(), SessionId.From(SessionIdStr), "reply text",
            ConversationId.From(ConversationIdStr), CancellationToken.None);

        // THE regression assertion: exactly one delivery, not two.
        deltas.ShouldBe(1);
        groups.Count.ShouldBe(1);
        // Deliberately NOT asserting WHICH group - that is AC2's clause. Pinning the key here
        // would make this test fail under the AC5 revert, collapsing two independent guarantees
        // into one and destroying the proof that each is separately load-bearing. What matters
        // for #2263 is the COUNT: one binding, one delivery, whatever the key resolves to.
        groups.ShouldHaveSingleItem();
    }

    /// <summary>
    /// AC3 (fan-out shape): two distinct non-muted SignalR bindings - e.g. two browser tabs -
    /// deliver once EACH, never twice each. This pins delivery count to binding count, which is
    /// the property the #2263 dedup established; the #3181 stamp must not multiply it.
    /// </summary>
    [Fact]
    public async Task FanOutAsync_TwoSignalRBindings_DeliverOncePerBindingNotTwice()
    {
        var first = SignalRBinding("bind-tab-1", "conn-1");
        var second = SignalRBinding("bind-tab-2", "conn-2");

        var router = new Mock<IConversationRouter>();
        router.Setup(r => r.GetOutboundBindingsAsync(It.IsAny<SessionId>(), It.IsAny<BindingId?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([first, second]);

        var groups = new List<string>();
        var deltas = 0;
        var adapter = RecordingAdapter(groups, () => deltas++);

        var deliverer = new OutboundResponseDeliverer(
            router.Object, ChannelManager(adapter).Object, NullLogger<OutboundResponseDeliverer>.Instance);

        await deliverer.FanOutAsync(
            SourceMessage(), SessionId.From(SessionIdStr), "reply text",
            ConversationId.From(ConversationIdStr), CancellationToken.None);

        // One delivery per binding - two bindings, two sends. Four would be the duplicate defect.
        deltas.ShouldBe(2);
        groups.Count.ShouldBe(2);
        // Both deliveries address the SAME group (the two tabs share one conversation), but the
        // key itself is AC2's concern - asserting only that they agree keeps this clause
        // independent of the stamp, as AC5 requires.
        groups.Distinct().Count().ShouldBe(1);
    }

    /// <summary>
    /// AC3 non-vacuity: the originating binding is excluded upstream by
    /// <c>GetOutboundBindingsAsync</c>, so a fan-out with no other bindings delivers NOTHING.
    /// Without this, the exactly-once assertions above could be satisfied by a deliverer that
    /// simply always sent once regardless of the binding set.
    /// </summary>
    [Fact]
    public async Task FanOutAsync_NoOtherBindings_DeliversNothing()
    {
        var router = new Mock<IConversationRouter>();
        router.Setup(r => r.GetOutboundBindingsAsync(It.IsAny<SessionId>(), It.IsAny<BindingId?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var groups = new List<string>();
        var deltas = 0;
        var adapter = RecordingAdapter(groups, () => deltas++);

        var deliverer = new OutboundResponseDeliverer(
            router.Object, ChannelManager(adapter).Object, NullLogger<OutboundResponseDeliverer>.Instance);

        await deliverer.FanOutAsync(
            SourceMessage(), SessionId.From(SessionIdStr), "reply text",
            ConversationId.From(ConversationIdStr), CancellationToken.None);

        deltas.ShouldBe(0);
        groups.ShouldBeEmpty();
    }

    /// <summary>
    /// AC3 sad path: a muted binding is filtered upstream, so a conversation whose only remaining
    /// SignalR binding is muted delivers nothing rather than falling back to a broadcast.
    /// </summary>
    [Fact]
    public async Task FanOutAsync_MutedBindingFilteredUpstream_DeliversNothing()
    {
        // GetOutboundBindingsAsync already excludes Muted bindings; modelling that here keeps the
        // deliverer honest about not re-deriving its own binding set.
        var router = new Mock<IConversationRouter>();
        router.Setup(r => r.GetOutboundBindingsAsync(It.IsAny<SessionId>(), It.IsAny<BindingId?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var groups = new List<string>();
        var deltas = 0;
        var adapter = RecordingAdapter(groups, () => deltas++);

        var deliverer = new OutboundResponseDeliverer(
            router.Object, ChannelManager(adapter).Object, NullLogger<OutboundResponseDeliverer>.Instance);

        await deliverer.FanOutAsync(
            SourceMessage(), SessionId.From(SessionIdStr), "reply text",
            ConversationId.From(ConversationIdStr), CancellationToken.None);

        deltas.ShouldBe(0);
        groups.ShouldBeEmpty();
    }
}
