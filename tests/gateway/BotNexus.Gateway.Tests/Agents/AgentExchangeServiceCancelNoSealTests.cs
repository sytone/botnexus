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
    /// An OCE thrown by an inner token that the caller did NOT cancel must still fall through and
    /// seal - otherwise a real inner timeout masks itself as caller cancellation and leaks as a
    /// "session is Active" lie. If this test fails, the filter has been weakened to a bare
    /// <c>catch (OperationCanceledException)</c> and the discriminator is gone.
    /// </summary>
    /// <remarks>
    /// #3515 AC6: the inner source is LINKED to <c>callerCts</c>, reproducing the production
    /// parent/child relationship. Every deadline above this frame is armed on a token linked from
    /// the caller's token, so an unlinked <c>new CancellationTokenSource()</c> - what this test used
    /// before - exercised two unrelated sources and pinned nothing about token identity. Linking it
    /// is strictly harder: the child's cancellation must still leave <c>callerCts</c> unsignalled
    /// (cancellation flows parent to child only), which is exactly the property the filter relies on.
    /// </remarks>
    [Fact]
    public async Task ConverseAsync_LocalPath_WhenInnerTokenCancels_NotCallerToken_StillSealsSession()
    {
        var initiator = AgentId.From("test-agent");
        var target = AgentId.From("agent-c");
        var registry = CreateRegistry(initiator, target, ["agent-c"]);
        var sessionStore = new InMemorySessionStore();
        var conversationStore = new InMemoryConversationStore();

        using var callerCts = new CancellationTokenSource();
        // #3515 AC6: a linked CHILD of the caller's token - the production shape - not an unrelated
        // source. Cancelling it must NOT signal callerCts, so the filter still discriminates.
        using var innerCts = CancellationTokenSource.CreateLinkedTokenSource(callerCts.Token);

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

        // Filter must discriminate: callerCts was NEVER cancelled (the inner source is its child,
        // and cancellation does not flow child->parent), so the OCE goes to the catch-all and seals.
        // This pins the `when` filter against accidental weakening.
        session!.Status.ShouldBe(GatewaySessionStatus.Sealed,
            "An OCE from a token the caller did not cancel must still seal - that's a genuine " +
            "failure. The `when (cancellationToken.IsCancellationRequested)` filter is what " +
            "discriminates between the two. If this assertion fails, the filter has been " +
            "weakened to a bare `catch (OperationCanceledException)` and the discrimination is gone.");
        session.Metadata.ShouldContainKey("error");
        callerCts.IsCancellationRequested.ShouldBeFalse(
            "#3515 AC6 non-vacuity: the caller's token must remain unsignalled for this case to be " +
            "testing what it claims. If cancelling the linked child ever signalled the parent, the " +
            "assertion above would be pinning the caller-cancel path by accident.");
    }

    /// <summary>
    /// #3515 AC2: a cancellation arriving between <c>onSealSuccess</c> and the seal persist must not
    /// lose the seal. The announcement has already gone out, so the stored row has to agree with it.
    /// </summary>
    /// <remarks>
    /// Pre-fix the terminal <c>SaveAsync</c> was the one write on the success path still passed the
    /// cancellable token, so a cancellation landing in that gap threw a raw
    /// <see cref="OperationCanceledException"/> which the caller-cancellation arm then swallowed and
    /// rethrew WITHOUT sealing - after the seal had been announced. Announced state and persisted
    /// state diverged silently. Reverting the token change turns this red.
    /// </remarks>
    [Fact]
    public async Task ConverseAsync_WhenCallerCancelsAfterFinalTurn_StillPersistsTheAnnouncedSeal()
    {
        var initiator = AgentId.From("test-agent");
        var target = AgentId.From("agent-c");
        var registry = CreateRegistry(initiator, target, ["agent-c"]);
        var sessionStore = new InMemorySessionStore();
        var conversationStore = new InMemoryConversationStore();

        using var callerCts = new CancellationTokenSource();

        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string _, CancellationToken _) =>
            {
                // The turn itself SUCCEEDS. The caller then cancels, so the very next cancellable
                // operation is the terminal seal persist - precisely the :152->:153 gap.
                callerCts.Cancel();
                return Task.FromResult(new AgentResponse { Content = "done" });
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

        // No Objective => single-shot: exactly one turn, then straight to the seal block.
        var result = await service.ConverseAsync(
            new AgentExchangeRequest
            {
                InitiatorId = initiator,
                TargetId = target,
                Message = "hello",
                MaxTurns = 1
            },
            callerCts.Token);

        result.Status.ShouldBe("sealed",
            "the exchange completed its only turn, so it must report a sealed outcome");

        var sessions = await sessionStore.GetExistenceAsync(initiator, new ExistenceQuery());
        sessions.Count.ShouldBe(1);
        var session = await sessionStore.GetAsync(sessions[0].SessionId);
        session.ShouldNotBeNull();

        session!.Status.ShouldBe(GatewaySessionStatus.Sealed,
            "#3515 AC2: the seal was already announced to observers before the persist. Passing the " +
            "caller's cancellable token to that terminal SaveAsync let a late cancellation abort the " +
            "write, leaving the stored row Active while callers had been told it sealed. The " +
            "terminal write must use CancellationToken.None like every other seal-site write.");
        callerCts.IsCancellationRequested.ShouldBeTrue(
            "non-vacuity: the caller token must actually have been cancelled during the exchange, " +
            "otherwise this test proves nothing about the cancellation race it claims to cover.");
    }

    /// <summary>
    /// #3515 AC1: a deadline expiring while the engine is inside one of the store/supervisor calls
    /// (which take the cancellable token directly and throw raw
    /// <see cref="OperationCanceledException"/>) must leave the session <c>Sealed</c>, not
    /// <c>Active</c>.
    /// </summary>
    /// <remarks>
    /// The ambient token handed to <c>ConverseAsync</c> here is a linked CHILD that a timer cancels -
    /// the production shape produced by <c>AgentConverseTool</c>'s own budget. Pre-fix that made
    /// <c>cancellationToken.IsCancellationRequested</c> true on timeout exactly as it is on caller
    /// cancel, so the caller-cancellation arm claimed the timeout and skipped the seal. With the fix
    /// the engine arms its OWN unlinked deadline from <c>AgentExchangeRequest.Deadline</c>, which
    /// fires first, so the timeout is attributable and seals.
    /// </remarks>
    [Fact]
    public async Task ConverseAsync_WhenEngineDeadlineExpires_AndCallerDidNotCancel_SealsSession()
    {
        var initiator = AgentId.From("test-agent");
        var target = AgentId.From("agent-c");
        var registry = CreateRegistry(initiator, target, ["agent-c"]);
        var sessionStore = new InMemorySessionStore();
        var conversationStore = new InMemoryConversationStore();

        using var callerCts = new CancellationTokenSource();
        // The ambient token the tool threads down: linked to the caller AND timer-armed. Its timer is
        // deliberately far later than the engine deadline so the engine's own source wins the race,
        // mirroring the production backstop buffer.
        using var ambientCts = CancellationTokenSource.CreateLinkedTokenSource(callerCts.Token);
        ambientCts.CancelAfter(TimeSpan.FromSeconds(30));

        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, CancellationToken ct) =>
            {
                // Block until something cancels, then throw RAW OperationCanceledException - the
                // shape the store and supervisor calls produce when they take the token directly.
                var idle = new TaskCompletionSource();
                await using (ct.Register(() => idle.TrySetResult()))
                {
                    await idle.Task.ConfigureAwait(false);
                }
                ct.ThrowIfCancellationRequested();
                return new AgentResponse { Content = "unreachable" };
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
                MaxTurns = 3,
                // Already elapsed: the engine's deadline source arms immediately.
                Deadline = DateTimeOffset.UtcNow
            },
            ambientCts.Token);

        await act.ShouldThrowAsync<OperationCanceledException>();

        var sessions = await sessionStore.GetExistenceAsync(initiator, new ExistenceQuery());
        sessions.Count.ShouldBe(1);
        var session = await sessionStore.GetAsync(sessions[0].SessionId);
        session.ShouldNotBeNull();

        session!.Status.ShouldBe(GatewaySessionStatus.Sealed,
            "#3515 AC1: a deadline expiry is a terminal outcome nobody is waiting to retry, so it " +
            "must seal. Before the fix the only evidence available was " +
            "`cancellationToken.IsCancellationRequested`, which a linked deadline sets just as a " +
            "human pressing stop does - so the timeout took the caller-cancellation arm and the " +
            "session was left Active.");
        session.Metadata.ShouldContainKey("error");
        callerCts.IsCancellationRequested.ShouldBeFalse(
            "non-vacuity: no caller cancellation occurred, so the seal above is attributable to the " +
            "deadline arm and not to the generic failure arm reached via a cancelled caller.");
    }

    /// <summary>
    /// #3515 AC4: when the caller token and the deadline are BOTH cancelled, the outcome is
    /// classified as caller cancellation - the ambiguous case resolves to "cancel", never "timeout".
    /// </summary>
    /// <remarks>
    /// This pins the ARM ORDER, which is the safety property. Cancelling a session that had timed out
    /// anyway is recoverable; sealing a session the user is still holding is not, because the
    /// sealed-session 409 guard then rejects every retry. If the deadline arm is ever moved above the
    /// caller arm this test goes red.
    /// </remarks>
    [Fact]
    public async Task ConverseAsync_WhenCallerCancelsAndDeadlineElapsed_ClassifiesAsCallerCancellation()
    {
        var initiator = AgentId.From("test-agent");
        var target = AgentId.From("agent-c");
        var registry = CreateRegistry(initiator, target, ["agent-c"]);
        var sessionStore = new InMemorySessionStore();
        var conversationStore = new InMemoryConversationStore();

        using var callerCts = new CancellationTokenSource();

        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string _, CancellationToken ct) =>
            {
                // Both causes are live at once: the deadline below is already elapsed AND the caller
                // cancels here. The caller must win.
                callerCts.Cancel();
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(new AgentResponse { Content = "unreachable" });
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
                MaxTurns = 3,
                Deadline = DateTimeOffset.UtcNow.AddSeconds(-1)
            },
            callerCts.Token);

        await act.ShouldThrowAsync<OperationCanceledException>();

        var sessions = await sessionStore.GetExistenceAsync(initiator, new ExistenceQuery());
        sessions.Count.ShouldBe(1);
        var session = await sessionStore.GetAsync(sessions[0].SessionId);
        session.ShouldNotBeNull();

        session!.Status.ShouldBe(GatewaySessionStatus.Active,
            "#3515 AC4 / #553 parity: with both the caller token and the deadline cancelled, the " +
            "caller-cancellation arm must win because it is declared FIRST. Sealing here would " +
            "poison the session against the retry the caller is entitled to make.");
        session.Metadata.ContainsKey("error").ShouldBeFalse(
            "the error key is written only by the sealing arms; its presence would mean the " +
            "deadline arm claimed an exchange the caller cancelled.");
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
