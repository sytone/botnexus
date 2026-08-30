using BotNexus.Agent.Core.Types;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Routing;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// #3541: <c>GatewayHost.ResolveChannelAdapter</c> must consult the shared non-deliverable
/// classification before logging a WARNING. #3167 taught only the fan-out path
/// (<c>OutboundResponseDeliverer.DeliverToBindingAsync</c>) that <c>webhook</c> has no adapter by
/// design, so this path kept emitting ~54 by-design WARNINGs/day using the same message text as a
/// genuine adapter outage.
/// </summary>
/// <remarks>
/// Non-vacuity: <see cref="ResolveChannelAdapter_DeliverableChannel_StillWarns"/> is the control.
/// It fires the identical code path with a deliverable-but-unregistered channel type and asserts
/// the WARNING IS produced, so the webhook assertion cannot pass merely because nothing logged.
/// Mutation check: deleting the <c>IsNonDeliverableChannel</c> guard from
/// <c>ResolveChannelAdapter</c> makes <see cref="ResolveChannelAdapter_NonDeliverableChannel_LogsNoWarning"/>
/// fail.
/// </remarks>
public sealed class GatewayHostResolveChannelAdapterGuardTests
{
    [Theory]
    [InlineData("webhook")]
    [InlineData("Webhook")]
    [InlineData("cron")]
    [InlineData("exchange")]
    public async Task ResolveChannelAdapter_NonDeliverableChannel_LogsNoWarning(string channelType)
    {
        var (records, host) = CreateHost();
        await using var _ = host;

        await host.DispatchAsync(CreateMessage(channelType));

        Assert.DoesNotContain(
            records,
            r => r.Level == LogLevel.Warning && r.Message.Contains("No channel adapter found for type", StringComparison.Ordinal));

        Assert.Contains(
            records,
            r => r.Level == LogLevel.Debug && r.Message.Contains("Skipping non-deliverable channel type", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResolveChannelAdapter_DeliverableChannel_StillWarns()
    {
        // Control: an unregistered but DELIVERABLE channel type is a real misconfiguration and
        // must keep its WARNING. Without this the assertion above would pass vacuously.
        var (records, host) = CreateHost();
        await using var _ = host;

        await host.DispatchAsync(CreateMessage("slack"));

        Assert.Contains(
            records,
            r => r.Level == LogLevel.Warning && r.Message.Contains("No channel adapter found for type", StringComparison.Ordinal));
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static (List<LogRecord> Records, GatewayHost Host) CreateHost()
    {
        const string agentId = "agent-guard";
        const string sessionId = "session-guard";

        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([agentId]);

        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(x => x.AgentId).Returns(AgentId.From(agentId));
        handle.SetupGet(x => x.SessionId).Returns(SessionId.From(sessionId));
        handle.Setup(x => x.IsRunning).Returns(false);
        handle.Setup(x => x.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "reply" });
        handle.Setup(x => x.PromptAsync(It.IsAny<AgentUserMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "reply" });

        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(
                AgentId.From(agentId), SessionId.From(sessionId), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        // No adapters registered at all, so every resolution misses and the guard decides the level.
        var channelManager = new Mock<IChannelManager>();
        channelManager.SetupGet(m => m.Adapters).Returns([]);
        channelManager.Setup(m => m.Get(It.IsAny<ChannelKey>())).Returns((IChannelAdapter?)null);
        channelManager.Setup(m => m.Get(It.IsAny<ChannelKey>(), It.IsAny<string?>())).Returns((IChannelAdapter?)null);

        var records = new List<LogRecord>();
        var host = new GatewayHost(
            supervisor.Object,
            router.Object,
            new InMemorySessionStore(),
            Mock.Of<IActivityBroadcaster>(),
            channelManager.Object,
            Mock.Of<ISessionCompactor>(),
            new TestOptionsMonitor<CompactionOptions>(new CompactionOptions()),
            new RecordingLogger(records));

        return (records, host);
    }

    private static InboundMessage CreateMessage(string channelType) => new()
    {
        ChannelType = channelType,
        SenderId = "sender-guard",
        Sender = CitizenId.Of(UserId.From("sender-guard")),
        ChannelAddress = ChannelAddress.From("conv-guard"),
        Content = "hello",
        RoutingHints = InboundMessageRoutingHints.LiftFromStrings(null, "session-guard", null),
        Metadata = new Dictionary<string, object?>()
    };

    private sealed record LogRecord(LogLevel Level, string Message);

    private sealed class RecordingLogger(List<LogRecord> records) : ILogger<GatewayHost>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        // Debug must be enabled or the guard's LogDebug call is elided and the test cannot
        // distinguish "skipped quietly" from "never reached".
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (records)
                records.Add(new LogRecord(logLevel, formatter(state, exception)));
        }
    }
}
