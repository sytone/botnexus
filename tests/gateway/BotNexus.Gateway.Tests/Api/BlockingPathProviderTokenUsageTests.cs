using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Abstractions.Triggers;
using BotNexus.Gateway.Api.Triggers;
using BotNexus.Gateway.Sessions;
using BotNexus.Gateway.Streaming;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests.Api;

/// <summary>
/// #2522 residual: the blocking <c>PromptAsync</c> path (cron / soul / heartbeat) never stamped the
/// provider's reported prompt-token count onto the session, so
/// <see cref="LlmSessionCompactor.ProviderPromptTokensMetadataKey"/> stayed absent for every
/// non-streamed turn. The compactor's unit normalisation (<c>ScalePreservedTurns</c>) then read a
/// null ratio and silently fell back to the unscaled keep-recent budget - i.e. the whole #2522 fix
/// was inert on exactly the long-lived cron sessions that compact most often.
///
/// The streaming half was already covered by <see cref="ProviderTokenUsageRecorder"/> at
/// <c>StreamingSessionHelper</c>'s <c>MessageEnd</c>. These tests pin the blocking half to the same
/// contract, including the sad paths where nothing must be written.
/// </summary>
public sealed class BlockingPathProviderTokenUsageTests
{
    private const string Key = LlmSessionCompactor.ProviderPromptTokensMetadataKey;

    // ---------- CronTrigger ----------

    [Fact]
    public async Task CronTrigger_WithProviderUsage_StampsProviderPromptTokensOnSession()
    {
        var saved = new List<GatewaySession>();
        var (sessionStore, conversationStore, supervisor) = BuildMocks(
            new AgentResponse
            {
                Content = "cron-response",
                Usage = new AgentResponseUsage(InputTokens: 1000, OutputTokens: 50, CacheRead: 200, CacheWrite: 300)
            },
            saved);

        var trigger = new CronTrigger(supervisor.Object, conversationStore.Object, sessionStore.Object, NullLogger<CronTrigger>.Instance);

        await trigger.CreateSessionAsync(
            AgentId.From("agent-cron"),
            "run",
            request: new InternalTriggerRequest { CronJobId = JobId.From("job-usage"), JobName = "Usage" });

        var session = saved.LastOrDefault(s => s.Metadata.ContainsKey(Key));
        session.ShouldNotBeNull("the cron blocking path must stamp the provider prompt-token count");
        // input + cacheRead + cacheWrite - the full prompt cost the model actually saw.
        Convert.ToInt32(session!.Metadata[Key]).ShouldBe(1500);
    }

    [Fact]
    public async Task CronTrigger_WithNoProviderUsage_LeavesMetadataAbsent()
    {
        var saved = new List<GatewaySession>();
        var (sessionStore, conversationStore, supervisor) = BuildMocks(
            new AgentResponse { Content = "cron-response", Usage = null },
            saved);

        var trigger = new CronTrigger(supervisor.Object, conversationStore.Object, sessionStore.Object, NullLogger<CronTrigger>.Instance);

        await trigger.CreateSessionAsync(
            AgentId.From("agent-cron"),
            "run",
            request: new InternalTriggerRequest { CronJobId = JobId.From("job-nousage"), JobName = "NoUsage" });

        // Absent, NOT zero: the compactor treats absence as "unavailable" and a fabricated 0 would
        // make the ratio computable and wrong.
        saved.ShouldNotBeEmpty();
        saved.ShouldAllBe(s => !s.Metadata.ContainsKey(Key));
    }

    [Fact]
    public async Task CronTrigger_WithNonPositiveUsage_DoesNotOverwriteAnExistingCount()
    {
        var saved = new List<GatewaySession>();
        var (sessionStore, conversationStore, supervisor) = BuildMocks(
            new AgentResponse { Content = "cron-response", Usage = new AgentResponseUsage(InputTokens: 0) },
            saved,
            seedExistingCount: 4242);

        var trigger = new CronTrigger(supervisor.Object, conversationStore.Object, sessionStore.Object, NullLogger<CronTrigger>.Instance);

        await trigger.CreateSessionAsync(
            AgentId.From("agent-cron"),
            "run",
            request: new InternalTriggerRequest { CronJobId = JobId.From("job-zero"), JobName = "Zero" });

        var session = saved.Last();
        Convert.ToInt32(session.Metadata[Key]).ShouldBe(4242);
    }

    // ---------- SoulTrigger ----------

    [Fact]
    public async Task SoulTrigger_WithProviderUsage_StampsProviderPromptTokensOnSession()
    {
        var agentId = AgentId.From("agent-soul");
        var now = new DateTimeOffset(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);
        var expectedSessionId = SessionId.ForSoul(agentId, new DateOnly(2026, 1, 10));

        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(agentId)).Returns(CreateDescriptor(agentId));

        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.ListAsync(agentId, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        sessions.Setup(s => s.GetOrCreateAsync(expectedSessionId, agentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GatewaySession { SessionId = expectedSessionId, AgentId = agentId });
        var saved = new List<GatewaySession>();
        sessions.Setup(s => s.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<CancellationToken>()))
            .Callback<GatewaySession, CancellationToken>((session, _) => saved.Add(session))
            .Returns(Task.CompletedTask);

        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync("hello", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse
            {
                Content = "response",
                Usage = new AgentResponseUsage(InputTokens: 900, CacheRead: 100)
            });

        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(agentId, expectedSessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var trigger = new SoulTrigger(
            supervisor.Object,
            registry.Object,
            sessions.Object,
            NullLogger<SoulTrigger>.Instance,
            new FixedTimeProvider(now));

        await trigger.CreateSessionAsync(agentId, "hello");

        var session = saved.LastOrDefault(s => s.Metadata.ContainsKey(Key));
        session.ShouldNotBeNull("the soul blocking path must stamp the provider prompt-token count");
        Convert.ToInt32(session!.Metadata[Key]).ShouldBe(1000);
    }

    // ---------- ProviderTokenUsageRecorder contract (shared by both halves) ----------

    [Fact]
    public void ResolvePromptTokens_SumsInputAndBothCacheCounters()
        => ProviderTokenUsageRecorder.ResolvePromptTokens(
            new AgentResponseUsage(InputTokens: 10, OutputTokens: 999, CacheRead: 5, CacheWrite: 3)).ShouldBe(18);

    [Fact]
    public void ResolvePromptTokens_NullUsage_ReturnsNull()
        => ProviderTokenUsageRecorder.ResolvePromptTokens(null).ShouldBeNull();

    [Fact]
    public void ResolvePromptTokens_AllZero_ReturnsNull()
        => ProviderTokenUsageRecorder.ResolvePromptTokens(new AgentResponseUsage(InputTokens: 0)).ShouldBeNull();

    // ---------- helpers ----------

    private static (Mock<ISessionStore>, Mock<IConversationStore>, Mock<IAgentSupervisor>) BuildMocks(
        AgentResponse response,
        List<GatewaySession> saved,
        int? seedExistingCount = null)
    {
        var sessionStore = new Mock<ISessionStore>();
        var conversationStore = new Mock<IConversationStore>();
        var supervisor = new Mock<IAgentSupervisor>();
        var handle = new Mock<IAgentHandle>();

        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        sessionStore
            .Setup(s => s.GetOrCreateAsync(It.IsAny<SessionId>(), It.IsAny<AgentId>(), It.IsAny<CancellationToken>()))
            .Returns<SessionId, AgentId, CancellationToken>((sid, aid, _) =>
            {
                var session = new GatewaySession { SessionId = sid, AgentId = aid };
                if (seedExistingCount.HasValue)
                    session.Metadata[Key] = seedExistingCount.Value;
                return Task.FromResult(session);
            });

        sessionStore
            .Setup(s => s.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<CancellationToken>()))
            .Callback<GatewaySession, CancellationToken>((session, _) => saved.Add(session))
            .Returns(Task.CompletedTask);

        conversationStore
            .Setup(s => s.CreateAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()))
            .Returns<Conversation, CancellationToken>((c, _) => Task.FromResult(c));
        conversationStore
            .Setup(s => s.SaveAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        supervisor
            .Setup(s => s.GetOrCreateAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        return (sessionStore, conversationStore, supervisor);
    }

    private static AgentDescriptor CreateDescriptor(AgentId agentId)
        => new()
        {
            AgentId = agentId,
            DisplayName = "Agent Soul",
            ModelId = "model-a",
            ApiProvider = "provider-a",
            Soul = new SoulAgentConfig()
        };

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
