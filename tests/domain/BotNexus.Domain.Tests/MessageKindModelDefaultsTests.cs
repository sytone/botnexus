using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Models;
using ChannelAddress = BotNexus.Domain.Primitives.ChannelAddress;

namespace BotNexus.Domain.Tests;

/// <summary>
/// Pins the "default safely to message" contract for issue #2149 across the model records the kind
/// threads through: <see cref="SessionEntry"/>, <see cref="InboundMessage"/>, and
/// <see cref="OutboundMessage"/>. An absent kind must resolve to <see cref="MessageKind.Message"/>
/// so legacy rows and ordinary responses stay indistinguishable from a freshly-defaulted value, and
/// <see cref="MessageRole"/> must remain orthogonal to the kind.
/// </summary>
public sealed class MessageKindModelDefaultsTests
{
    [Fact]
    public void SessionEntry_WhenKindNotSet_ShouldResolveToMessage()
    {
        var entry = new SessionEntry { Role = MessageRole.Assistant, Content = "hi" };
        entry.Kind.ShouldBeNull();
        entry.ResolveKind().ShouldBe(MessageKind.Message);
    }

    [Fact]
    public void SessionEntry_WhenKindSet_ShouldResolveToThatKind()
    {
        var entry = new SessionEntry
        {
            Role = MessageRole.Assistant,
            Content = "hi",
            Kind = MessageKind.SubAgentResponse
        };
        entry.ResolveKind().ShouldBe(MessageKind.SubAgentResponse);
        // Role stays orthogonal to the kind.
        entry.Role.ShouldBe(MessageRole.Assistant);
    }

    [Fact]
    public void InboundMessage_WhenKindNotSet_ShouldResolveToMessage()
    {
        var message = CreateInbound(kind: null);
        message.Kind.ShouldBeNull();
        message.ResolveKind().ShouldBe(MessageKind.Message);
    }

    [Fact]
    public void InboundMessage_WhenCompletionKindSet_ShouldResolveToCompletion()
    {
        var message = CreateInbound(kind: MessageKind.SubAgentCompletion);
        message.ResolveKind().ShouldBe(MessageKind.SubAgentCompletion);
    }

    [Fact]
    public void OutboundMessage_WhenKindNotSet_ShouldResolveToMessage()
    {
        var message = new OutboundMessage
        {
            ChannelType = ChannelKey.From("web"),
            ChannelAddress = ChannelAddress.From("conv-1"),
            Content = "hi"
        };
        message.Kind.ShouldBeNull();
        message.ResolveKind().ShouldBe(MessageKind.Message);
    }

    [Fact]
    public void OutboundMessage_WhenResponseKindSet_ShouldResolveToResponse()
    {
        var message = new OutboundMessage
        {
            ChannelType = ChannelKey.From("web"),
            ChannelAddress = ChannelAddress.From("conv-1"),
            Content = "hi",
            Kind = MessageKind.SubAgentResponse
        };
        message.ResolveKind().ShouldBe(MessageKind.SubAgentResponse);
    }

    private static InboundMessage CreateInbound(MessageKind? kind) => new()
    {
        ChannelType = ChannelKey.From("internal"),
        SenderId = "subagent:child-1",
        Sender = CitizenId.Of(AgentId.From("child-agent")),
        ChannelAddress = ChannelAddress.From("parent-session"),
        Content = "done",
        Kind = kind
    };
}
