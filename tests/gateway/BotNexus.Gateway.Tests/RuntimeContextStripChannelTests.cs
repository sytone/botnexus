using BotNexus.Extensions.Channels.SignalR;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Channels;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// #1430: the delimited internal runtime-context envelope must be stripped at the user-visible
/// channel projection seam, and must be preserved on internal / agent-to-agent surfaces.
/// </summary>
public sealed class RuntimeContextStripChannelTests
{
    private const string Begin = RuntimeContextRedactor.BeginDelimiter;
    private const string End = RuntimeContextRedactor.EndDelimiter;

    private static (SignalRChannelAdapter Adapter, Mock<IGatewayHubClient> Client) CreateSignalRAdapter(string group)
    {
        var clientProxy = new Mock<IGatewayHubClient>();
        clientProxy.Setup(proxy => proxy.ContentDelta(It.IsAny<object>())).Returns(Task.CompletedTask);

        var clients = new Mock<IHubClients<IGatewayHubClient>>();
        clients.Setup(value => value.Group(group)).Returns(clientProxy.Object);

        var hubContext = new Mock<IHubContext<GatewayHub, IGatewayHubClient>>();
        hubContext.SetupGet(value => value.Clients).Returns(clients.Object);

        return (new SignalRChannelAdapter(NullLogger<SignalRChannelAdapter>.Instance, hubContext.Object), clientProxy);
    }

    [Fact]
    public void RedactorDelimiters_MatchTheRuntimeLineFormatterLiterals()
    {
        // The channel layer duplicates the literals rather than depending on the prompt-building
        // project; this pin fails loudly if the two ever drift apart.
        Assert.Equal(BotNexus.Gateway.Prompts.RuntimeLineFormatter.RuntimeContextBeginDelimiter, Begin);
        Assert.Equal(BotNexus.Gateway.Prompts.RuntimeLineFormatter.RuntimeContextEndDelimiter, End);
    }

    [Fact]
    public async Task SignalRSendAsync_StripsDelimitedRuntimeContextBlock()
    {
        var (adapter, client) = CreateSignalRAdapter("conversation:conv-strip");

        await adapter.SendAsync(new OutboundMessage
        {
            ChannelType = ChannelKey.From("signalr"),
            ChannelAddress = ChannelAddress.From("addr-strip"),
            Content = $"Here you go.\n{Begin}\nRuntime: agent=a | host=SECRET-BOX\n{End}\nDone.",
            SessionId = "session-strip",
            ConversationId = "conv-strip"
        });

        client.Verify(proxy => proxy.ContentDelta(
                It.Is<object>(arg =>
                    arg is ContentDeltaPayload &&
                    ((ContentDeltaPayload)arg).ContentDelta == "Here you go.\nDone.")),
            Times.Once);
    }

    [Fact]
    public async Task SignalRSendAsync_StripsRealEnvelope_WhenEchoedMarkerUnbalancesCounts()
    {
        // #2520: a stray END echoed from untrusted text used to unbalance the marker counts and
        // suppress the strip entirely, delivering the real envelope to the channel.
        const string sentinel = "sess-4f21c0de-LEAK-CANARY";
        var content = $"The page said {End} verbatim.\n{Begin}\nRuntime: session={sentinel}\n{End}\nDone.";
        var (adapter, client) = CreateSignalRAdapter("conversation:conv-guard");

        await adapter.SendAsync(new OutboundMessage
        {
            ChannelType = ChannelKey.From("signalr"),
            ChannelAddress = ChannelAddress.From("addr-guard"),
            Content = content,
            SessionId = "session-guard",
            ConversationId = "conv-guard"
        });

        client.Verify(proxy => proxy.ContentDelta(
                It.Is<object>(arg =>
                    arg is ContentDeltaPayload &&
                    !((ContentDeltaPayload)arg).ContentDelta!.Contains(sentinel) &&
                    !((ContentDeltaPayload)arg).ContentDelta!.Contains(Begin))),
            Times.Once);
    }

    [Fact]
    public async Task SignalRSendAsync_LeavesProseMentioningEndMarkerByteIdentical()
    {
        // The fail-closed change must not mangle a legitimate reply that merely names the marker.
        var content = $"You asked about {End} - here is what it means.";
        var (adapter, client) = CreateSignalRAdapter("conversation:conv-prose");

        await adapter.SendAsync(new OutboundMessage
        {
            ChannelType = ChannelKey.From("signalr"),
            ChannelAddress = ChannelAddress.From("addr-prose"),
            Content = content,
            SessionId = "session-prose",
            ConversationId = "conv-prose"
        });

        client.Verify(proxy => proxy.ContentDelta(
                It.Is<object>(arg =>
                    arg is ContentDeltaPayload &&
                    ((ContentDeltaPayload)arg).ContentDelta == content)),
            Times.Once);
    }

    [Fact]
    public async Task SignalRSendAsync_LeavesOrdinaryContentByteIdentical()
    {
        const string content = "An ordinary reply with no runtime context at all.";
        var (adapter, client) = CreateSignalRAdapter("conversation:conv-plain");

        await adapter.SendAsync(new OutboundMessage
        {
            ChannelType = ChannelKey.From("signalr"),
            ChannelAddress = ChannelAddress.From("addr-plain"),
            Content = content,
            SessionId = "session-plain",
            ConversationId = "conv-plain"
        });

        client.Verify(proxy => proxy.ContentDelta(
                It.Is<object>(arg =>
                    arg is ContentDeltaPayload &&
                    ((ContentDeltaPayload)arg).ContentDelta == content)),
            Times.Once);
    }

    [Fact]
    public async Task InternalSurface_PreservesRuntimeContextBlock()
    {
        // An adapter that does NOT opt in (the base-class default) models the internal /
        // agent-to-agent / transcript surfaces, which keep the runtime-context block.
        var content = $"reply\n{Begin}\nRuntime: agent=a\n{End}\ntail";
        var adapter = new InternalProbeAdapter();

        await adapter.SendAsync(new OutboundMessage
        {
            ChannelType = ChannelKey.From("internal-probe"),
            ChannelAddress = ChannelAddress.From("addr-internal"),
            Content = content
        });

        Assert.Equal(content, adapter.Delivered);
    }

    /// <summary>
    /// Minimal internal-surface adapter: inherits the default <c>StripsRuntimeContext == false</c>
    /// so the projection is a pass-through.
    /// </summary>
    private sealed class InternalProbeAdapter() : ChannelAdapterBase(NullLogger<InternalProbeAdapter>.Instance)
    {
        public string? Delivered { get; private set; }

        public override ChannelKey ChannelType => ChannelKey.From("internal-probe");

        public override string DisplayName => "Internal Probe";

        public override Task SendAsync(OutboundMessage message, CancellationToken cancellationToken = default)
        {
            Delivered = ProjectOutboundText(message.Content);
            return Task.CompletedTask;
        }

        protected override Task OnStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        protected override Task OnStopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
