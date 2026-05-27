using BotNexus.Domain.AgentExchange;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Channels;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using System.Net;
using System.Net.Http.Json;
using GatewaySessionStatus = BotNexus.Gateway.Abstractions.Models.SessionStatus;

namespace BotNexus.Gateway.Tests.Agents;

/// <summary>
/// Pins issue <c>#553</c>: caller-initiated cancellation must NOT seal the session.
/// Before the fix, an <see cref="OperationCanceledException"/> raised inside the
/// per-turn write→prompt→reload window was caught by the catch-all that sealed the
/// session, set <c>conversationStatus = "error"</c>, recorded the OCE message in
/// <c>session.Metadata["error"]</c>, then rethrew. That made caller retries impossible
/// because <c>ResolveSessionAsync</c>'s sealed-session guard returns 409.
///
/// The fix inserts a preceding
/// <c>catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }</c>
/// at both <see cref="AgentExchangeService.ConverseAsync"/> call sites — the local
/// agent-agent path (around line 200) and the cross-world relay-out path (around line 360).
/// The <c>when</c> filter is essential: an OCE raised from an unrelated inner token
/// (e.g. a downstream HTTP-client timeout linked into the supervisor) MUST still fall
/// through to the catch-all and seal — those are genuine failures, not caller intent.
/// </summary>
public sealed class AgentExchangeServiceCancelNoSealTests
{
    [Fact]
    public async Task ConverseAsync_LocalPath_WhenCallerCancelsDuringPromptAsync_RethrowsOce_AndDoesNotSealSession()
    {
        var initiator = AgentId.From("test-agent");
        var target = AgentId.From("agent-c");
        var registry = CreateRegistry(initiator, target, ["agent-c"]);
        var sessionStore = new InMemorySessionStore();
        var conversationStore = new InMemoryConversationStore();

        using var cts = new CancellationTokenSource();

        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string _, CancellationToken ct) =>
            {
                // Caller-initiated cancellation: the same token the controller threaded all the
                // way down to PromptAsync is the one that fires. The new catch's `when` filter
                // checks `cancellationToken.IsCancellationRequested` (same token), so it MUST
                // rethrow without sealing.
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
                throw new InvalidOperationException("unreachable");
            });
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(target, It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var service = new AgentExchangeService(
            registry.Object,
            supervisor.Object,
            sessionStore,
            conversationStore,
            Options.Create(new GatewayOptions()),
            NullLogger<AgentExchangeService>.Instance);

        var act = async () => await service.ConverseAsync(
            new AgentExchangeRequest
            {
                InitiatorId = initiator,
                TargetId = target,
                Message = "hello",
                MaxTurns = 3
            },
            cts.Token);

        await act.ShouldThrowAsync<OperationCanceledException>();

        // Find the session created by the failed call — there should be exactly one.
        var sessions = await sessionStore.GetExistenceAsync(initiator, new ExistenceQuery());
        sessions.Count.ShouldBe(1, "ConverseAsync should have created exactly one session before the cancellation");
        var session = await sessionStore.GetAsync(sessions[0].SessionId);
        session.ShouldNotBeNull();

        // The core acceptance criterion of #553: cancellation does NOT seal.
        session!.Status.ShouldBe(GatewaySessionStatus.Active,
            "Caller-initiated cancellation must leave the session Active so the sender can retry. " +
            "Sealing here was the bug: the sealed-session 409 guard in ResolveSessionAsync would " +
            "permanently reject any retry attempt with the same SessionId.");
        session.Metadata.ContainsKey("error").ShouldBeFalse(
            "The error-metadata key is only written by the seal-on-error catch-all. If it's present, " +
            "the OCE rethrow path took the wrong branch.");
        session.Metadata.TryGetValue("conversationStatus", out var convStatus);
        (convStatus as string).ShouldNotBe("error",
            "conversationStatus='error' is set exclusively by the seal-on-error catch-all.");
    }

    /// <summary>
    /// Vacuity guard for the <c>when (cancellationToken.IsCancellationRequested)</c> filter.
    /// An OCE thrown by an UNRELATED token (e.g. a downstream timeout linked into the
    /// supervisor) must still fall through to the catch-all and seal — otherwise a real
    /// inner timeout would mask itself as caller cancellation and leak as a "session is
    /// Active" lie. If this test fails, the filter has been weakened to a bare
    /// <c>catch (OperationCanceledException)</c> and the discriminator is gone.
    /// </summary>
    [Fact]
    public async Task ConverseAsync_LocalPath_WhenInnerTokenCancels_NotCallerToken_StillSealsSession()
    {
        var initiator = AgentId.From("test-agent");
        var target = AgentId.From("agent-c");
        var registry = CreateRegistry(initiator, target, ["agent-c"]);
        var sessionStore = new InMemorySessionStore();
        var conversationStore = new InMemoryConversationStore();

        // Inner CTS unrelated to the caller's token; the handle will cancel + throw using it.
        using var innerCts = new CancellationTokenSource();
        using var callerCts = new CancellationTokenSource();

        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string _, CancellationToken _) =>
            {
                innerCts.Cancel();
                throw new OperationCanceledException(innerCts.Token);
            });
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(target, It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var service = new AgentExchangeService(
            registry.Object,
            supervisor.Object,
            sessionStore,
            conversationStore,
            Options.Create(new GatewayOptions()),
            NullLogger<AgentExchangeService>.Instance);

        var act = async () => await service.ConverseAsync(
            new AgentExchangeRequest
            {
                InitiatorId = initiator,
                TargetId = target,
                Message = "hello",
                MaxTurns = 3
            },
            callerCts.Token);

        await act.ShouldThrowAsync<OperationCanceledException>();

        var sessions = await sessionStore.GetExistenceAsync(initiator, new ExistenceQuery());
        sessions.Count.ShouldBe(1);
        var session = await sessionStore.GetAsync(sessions[0].SessionId);
        session.ShouldNotBeNull();

        // Filter must discriminate: callerCts was NEVER cancelled, so the OCE goes to the
        // catch-all and seals. This pins the `when` filter against accidental weakening.
        session!.Status.ShouldBe(GatewaySessionStatus.Sealed,
            "An OCE from a token unrelated to the caller's must still seal — that's a genuine " +
            "failure. The `when (cancellationToken.IsCancellationRequested)` filter is what " +
            "discriminates between the two. If this assertion fails, the filter has been " +
            "weakened to a bare `catch (OperationCanceledException)` and the discrimination is gone.");
        session.Metadata.ShouldContainKey("error");
    }

    [Fact]
    public async Task ConverseAsync_CrossWorldPath_WhenCallerCancelsDuringRelay_RethrowsOce_AndDoesNotSealSession()
    {
        var initiator = AgentId.From("test-agent");
        var target = AgentId.From("world-b:agent-c");
        var registry = CreateRegistry(initiator, AgentId.From("agent-c"), ["world-b:agent-c"]);
        registry.Setup(r => r.Contains(target)).Returns(false);
        var sessionStore = new InMemorySessionStore();
        var conversationStore = new InMemoryConversationStore();

        using var cts = new CancellationTokenSource();

        // Stub HTTP handler that on first request cancels the caller's token then throws OCE,
        // emulating a slow remote relay that the caller abandons mid-flight (sender HTTP
        // timeout, retry-policy abort, client disconnect).
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });
        var adapter = new CrossWorldChannelAdapter(
            NullLogger<CrossWorldChannelAdapter>.Instance,
            new HttpClient(handler));

        var service = new AgentExchangeService(
            registry.Object,
            Mock.Of<IAgentSupervisor>(),
            sessionStore,
            conversationStore,
            Options.Create(new GatewayOptions()),
            NullLogger<AgentExchangeService>.Instance,
            Options.Create(BuildCrossWorldPlatformConfig()),
            adapter);

        var act = async () => await service.ConverseAsync(
            new AgentExchangeRequest
            {
                InitiatorId = initiator,
                TargetId = target,
                Message = "hello",
                MaxTurns = 1
            },
            cts.Token);

        await act.ShouldThrowAsync<OperationCanceledException>();

        var sessions = await sessionStore.GetExistenceAsync(initiator, new ExistenceQuery());
        sessions.Count.ShouldBe(1);
        var session = await sessionStore.GetAsync(sessions[0].SessionId);
        session.ShouldNotBeNull();

        session!.Status.ShouldBe(GatewaySessionStatus.Active,
            "Cross-world caller cancellation must leave the session Active so the sender can " +
            "retry the relay. Sealing here would poison the session for the sender's retry policy.");
        session.Metadata.ContainsKey("error").ShouldBeFalse();
    }

    private static Mock<IAgentRegistry> CreateRegistry(AgentId initiator, AgentId target, IReadOnlyList<string> allowedTargets)
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(initiator)).Returns(new AgentDescriptor
        {
            AgentId = initiator,
            DisplayName = "Initiator",
            ModelId = "gpt-5-mini",
            ApiProvider = "copilot",
            SubAgentIds = allowedTargets
        });
        registry.Setup(r => r.Contains(target)).Returns(true);
        return registry;
    }

    private static PlatformConfig BuildCrossWorldPlatformConfig() => new()
    {
        Gateway = new GatewaySettingsConfig
        {
            World = new BotNexus.Domain.WorldIdentity { Id = "world-a", Name = "World A" },
            CrossWorldPermissions =
            [
                new CrossWorldPermissionConfig
                {
                    TargetWorldId = "world-b",
                    AllowOutbound = true,
                    AllowedAgents = ["test-agent"]
                }
            ],
            CrossWorld = new CrossWorldFederationConfig
            {
                Peers = new Dictionary<string, CrossWorldPeerConfig>
                {
                    ["world-b"] = new()
                    {
                        Endpoint = "https://gateway-b.internal",
                        ApiKey = "peer-key",
                        Enabled = true
                    }
                }
            }
        }
    };

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request, cancellationToken);
    }
}
