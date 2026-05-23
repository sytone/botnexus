using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using ChannelAddress = BotNexus.Domain.Primitives.ChannelAddress;
using AgentUserMessage = BotNexus.Agent.Core.Types.UserMessage;
using BotNexus.Agent.Core.Types;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Routing;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Behavioural tests locking in the Phase 2c typed-Sender contract on
/// <see cref="InboundMessage"/>: the runtime <see cref="CitizenId.IsValid"/> guard,
/// and species-correct participant tracking for User vs Agent senders (including the
/// sub-agent wake-up case where the wire <c>SenderId</c> and typed <c>Sender</c>
/// deliberately diverge).
/// </summary>
public sealed class SenderClassificationTests
{
    private const string AgentIdValue = "agent-classification";
    private const string SessionIdValue = "session-classification";

    [Fact]
    public async Task DispatchAsync_DefaultSender_ThrowsArgumentException()
    {
        await using var host = CreateHost(new InMemorySessionStore(), out _);

        var message = new InboundMessage
        {
            ChannelType = ChannelKey.From("web"),
            SenderId = "sender-1",
            Sender = default,
            ChannelAddress = ChannelAddress.From("conv-1"),
            Content = "hello"
        };

        var ex = await Should.ThrowAsync<ArgumentException>(
            async () => await host.DispatchAsync(message));
        ex.ParamName.ShouldBe("message");
    }

    [Fact]
    public async Task DispatchAsync_UserSender_AddsUserParticipant()
    {
        var sessions = new InMemorySessionStore();
        await using var host = CreateHost(sessions, out _);

        var senderId = UserId.From("alice");
        await host.DispatchAsync(new InboundMessage
        {
            ChannelType = ChannelKey.From("web"),
            SenderId = "alice",
            Sender = CitizenId.Of(senderId),
            ChannelAddress = ChannelAddress.From("conv-1"),
            SessionId = SessionIdValue,
            Content = "hello"
        });

        var reloaded = await sessions.GetAsync(SessionId.From(SessionIdValue));
        reloaded.ShouldNotBeNull();
        var participant = reloaded!.Participants.ShouldHaveSingleItem();
        participant.CitizenId.Kind.ShouldBe(CitizenKind.User);
        participant.CitizenId.AsUser.ShouldNotBeNull().ShouldBe(senderId);
    }

    [Fact]
    public async Task DispatchAsync_AgentSender_AddsAgentParticipant_NotMisclassifiedAsUser()
    {
        var sessions = new InMemorySessionStore();
        await using var host = CreateHost(sessions, out _);

        // The sub-agent wake-up case: wire token is "subagent:..." but the typed
        // Sender carries the child agent id. Phase 2c regression-guard for the
        // original misclassification where the participant was added as a User.
        var childAgentId = AgentId.From("child-agent-x");
        await host.DispatchAsync(new InboundMessage
        {
            ChannelType = ChannelKey.From("internal"),
            SenderId = "subagent:wake-1",
            Sender = CitizenId.Of(childAgentId),
            ChannelAddress = ChannelAddress.From(SessionIdValue),
            SessionId = SessionIdValue,
            TargetAgentId = AgentIdValue,
            Content = "wake-up"
        });

        var reloaded = await sessions.GetAsync(SessionId.From(SessionIdValue));
        reloaded.ShouldNotBeNull();
        var participant = reloaded!.Participants.ShouldHaveSingleItem();
        participant.CitizenId.Kind.ShouldBe(CitizenKind.Agent);
        participant.CitizenId.AsAgent.ShouldNotBeNull().ShouldBe(childAgentId);
    }

    [Fact]
    public async Task DispatchAsync_SameCitizen_DoesNotAddDuplicateParticipant()
    {
        var sessions = new InMemorySessionStore();
        await using var host = CreateHost(sessions, out _);

        var senderId = UserId.From("bob");
        var template = new InboundMessage
        {
            ChannelType = ChannelKey.From("web"),
            SenderId = "bob",
            Sender = CitizenId.Of(senderId),
            ChannelAddress = ChannelAddress.From("conv-1"),
            SessionId = SessionIdValue,
            Content = "hello"
        };

        await host.DispatchAsync(template);
        await host.DispatchAsync(template with { Content = "again" });

        var reloaded = await sessions.GetAsync(SessionId.From(SessionIdValue));
        reloaded.ShouldNotBeNull();
        reloaded!.Participants.Count.ShouldBe(1, "second dispatch from same citizen should not add a duplicate participant");
    }

    [Fact]
    public async Task DispatchAsync_WireSenderId_AndTypedSender_AreIndependent()
    {
        // Documents the two-field invariant. SenderId is the channel-native wire
        // token (audit / allowlist / logging); Sender is the typed domain identity.
        // They are deliberately allowed to differ -- the sub-agent wake-up uses
        // "subagent:X" as the wire token while Sender carries the agent's id.
        var sessions = new InMemorySessionStore();
        await using var host = CreateHost(sessions, out _);

        var childAgentId = AgentId.From("child-agent-y");
        await host.DispatchAsync(new InboundMessage
        {
            ChannelType = ChannelKey.From("internal"),
            SenderId = "subagent:wake-2",
            Sender = CitizenId.Of(childAgentId),
            ChannelAddress = ChannelAddress.From(SessionIdValue),
            SessionId = SessionIdValue,
            TargetAgentId = AgentIdValue,
            Content = "follow-up"
        });

        var reloaded = await sessions.GetAsync(SessionId.From(SessionIdValue));
        reloaded.ShouldNotBeNull();
        // Wire token survives on the session for audit/back-compat.
        reloaded!.CallerId.ShouldBe("subagent:wake-2");
        // Typed species is used for participant tracking.
        var participant = reloaded.Participants.ShouldHaveSingleItem();
        participant.CitizenId.AsAgent.ShouldNotBeNull().ShouldBe(childAgentId);
    }

    private static GatewayHost CreateHost(InMemorySessionStore sessions, out Mock<IAgentSupervisor> supervisor)
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([AgentIdValue]);

        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(AgentId.From(AgentIdValue));
        handle.SetupGet(h => h.SessionId).Returns(SessionId.From(SessionIdValue));
        handle.Setup(h => h.IsRunning).Returns(false);
        handle.Setup(h => h.PromptAsync(It.IsAny<AgentUserMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "ack" });
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "ack" });

        var supervisorMock = new Mock<IAgentSupervisor>();
        supervisorMock.Setup(s => s.GetOrCreateAsync(
                It.IsAny<AgentId>(),
                It.IsAny<SessionId>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        supervisor = supervisorMock;

        var channelManager = new Mock<IChannelManager>();
        channelManager.SetupGet(m => m.Adapters).Returns([]);
        channelManager.Setup(m => m.Get(It.IsAny<ChannelKey>())).Returns((IChannelAdapter?)null);
        channelManager.Setup(m => m.Get(It.IsAny<ChannelKey>(), It.IsAny<string?>())).Returns((IChannelAdapter?)null);

        return new GatewayHost(
            supervisorMock.Object,
            router.Object,
            sessions,
            Mock.Of<IActivityBroadcaster>(),
            channelManager.Object,
            Mock.Of<ISessionCompactor>(),
            new TestOptionsMonitor<CompactionOptions>(new CompactionOptions()),
            NullLogger<GatewayHost>.Instance);
    }
}
