using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using AgentUserMessage = BotNexus.Agent.Core.Types.UserMessage;
using BotNexus.Agent.Core.Types;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Routing;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Sessions;
using ChannelAddress = BotNexus.Domain.Primitives.ChannelAddress;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Behavioural tests for Phase 5 / F-6 step 2 (#554): <c>GatewayHost.ResolveSessionType</c>
/// must derive the sub-agent species discriminator from the typed
/// <see cref="AgentDescriptor.Kind"/> via <see cref="IAgentRegistry"/>, not from
/// the legacy <c>SessionId.IsSubAgent</c> substring check.
/// </summary>
/// <remarks>
/// <para>
/// Each test pins one row of the migration truth table. Together they prove:
/// (1) typed signal works when registry holds a SubAgent descriptor;
/// (2) typed signal trumps the legacy substring even when the SessionId has
/// the canonical <c>::subagent::</c> infix and the descriptor is Named — the
/// session is bucketed as <see cref="SessionType.UserAgent"/>, not
/// <see cref="SessionType.AgentSubAgent"/>;
/// (3) when the descriptor is missing from the registry the host defaults to
/// <see cref="SessionType.UserAgent"/> and does NOT fall back to the substring
/// — proving the legacy code path has been cut;
/// (4) <see cref="SessionType.Soul"/> and <see cref="SessionType.Cron"/>
/// classifications remain intact (regression-pin for the other branches).
/// </para>
/// </remarks>
public sealed class GatewayHostResolveSessionTypeTests
{
    private const string AgentIdValue = "agent-resolve";

    [Fact]
    public async Task DispatchAsync_WhenRegistryReturnsSubAgentDescriptor_StampsSessionTypeAsAgentSubAgent()
    {
        // Registry-backed typed signal: descriptor.Kind = SubAgent -> SessionType.AgentSubAgent.
        const string sessionIdValue = "session-with-subagent-descriptor";
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(AgentId.From(AgentIdValue)))
            .Returns(CreateDescriptor(AgentKind.SubAgent));
        var sessions = new InMemorySessionStore();
        await using var host = CreateHost(sessions, registry.Object, sessionIdValue);

        await host.DispatchAsync(CreateMessage(sessionIdValue, "hello"));

        var reloaded = await sessions.GetAsync(SessionId.From(sessionIdValue));
        reloaded.ShouldNotBeNull();
        reloaded!.SessionType.ShouldBe(SessionType.AgentSubAgent);
    }

    [Fact]
    public async Task DispatchAsync_WhenRegistryReturnsNamedDescriptor_AndSessionIdHasSubAgentInfix_StampsAsUserAgent()
    {
        // CRITICAL regression pin: typed signal trumps substring. Even with the
        // legacy "::subagent::" infix on the SessionId, a Named descriptor
        // means SessionType.UserAgent. This is the row that proves the migration
        // off SessionId.IsSubAgent is complete in this code path.
        var subAgentShapedId = SessionId.ForSubAgent("parent-session", "child-1");
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(AgentId.From(AgentIdValue)))
            .Returns(CreateDescriptor(AgentKind.Named));
        var sessions = new InMemorySessionStore();
        await using var host = CreateHost(sessions, registry.Object, subAgentShapedId.Value);

        await host.DispatchAsync(CreateMessage(subAgentShapedId.Value, "hello"));

        var reloaded = await sessions.GetAsync(subAgentShapedId);
        reloaded.ShouldNotBeNull();
        reloaded!.SessionType.ShouldBe(SessionType.UserAgent);
    }

    [Fact]
    public async Task DispatchAsync_WhenRegistryHasNoDescriptor_AndSessionIdHasSubAgentInfix_StampsAsUserAgent()
    {
        // Cuts the legacy substring fallback: if the registry doesn't know the
        // agent (transient race, deregister, or test misconfiguration), we MUST
        // default to UserAgent rather than parsing the SessionId. This is the
        // exact path that #554 deletes.
        var subAgentShapedId = SessionId.ForSubAgent("parent-session", "child-2");
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(It.IsAny<AgentId>())).Returns((AgentDescriptor?)null);
        var sessions = new InMemorySessionStore();
        await using var host = CreateHost(sessions, registry.Object, subAgentShapedId.Value);

        await host.DispatchAsync(CreateMessage(subAgentShapedId.Value, "hello"));

        var reloaded = await sessions.GetAsync(subAgentShapedId);
        reloaded.ShouldNotBeNull();
        reloaded!.SessionType.ShouldBe(SessionType.UserAgent);
    }

    [Fact]
    public async Task DispatchAsync_WhenRegistryNotProvided_AndSessionIdHasSubAgentInfix_StampsAsUserAgent()
    {
        // Belt-and-braces variant of the above: no registry passed at all
        // (test composition root). Still must NOT fall back to substring detection.
        var subAgentShapedId = SessionId.ForSubAgent("parent-session", "child-3");
        var sessions = new InMemorySessionStore();
        await using var host = CreateHost(sessions, registry: null, sessionIdValue: subAgentShapedId.Value);

        await host.DispatchAsync(CreateMessage(subAgentShapedId.Value, "hello"));

        var reloaded = await sessions.GetAsync(subAgentShapedId);
        reloaded.ShouldNotBeNull();
        reloaded!.SessionType.ShouldBe(SessionType.UserAgent);
    }

    [Fact]
    public async Task DispatchAsync_WhenSessionIdIsSoul_StampsAsSoul()
    {
        // Regression pin: Soul bucketing is preserved. The Soul predicate
        // (SessionId.IsSoul) is unrelated to the AgentKind migration and
        // remains in place.
        var soulId = SessionId.ForSoul(AgentId.From(AgentIdValue), DateOnly.FromDateTime(DateTime.UtcNow));
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(AgentId.From(AgentIdValue)))
            .Returns(CreateDescriptor(AgentKind.Named));
        var sessions = new InMemorySessionStore();
        await using var host = CreateHost(sessions, registry.Object, soulId.Value);

        await host.DispatchAsync(CreateMessage(soulId.Value, "hello"));

        var reloaded = await sessions.GetAsync(soulId);
        reloaded.ShouldNotBeNull();
        reloaded!.SessionType.ShouldBe(SessionType.Soul);
    }

    [Fact]
    public async Task DispatchAsync_WhenChannelTypeIsCron_StampsAsCron()
    {
        // Regression pin: Cron bucketing is preserved (driven by channel type,
        // not by the descriptor or the SessionId).
        const string sessionIdValue = "cron-session-1";
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(AgentId.From(AgentIdValue)))
            .Returns(CreateDescriptor(AgentKind.Named));
        var sessions = new InMemorySessionStore();
        await using var host = CreateHost(sessions, registry.Object, sessionIdValue);

        await host.DispatchAsync(CreateMessage(sessionIdValue, "fire", channelType: "cron"));

        var reloaded = await sessions.GetAsync(SessionId.From(sessionIdValue));
        reloaded.ShouldNotBeNull();
        reloaded!.SessionType.ShouldBe(SessionType.Cron);
    }

    [Fact]
    public async Task DispatchAsync_WhenChannelTypeIsCron_AndDescriptorKindIsSubAgent_PrefersSubAgentBucketing()
    {
        // Edge case truth-table row: the SubAgent kind takes precedence over
        // Cron channel-type because the original method's branch order
        // checked sub-agent first. Pinned so the precedence doesn't flip
        // silently under refactor.
        const string sessionIdValue = "cron-by-subagent";
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(AgentId.From(AgentIdValue)))
            .Returns(CreateDescriptor(AgentKind.SubAgent));
        var sessions = new InMemorySessionStore();
        await using var host = CreateHost(sessions, registry.Object, sessionIdValue);

        await host.DispatchAsync(CreateMessage(sessionIdValue, "fire", channelType: "cron"));

        var reloaded = await sessions.GetAsync(SessionId.From(sessionIdValue));
        reloaded.ShouldNotBeNull();
        reloaded!.SessionType.ShouldBe(SessionType.AgentSubAgent);
    }

    private static AgentDescriptor CreateDescriptor(AgentKind kind)
        => new()
        {
            AgentId = AgentId.From(AgentIdValue),
            DisplayName = AgentIdValue,
            ModelId = "test-model",
            ApiProvider = "test-provider",
            Kind = kind
        };

    private static GatewayHost CreateHost(
        InMemorySessionStore sessions,
        IAgentRegistry? registry,
        string sessionIdValue)
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([AgentIdValue]);

        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(AgentId.From(AgentIdValue));
        handle.SetupGet(h => h.SessionId).Returns(SessionId.From(sessionIdValue));
        handle.Setup(h => h.IsRunning).Returns(false);
        handle.Setup(h => h.PromptAsync(It.IsAny<AgentUserMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "ack" });
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "ack" });

        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(
                It.IsAny<AgentId>(),
                It.IsAny<SessionId>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var channelManager = new Mock<IChannelManager>();
        channelManager.SetupGet(m => m.Adapters).Returns([]);
        channelManager.Setup(m => m.Get(It.IsAny<ChannelKey>())).Returns((IChannelAdapter?)null);
        channelManager.Setup(m => m.Get(It.IsAny<ChannelKey>(), It.IsAny<string?>())).Returns((IChannelAdapter?)null);

        return new GatewayHost(
            supervisor.Object,
            router.Object,
            sessions,
            Mock.Of<IActivityBroadcaster>(),
            channelManager.Object,
            Mock.Of<ISessionCompactor>(),
            new TestOptionsMonitor<CompactionOptions>(new CompactionOptions()),
            NullLogger<GatewayHost>.Instance,
            registry: registry);
    }

    private static InboundMessage CreateMessage(string sessionId, string content, string channelType = "web")
        => new()
        {
            ChannelType = ChannelKey.From(channelType),
            SenderId = "sender-1",
            Sender = CitizenId.Of(UserId.From("sender-1")),
            ChannelAddress = ChannelAddress.From("conv-1"),
            Content = content,
            SessionId = sessionId
        };
}
