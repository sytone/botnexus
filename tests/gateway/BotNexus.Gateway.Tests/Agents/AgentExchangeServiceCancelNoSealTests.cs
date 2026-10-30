using Microsoft.Extensions.DependencyInjection;
using BotNexus.Gateway.Extensions;
using BotNexus.Gateway.Audit;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConverseAsync_InterruptedPrompt_PersistsPartialAuditAndPreservesCancellationCause(bool engineDeadline)
    {
        var initiator = AgentId.From("test-agent");
        var target = AgentId.From("agent-c");
        var registry = CreateRegistry(initiator, target, ["agent-c"]);
        var directory = Path.Combine(Path.GetTempPath(), "exchange-5035", Guid.NewGuid().ToString("N"));
        var conversationStore = new InMemoryConversationStore();
        var sessionStore = new FileSessionStore(directory, NullLogger<FileSessionStore>.Instance,
            new System.IO.Abstractions.FileSystem(), conversationStore);
        using var cleanup = new DirectoryCleanup(directory);
        using var caller = new CancellationTokenSource();
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var runId = AgentRunId.From("run-4101");
        var partial = new AgentResponse
        {
            AgentRunId = runId,
            Content = "not a final answer " + "ghp_" + new string('a', 36) + " configured-secret "
                + string.Concat(Enumerable.Repeat("界😀", 20000)),
            Usage = engineDeadline ? new AgentResponseUsage(InputTokens: 17, OutputTokens: 9) : null,
            RunUsage = engineDeadline ? new AgentResponseUsage(InputTokens: 31, OutputTokens: 12) : null,
            TerminalError = "raw-provider-diagnostic-do-not-persist",
            Completion = new RunCompletionSignal("Cancelled", [], null, "private-thinking-do-not-persist",
                "private-thinking-do-not-persist", null, null, 0),
            ToolCalls = [
                new("completed-4101", "write", false, "{\"path\":\"unique-5035.txt\"}", "mutation receipt unique-5035") { AgentRunId = runId },
                new("interrupted-4101", "read", true, "{}", null, IsIncomplete: true) { AgentRunId = runId }]
        };
        var mutationCount = 0;
        var mutationPath = Path.Combine(directory, "unique-5035.txt");
        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, CancellationToken ct) =>
            {
                await File.WriteAllTextAsync(mutationPath, "mutation receipt unique-5035", ct);
                mutationCount++;
                // Expire the engine-owned timer only after setup and prompt admission.
                if (engineDeadline) clock.Advance(TimeSpan.FromMinutes(1));
                else caller.Cancel();
                var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = ct.Register(() => cancelled.TrySetResult());
                await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
                throw new AgentPromptInterruptedException(partial, ct);
            });
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(target, It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        var engine = new AgentExchangeTurnEngine(sessionStore, conversationStore,
            NullLogger.Instance, null, clock);
        // Exercise both production registrations and real operator configuration, without
        // resolving hosted services, constructing a host, or loading user configuration.
        var services = new ServiceCollection();
        services.AddSingleton<IOptions<PlatformConfig>>(Options.Create(new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                SecretRedaction = new SecretRedactionConfig { Patterns = ["configured-secret"] }
            }
        }));
        services.AddBotNexusGateway();
        using var provider = services.BuildServiceProvider();
        var sink = provider.GetRequiredService<IToolAuditSink>();
        var service = new AgentExchangeService(registry.Object, supervisor.Object, sessionStore,
            conversationStore, Options.Create(new GatewayOptions()), NullLogger<AgentExchangeService>.Instance,
            turnEngine: engine, toolAudit: sink);

        // Shouldly 4.3 ThrowAsync substitutes TaskCanceledException for every canceled task,
        // losing the engine-owned exception type. Await directly to preserve the actual cause.
        OperationCanceledException? cancellation = null;
        try
        {
            await service.ConverseAsync(new AgentExchangeRequest
            {
                InitiatorId = initiator, TargetId = target, Message = "hello", MaxTurns = 3,
                Deadline = engineDeadline ? clock.GetUtcNow().AddMinutes(1) : null
            }, caller.Token);
        }
        catch (OperationCanceledException ex)
        {
            cancellation = ex;
        }
        var exception = cancellation.ShouldNotBeNull();

        if (engineDeadline) exception.ShouldBeOfType<AgentExchangeDeadlineExceededException>();
        else exception.CancellationToken.ShouldBe(caller.Token);
        caller.IsCancellationRequested.ShouldBe(!engineDeadline);
        var summaries = await sessionStore.GetExistenceAsync(initiator, new ExistenceQuery());
        // A fresh store instance must recover the evidence from disk, not an in-memory reference.
        var reopened = new FileSessionStore(directory, NullLogger<FileSessionStore>.Instance,
            new System.IO.Abstractions.FileSystem(), conversationStore);
        var session = (await reopened.GetAsync(summaries.ShouldHaveSingleItem().SessionId)).ShouldNotBeNull();
        session.Status.ShouldBe(engineDeadline ? GatewaySessionStatus.Sealed : GatewaySessionStatus.Active);
        session.Metadata.ContainsKey("error").ShouldBe(engineDeadline);
        if (!engineDeadline) session.Metadata["exchangeOutcome"]?.ToString().ShouldBe("callerCancelled");
        var history = session.GetHistorySnapshot();
        history.Where(row => row.Role == MessageRole.User).ShouldHaveSingleItem().Content.ShouldBe("hello");
        history.ShouldNotContain(row => row.Role == MessageRole.Assistant);
        var tools = history.Where(row => row.Role == MessageRole.Tool).ToArray();
        tools.Select(row => row.ToolCallId).ShouldBe(new[] { "completed-4101", "interrupted-4101" });
        tools.ShouldAllBe(row => row.AgentRunId == runId);
        tools[0].Content.ShouldContain("mutation receipt unique-5035");
        tools[0].ToolArgs.ShouldBe("{\"path\":\"unique-5035.txt\"}");
        tools[0].ToolIsIncomplete.ShouldBeFalse();
        tools[1].ToolIsIncomplete.ShouldBeTrue();
        tools.Count(row => row.ToolIsIncomplete == true).ShouldBe(1);
        var conversation = (await conversationStore.GetAsync(session.ConversationId)).ShouldNotBeNull();
        conversation.Status.ShouldBe(engineDeadline ? ConversationStatus.Archived : ConversationStatus.Active);
        session.Metadata.ShouldContainKey("interruptedExchangeEvidence");
        var storedMetadata = System.Text.Json.JsonSerializer.Serialize(session.Metadata);
        storedMetadata.ShouldNotContain("raw-provider-diagnostic-do-not-persist");
        storedMetadata.ShouldNotContain("private-thinking-do-not-persist");
        var evidence = System.Text.Json.JsonSerializer.SerializeToElement(session.Metadata["interruptedExchangeEvidence"]);
        evidence.GetProperty("isIncomplete").GetBoolean().ShouldBeTrue();
        evidence.GetProperty("partialText").GetString().ShouldNotBeNull().ShouldContain("not a final answer");
        evidence.GetProperty("partialText").GetString().ShouldNotBeNull().ShouldNotContain("ghp_");
        evidence.GetProperty("partialText").GetString().ShouldNotBeNull().ShouldNotContain("configured-secret");
        var retainedText = evidence.GetProperty("partialText").GetString().ShouldNotBeNull();
        System.Text.Encoding.UTF8.GetByteCount(retainedText).ShouldBeLessThanOrEqualTo(ToolInvocationRecordPolicy.DefaultMaxBytes + 64);
        retainedText.ShouldContain("[truncated");
        new System.Text.UTF8Encoding(false, true).GetBytes(retainedText).Length.ShouldBeGreaterThan(0);
        mutationCount.ShouldBe(1);
        (await File.ReadAllTextAsync(mutationPath)).ShouldBe("mutation receipt unique-5035");
        evidence.GetProperty("completedToolCount").GetInt32().ShouldBe(1);
        evidence.GetProperty("incompleteToolCount").GetInt32().ShouldBe(1);
        if (engineDeadline)
        {
            evidence.GetProperty("usage").GetProperty("inputTokens").GetInt32().ShouldBe(17);
            evidence.GetProperty("runUsage").GetProperty("inputTokens").GetInt32().ShouldBe(31);
        }
        else
        {
            evidence.GetProperty("usage").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);
            evidence.GetProperty("runUsage").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);
        }
        var receiptProperty = exception.GetType().GetProperty("Evidence").ShouldNotBeNull();
        var receipt = System.Text.Json.JsonSerializer.SerializeToElement(receiptProperty.GetValue(exception));
        receipt.GetProperty("SessionId").GetString().ShouldBe(session.SessionId.Value);
        receipt.GetProperty("ConversationId").GetString().ShouldBe(session.ConversationId.Value);
        receipt.GetProperty("Phase").GetString().ShouldBe("promptInterrupted");

        using var toolCaller = new CancellationTokenSource();
        var exchange = new Mock<IAgentExchangeService>();
        exchange.Setup(s => s.ConverseAsync(It.IsAny<AgentExchangeRequest>(), It.IsAny<CancellationToken>()))
            .Returns((AgentExchangeRequest _, CancellationToken _) =>
            {
                if (!engineDeadline) toolCaller.Cancel();
                throw exception;
            });
        var tool = new BotNexus.Gateway.Tools.AgentConverseTool(exchange.Object, reopened, initiator, session.SessionId);
        var toolResult = await tool.ExecuteAsync("receipt-5035", new Dictionary<string, object?>
        {
            ["agentId"] = target.Value, ["message"] = "hello"
        }, toolCaller.Token);
        var payload = System.Text.Json.JsonDocument.Parse(toolResult.Content[0].Value);
        using (payload)
        {
            var report = payload.RootElement;
            report.GetProperty("cancelled").GetBoolean().ShouldBeTrue();
            report.GetProperty("cancellationCause").GetString().ShouldBe(engineDeadline ? "timeout" : "callerAborted");
            report.GetProperty("retryAdvised").GetBoolean().ShouldBeFalse();
            report.GetProperty("evidence").GetProperty("sessionId").GetString().ShouldBe(session.SessionId.Value);
            report.GetProperty("evidence").GetProperty("completedToolCount").GetInt32().ShouldBe(1);
            report.GetProperty("message").GetString().ShouldNotBeNull().ShouldContain("reconcile");
            payload.RootElement.GetRawText().ShouldNotContain("ghp_");
            report.GetProperty("evidence").GetProperty("partialText").GetString().ShouldBe(retainedText);
            report.GetProperty("evidence").GetProperty("partialText").GetString().ShouldNotBeNull().ShouldNotContain("configured-secret");
            payload.RootElement.GetRawText().ShouldNotContain("AgentPromptInterruptedException");
            payload.RootElement.GetRawText().ShouldNotContain("raw-provider-diagnostic-do-not-persist");
            payload.RootElement.GetRawText().ShouldNotContain("private-thinking-do-not-persist");
        }
        supervisor.Verify(s => s.GetOrCreateAsync(target, It.IsAny<SessionId>(), It.IsAny<CancellationToken>()), Times.Once);
        handle.Verify(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ConverseAsync_CancelledBeforeHandleAdmission_ReportsNeverDispatched()
    {
        var initiator = AgentId.From("test-agent");
        var target = AgentId.From("agent-c");
        var sessions = new InMemorySessionStore();
        var conversations = new InMemoryConversationStore();
        using var caller = new CancellationTokenSource();
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(target, It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .Returns((AgentId _, SessionId _, CancellationToken token) =>
            {
                caller.Cancel();
                throw new OperationCanceledException(token);
            });
        var service = new AgentExchangeService(CreateRegistry(initiator, target, [target.Value]).Object,
            supervisor.Object, sessions, conversations, Options.Create(new GatewayOptions()),
            NullLogger<AgentExchangeService>.Instance);
        OperationCanceledException? cancellation = null;
        try
        {
            await service.ConverseAsync(new AgentExchangeRequest
            {
                InitiatorId = initiator, TargetId = target, Message = "never sent", MaxTurns = 1
            }, caller.Token);
        }
        catch (OperationCanceledException ex) { cancellation = ex; }
        var exception = cancellation.ShouldNotBeNull();
        var property = exception.GetType().GetProperty("Evidence").ShouldNotBeNull();
        var evidence = System.Text.Json.JsonSerializer.SerializeToElement(property.GetValue(exception));
        evidence.GetProperty("Phase").GetString().ShouldBe("neverDispatched");
        evidence.GetProperty("CompletedToolCount").GetInt32().ShouldBe(0);
        var summary = (await sessions.GetExistenceAsync(initiator, new ExistenceQuery())).ShouldHaveSingleItem();
        var session = (await sessions.GetAsync(summary.SessionId)).ShouldNotBeNull();
        session.Status.ShouldBe(GatewaySessionStatus.Active);
        session.GetHistorySnapshot().ShouldNotContain(row => row.Role == MessageRole.Assistant || row.Role == MessageRole.Tool);
    }

    [Theory]
    [InlineData("partial")]
    [InlineData("plain")]
    public async Task ConverseAsync_LaterInterruption_CountsRetainedResultsAcrossExchange(string interruption)
    {
        var initiator = AgentId.From("test-agent");
        var target = AgentId.From("agent-c");
        var sessions = new InMemorySessionStore();
        var conversations = new InMemoryConversationStore();
        using var caller = new CancellationTokenSource();
        var run = AgentRunId.From("prior-mutation-run");
        var directory = Path.Combine(Path.GetTempPath(), "exchange-5035-prior", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var cleanup = new DirectoryCleanup(directory);
        var mutationPath = Path.Combine(directory, "prior-mutation.txt");
        var prompts = 0;
        var mutations = 0;
        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, CancellationToken ct) =>
            {
                prompts++;
                if (prompts == 1)
                {
                    await File.WriteAllTextAsync(mutationPath, "mutation receipt", ct);
                    mutations++;
                    return new AgentResponse
                    {
                        Content = "continue", AgentRunId = run,
                        ToolCalls = [new("prior-write", "write", false, "{}", "mutation receipt") { AgentRunId = run }]
                    };
                }
                // Simulate retained write-ahead start/result rows. The result duplicates the
                // first turn's projection; starts must never inflate the completed count.
                var summary = (await sessions.GetExistenceAsync(initiator, new ExistenceQuery())).ShouldHaveSingleItem();
                var session = (await sessions.GetAsync(summary.SessionId)).ShouldNotBeNull();
                session.AddEntry(DefaultToolAuditSink.Instance.ProjectStart("prior-write", "write", "{}") with { AgentRunId = run });
                session.AddEntry(DefaultToolAuditSink.Instance.ProjectResult("prior-write", "write", "mutation receipt", false, 0) with { AgentRunId = run });
                caller.Cancel();
                if (interruption == "partial")
                    throw new AgentPromptInterruptedException(new AgentResponse { Content = "unfinished" }, ct);
                throw new OperationCanceledException(ct);
            });
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(target, It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        var service = new AgentExchangeService(CreateRegistry(initiator, target, [target.Value]).Object,
            supervisor.Object, sessions, conversations, Options.Create(new GatewayOptions()),
            NullLogger<AgentExchangeService>.Instance);
        OperationCanceledException? cancellation = null;
        try
        {
            await service.ConverseAsync(new AgentExchangeRequest
            {
                InitiatorId = initiator, TargetId = target, Message = "mutate once",
                Objective = "continue until finished", MaxTurns = 3
            }, caller.Token);
        }
        catch (OperationCanceledException ex) { cancellation = ex; }
        var exception = cancellation.ShouldNotBeNull();
        var evidence = ((exception as AgentExchangeCancelledException)?.Evidence
            ?? exception.Data[nameof(AgentExchangeCancellationEvidence)] as AgentExchangeCancellationEvidence).ShouldNotBeNull();
        evidence.Phase.ShouldBe("promptInterrupted");
        evidence.CompletedToolCount.ShouldBe(1);
        evidence.IncompleteToolCount.ShouldBe(0);
        mutations.ShouldBe(1);
        (await File.ReadAllTextAsync(mutationPath)).ShouldBe("mutation receipt");
        prompts.ShouldBe(2);
        var retained = (await sessions.GetAsync(evidence.SessionId)).ShouldNotBeNull();
        retained.Status.ShouldBe(GatewaySessionStatus.Active);
        retained.GetHistorySnapshot().Where(row => row.Role == MessageRole.Assistant).ShouldHaveSingleItem().Content.ShouldBe("continue");
        retained.GetHistorySnapshot().ShouldContain(row => row.Kind == MessageKind.ToolResult && row.Content == "mutation receipt");
    }

    [Theory]
    [InlineData("plain", "caller")]
    [InlineData("plain", "deadline")]
    [InlineData("plain", "inner")]
    [InlineData("partial", "caller")]
    [InlineData("partial", "deadline")]
    [InlineData("partial", "inner")]
    [InlineData("admission", "caller")]
    [InlineData("admission", "deadline")]
    [InlineData("admission", "inner")]
    public async Task ConverseAsync_SqliteIndependentAudit_CountsDurableAndPendingRows(string interruption, string cause)
    {
        var initiator = AgentId.From("test-agent");
        var target = AgentId.From("agent-c");
        var directory = Path.Combine(Path.GetTempPath(), "exchange-5035-sqlite", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var cleanup = new DirectoryCleanup(directory);
        var connectionString = $"Data Source={Path.Combine(directory, "sessions.db")};Pooling=False";
        var conversations = new InMemoryConversationStore();
        var sessions = new SqliteSessionStore(connectionString, NullLogger<SqliteSessionStore>.Instance, conversations);
        using var caller = new CancellationTokenSource();
        using var inner = new CancellationTokenSource();
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var priorRun = AgentRunId.From("prior-run");
        var currentRun = AgentRunId.From("current-run");
        var prompts = 0;
        SessionId? childId = null;
        async Task InterruptAsync(SessionId id, CancellationToken token)
        {
            // A separate aggregate, as used by ToolAuditWriteAhead.RecordInterruptedAsync.
            var independent = (await sessions.GetAsync(id)).ShouldNotBeNull();
            independent.Metadata["independent-metadata"] = "must-survive";
            independent.Metadata["maxTurns"] = 7;
            independent.Metadata.Remove("objective");
            independent.AddEntry(DefaultToolAuditSink.Instance.ProjectIncomplete("in-flight", "read", "{}") with
            {
                AgentRunId = currentRun,
                PersistenceKey = DefaultToolAuditSink.GetInterruptedToolPersistenceKey("in-flight", currentRun)
            });
            independent.AddEntry(DefaultToolAuditSink.Instance.ProjectResult("durable-write", "write", "durable receipt", false, 0) with
            {
                AgentRunId = currentRun
            });
            await sessions.SaveAsync(independent);
            if (cause == "caller") caller.Cancel();
            else if (cause == "deadline") clock.Advance(TimeSpan.FromMinutes(1));
            else inner.Cancel();
            var cancelledToken = cause == "inner" ? inner.Token : token;
            if (interruption == "partial")
                throw new AgentPromptInterruptedException(new AgentResponse
                {
                    Content = "unfinished", AgentRunId = currentRun,
                    ToolCalls = [
                        new("durable-write", "write", false, "{}", "durable receipt") { AgentRunId = currentRun },
                        new("pending-write", "write", false, "{}", "pending receipt") { AgentRunId = currentRun }]
                }, cancelledToken);
            throw new OperationCanceledException("original cancellation", cancelledToken);
        }
        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, CancellationToken token) =>
            {
                prompts++;
                if (prompts == 1)
                    return new AgentResponse
                    {
                        Content = "continue", AgentRunId = priorRun,
                        ToolCalls = [new("prior-write", "write", false, "{}", "prior receipt") { AgentRunId = priorRun }]
                    };
                await InterruptAsync(childId.ShouldNotBeNull(), token);
                throw new InvalidOperationException("unreachable");
            });
        var supervisor = new Mock<IAgentSupervisor>();
        var admissions = 0;
        supervisor.Setup(s => s.GetOrCreateAsync(target, It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .Returns(async (AgentId _, SessionId id, CancellationToken token) =>
            {
                childId = id;
                admissions++;
                if (interruption == "admission") await InterruptAsync(id, token);
                return handle.Object;
            });
        var engine = new AgentExchangeTurnEngine(sessions, conversations, NullLogger.Instance, null, clock);
        var service = new AgentExchangeService(CreateRegistry(initiator, target, [target.Value]).Object,
            supervisor.Object, sessions, conversations, Options.Create(new GatewayOptions()),
            NullLogger<AgentExchangeService>.Instance, turnEngine: engine);
        OperationCanceledException? cancellation = null;
        try
        {
            await service.ConverseAsync(new AgentExchangeRequest
            {
                InitiatorId = initiator, TargetId = target, Message = "preserve local user",
                Objective = "continue until finished", MaxTurns = 3,
                Deadline = cause == "deadline" ? clock.GetUtcNow().AddMinutes(1) : null
            }, caller.Token);
        }
        catch (OperationCanceledException ex) { cancellation = ex; }
        var exception = cancellation.ShouldNotBeNull();
        if (interruption == "plain" && cause != "deadline") exception.Message.ShouldBe("original cancellation");
        if (cause == "deadline") exception.ShouldBeOfType<AgentExchangeDeadlineExceededException>();
        else if (cause == "inner") exception.CancellationToken.ShouldBe(inner.Token);
        else exception.CancellationToken.ShouldBe(caller.Token);
        var evidence = ((exception as AgentExchangeCancelledException)?.Evidence
            ?? (exception as AgentExchangeDeadlineExceededException)?.Evidence
            ?? exception.Data[nameof(AgentExchangeCancellationEvidence)] as AgentExchangeCancellationEvidence).ShouldNotBeNull();
        evidence.IncompleteToolCount.ShouldBe(1, "the independently persisted incomplete result must be counted");
        evidence.CompletedToolCount.ShouldBe(interruption == "admission" ? 1 : interruption == "partial" ? 3 : 2);
        evidence.Phase.ShouldBe(interruption == "admission" ? "neverDispatched" : "promptInterrupted");
        var reopened = new SqliteSessionStore(connectionString, NullLogger<SqliteSessionStore>.Instance, conversations);
        var retained = (await reopened.GetAsync(evidence.SessionId)).ShouldNotBeNull();
        retained.Metadata["independent-metadata"]?.ToString().ShouldBe("must-survive");
        retained.Metadata["maxTurns"]?.ToString().ShouldBe("7");
        retained.Metadata.ContainsKey("objective").ShouldBeFalse("do not resurrect independently deleted metadata");
        var stored = System.Text.Json.JsonSerializer.SerializeToElement(retained.Metadata["interruptedExchangeEvidence"]);
        stored.GetProperty("incompleteToolCount").GetInt32().ShouldBe(evidence.IncompleteToolCount);
        stored.GetProperty("completedToolCount").GetInt32().ShouldBe(evidence.CompletedToolCount);
        retained.Status.ShouldBe(cause == "caller" ? GatewaySessionStatus.Active : GatewaySessionStatus.Sealed);
        retained.Metadata.ContainsKey("error").ShouldBe(cause != "caller");
        var history = retained.GetHistorySnapshot();
        history.Count(row => row.Role == MessageRole.User).ShouldBe(interruption == "admission" ? 1 : 2);
        history.First(row => row.Role == MessageRole.User).Content.ShouldBe("preserve local user");
        history.Count(row => row.Role == MessageRole.Assistant).ShouldBe(interruption == "admission" ? 0 : 1);
        if (interruption != "admission")
        {
            history.ShouldContain(row => row.Role == MessageRole.Assistant && row.Content == "continue");
            history.ShouldContain(row => row.ToolCallId == "prior-write" && row.Content == "prior receipt");
        }
        history.Count(row => row.ToolIsIncomplete == true).ShouldBe(1);
        history.Where(row => row.Kind == MessageKind.ToolResult && row.ToolCallId == "durable-write")
            .ShouldHaveSingleItem().Content.ShouldBe("durable receipt");
        if (interruption == "partial") history.ShouldContain(row => row.ToolCallId == "pending-write" && row.Content == "pending receipt");
        prompts.ShouldBe(interruption == "admission" ? 0 : 2);
        admissions.ShouldBe(1);
    }

    [Theory]
    [InlineData("partial", "reload", false)]
    [InlineData("partial", "reload", true)]
    [InlineData("partial", "save", false)]
    [InlineData("partial", "save", true)]
    [InlineData("plain", "reload", false)]
    [InlineData("plain", "reload", true)]
    [InlineData("plain", "save", false)]
    [InlineData("plain", "save", true)]
    [InlineData("admission", "reload", false)]
    [InlineData("admission", "reload", true)]
    [InlineData("admission", "save", false)]
    [InlineData("admission", "save", true)]
    public async Task ConverseAsync_CancellationBookkeepingFails_PreservesCauseAndAvailableEvidence(
        string interruption, string fault, bool engineDeadline)
    {
        var initiator = AgentId.From("test-agent");
        var target = AgentId.From("agent-c");
        var directory = Path.Combine(Path.GetTempPath(), "exchange-5035-bookkeeping", Guid.NewGuid().ToString("N"));
        using var cleanup = new DirectoryCleanup(directory);
        var conversations = new InMemoryConversationStore();
        var durable = new FileSessionStore(directory, NullLogger<FileSessionStore>.Instance,
            new System.IO.Abstractions.FileSystem(), conversations);
        using var caller = new CancellationTokenSource();
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var faultArmed = false;
        var faultCount = 0;
        var storageFailure = new IOException("injected cancellation bookkeeping " + fault);
        var store = new Mock<ISessionStore>(MockBehavior.Strict);
        store.Setup(s => s.GetOrCreateAsync(It.IsAny<SessionId>(), It.IsAny<AgentId>(), It.IsAny<CancellationToken>()))
            .Returns((SessionId id, AgentId agent, CancellationToken ct) => durable.GetOrCreateAsync(id, agent, ct));
        store.Setup(s => s.GetAsync(It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .Returns((SessionId id, CancellationToken ct) =>
            {
                if (faultArmed && fault == "reload" && faultCount == 0)
                {
                    ct.CanBeCanceled.ShouldBeFalse();
                    faultCount++;
                    throw storageFailure;
                }
                // Model an independent aggregate, not FileSessionStore's mutable cache. The
                // caller marker must see only what a successful write actually committed.
                return new FileSessionStore(directory, NullLogger<FileSessionStore>.Instance,
                    new System.IO.Abstractions.FileSystem(), conversations).GetAsync(id, ct);
            });
        store.Setup(s => s.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<CancellationToken>()))
            .Returns((GatewaySession session, CancellationToken ct) =>
            {
                if (faultArmed && fault == "save" && faultCount == 0)
                {
                    ct.CanBeCanceled.ShouldBeFalse();
                    session.Metadata.ShouldContainKey("interruptedExchangeEvidence");
                    session.Status.ShouldBe(GatewaySessionStatus.Active);
                    faultCount++;
                    throw storageFailure;
                }
                return durable.SaveAsync(session, ct);
            });
        Exception? original = null;
        Task<AgentResponse> Interrupt(CancellationToken ct)
        {
            faultArmed = true;
            if (engineDeadline) clock.Advance(TimeSpan.FromMinutes(1));
            else caller.Cancel();
            ct.IsCancellationRequested.ShouldBeTrue();
            original = interruption == "partial"
                ? new AgentPromptInterruptedException(new AgentResponse
                {
                    Content = "available unfinished prose",
                    ToolCalls = [new("completed", "write", false, "{}", "available mutation receipt")]
                }, ct)
                : new OperationCanceledException("original cancellation", ct);
            throw original;
        }
        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string _, CancellationToken ct) => Interrupt(ct));
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(target, It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .Returns((AgentId _, SessionId _, CancellationToken ct) =>
            {
                if (interruption == "admission") _ = Interrupt(ct);
                return Task.FromResult(handle.Object);
            });
        var logger = new Mock<Microsoft.Extensions.Logging.ILogger<AgentExchangeService>>();
        var engine = new AgentExchangeTurnEngine(store.Object, conversations, NullLogger.Instance, null, clock);
        var service = new AgentExchangeService(CreateRegistry(initiator, target, [target.Value]).Object,
            supervisor.Object, store.Object, conversations, Options.Create(new GatewayOptions()),
            logger.Object, turnEngine: engine);
        Exception? observed = null;
        try
        {
            await service.ConverseAsync(new AgentExchangeRequest
            {
                InitiatorId = initiator, TargetId = target, Message = "hello", MaxTurns = 1,
                Deadline = engineDeadline ? clock.GetUtcNow().AddMinutes(1) : null
            }, caller.Token);
        }
        catch (Exception ex) { observed = ex; }
        faultCount.ShouldBe(1, "the injected fault must actually hit cancellation bookkeeping");
        // Read an independent persisted aggregate: failed writes are not evidence of durability.
        var reopened = new FileSessionStore(directory, NullLogger<FileSessionStore>.Instance,
            new System.IO.Abstractions.FileSystem(), conversations);
        var summary = (await reopened.GetExistenceAsync(initiator, new ExistenceQuery())).ShouldHaveSingleItem();
        var retained = (await reopened.GetAsync(summary.SessionId)).ShouldNotBeNull();
        retained.Status.ShouldBe(engineDeadline ? GatewaySessionStatus.Sealed : GatewaySessionStatus.Active);
        var conversation = (await conversations.GetAsync(retained.ConversationId)).ShouldNotBeNull();
        conversation.Status.ShouldBe(engineDeadline ? ConversationStatus.Archived : ConversationStatus.Active);
        var cancellation = observed.ShouldNotBeNull().ShouldBeAssignableTo<OperationCanceledException>();
        if (engineDeadline) cancellation.ShouldBeOfType<AgentExchangeDeadlineExceededException>();
        else
        {
            cancellation.CancellationToken.ShouldBe(caller.Token);
            retained.Metadata.ShouldNotContainKey("error");
            retained.Metadata["exchangeOutcome"]?.ToString().ShouldBe("callerCancelled");
            if (interruption == "plain") ReferenceEquals(cancellation, original).ShouldBeTrue();
        }
        var evidence = ((cancellation as AgentExchangeCancelledException)?.Evidence
            ?? cancellation.Data[nameof(AgentExchangeCancellationEvidence)] as AgentExchangeCancellationEvidence).ShouldNotBeNull();
        evidence.Phase.ShouldBe(interruption == "admission" ? "neverDispatched" : "promptInterrupted");
        evidence.CompletedToolCount.ShouldBe(interruption == "partial" ? 1 : 0);
        evidence.PartialText.ShouldBe(interruption == "partial" ? "available unfinished prose" : null);
        retained.GetHistorySnapshot().ShouldNotContain(row => row.Role == MessageRole.Assistant);
        if (fault == "save" && !engineDeadline)
        {
            retained.Metadata.ShouldNotContainKey("interruptedExchangeEvidence",
                "a failed receipt save must not be presented as durable by a later marker write");
            retained.GetHistorySnapshot().ShouldNotContain(row => row.Role == MessageRole.Tool);
        }
        else
        {
            retained.Metadata.ShouldContainKey("interruptedExchangeEvidence");
            if (interruption == "partial")
                retained.GetHistorySnapshot().ShouldContain(row => row.Content == "available mutation receipt");
        }
        logger.Invocations.ShouldContain(call => call.Arguments.Any(argument => ReferenceEquals(argument, storageFailure)),
            "bookkeeping failures must be logged, not silently discarded");
        handle.Verify(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            interruption == "admission" ? Times.Never() : Times.Once());
    }

    [Fact]
    public void SanitizePartialText_UnlimitedPolicy_StillBoundsUnicodeReceipt()
    {
        var sink = new DefaultToolAuditSink(new ToolInvocationRecordPolicy(maxResultBytes: 0));
        var text = sink.SanitizePartialText(string.Concat(Enumerable.Repeat("界😀", 20000))).ShouldNotBeNull();
        System.Text.Encoding.UTF8.GetByteCount(text).ShouldBeLessThanOrEqualTo(ToolInvocationRecordPolicy.DefaultMaxBytes + 64);
        text.ShouldContain("[truncated");
        // Throwing UTF-8 encoding rejects lone surrogate halves at the truncation boundary.
        new System.Text.UTF8Encoding(false, true).GetBytes(text).Length.ShouldBeGreaterThan(0);
    }

    private sealed class DirectoryCleanup(string path) : IDisposable
    {
        public void Dispose()
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
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
