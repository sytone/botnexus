using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Sessions;
using BotNexus.Gateway.Streaming;
using Moq;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// The REST chat path must stamp provider usage onto the session, like every other blocking
/// boundary.
///
/// <para>
/// It did not, and the omission was invisible: the request succeeded, the reply was correct, and
/// the response body even carried a usage object. What was missing was durable — the compactor
/// read no <c>lastProviderPromptTokens</c> for a REST session and silently fell back to its
/// <c>chars/4</c> estimator, and the cache-efficiency counters stayed empty. It took measuring a
/// live gateway to notice.
/// </para>
/// </summary>
public sealed class ChatControllerUsageRecordingTests
{
    private static readonly AgentId Agent = AgentId.From("agent-a");

    [Fact]
    public async Task Send_StampsTheProviderPromptCountOnTheSession()
    {
        var saved = await SendAsync(new AgentResponseUsage(
            InputTokens: 3, OutputTokens: 4, CacheRead: 14_759, CacheWrite: 14));

        saved.ShouldNotBeNull();
        saved!.Metadata.ShouldContainKey(LlmSessionCompactor.ProviderPromptTokensMetadataKey);
        // The whole prompt the model saw, not just the uncached remainder: on a cache-aware
        // provider the cached components are most of it.
        Convert.ToInt32(saved.Metadata[LlmSessionCompactor.ProviderPromptTokensMetadataKey])
            .ShouldBe(14_776);
    }

    [Fact]
    public async Task Send_AccumulatesTheCacheEfficiencyCounters()
    {
        var saved = await SendAsync(new AgentResponseUsage(
            InputTokens: 3, OutputTokens: 4, CacheRead: 14_759, CacheWrite: 14));

        var (input, cacheRead, cacheWrite) = PromptCacheEfficiency.ReadTotals(saved!);
        input.ShouldBe(3);
        cacheRead.ShouldBe(14_759);
        cacheWrite.ShouldBe(14);
        PromptCacheEfficiency.SessionHitRatio(saved!)!.Value.ShouldBe(14_759d / 14_776d, 0.0001);
    }

    [Fact]
    public async Task Send_WithNoProviderUsage_LeavesTheSessionUnstamped()
    {
        // A provider that reports nothing must not be recorded as a zero-cost turn.
        var saved = await SendAsync(usage: null);

        saved!.Metadata.ShouldNotContainKey(LlmSessionCompactor.ProviderPromptTokensMetadataKey);
        PromptCacheEfficiency.SessionHitRatio(saved!).ShouldBeNull();
    }

    private static async Task<GatewaySession?> SendAsync(AgentResponseUsage? usage)
    {
        var session = new GatewaySession
        {
            SessionId = SessionId.From("session-1"),
            AgentId = Agent,
            Metadata = [],
        };

        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "ok", Usage = usage });

        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(Agent, It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        GatewaySession? saved = null;
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetOrCreateAsync(It.IsAny<SessionId>(), Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        sessions.Setup(s => s.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<CancellationToken>()))
            .Callback<GatewaySession, CancellationToken>((s, _) => saved = s)
            .Returns(Task.CompletedTask);

        var controller = new ChatController(supervisor.Object, sessions.Object);
        await controller.Send(new ChatRequest("agent-a", "hello", "session-1"), CancellationToken.None);

        return saved;
    }
}
