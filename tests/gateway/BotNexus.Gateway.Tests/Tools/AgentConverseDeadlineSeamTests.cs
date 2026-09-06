using System.Text.Json;
using BotNexus.Agent.Core.Types;
using BotNexus.Domain.AgentExchange;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using BotNexus.Gateway.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using GatewaySessionStatus = BotNexus.Gateway.Abstractions.Models.SessionStatus;

namespace BotNexus.Gateway.Tests.Tools;

/// <summary>
/// Pins #3913 across the real tool -> service -> turn-engine boundary. Mocking the exchange
/// service would miss the engine deadline firing before the tool's linked backstop, which is
/// precisely the cancellation that was incorrectly reported as targetUnavailable.
/// </summary>
public sealed class AgentConverseDeadlineSeamTests
{
    private const string DeadlineError = "Agent exchange exceeded its deadline.";
    private static readonly TimeSpan DiagnosticHangGuard = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The engine-owned deadline must retain its provenance through the service, even while
    /// the tool's backstop is unsignalled. No new production exception type is assumed here.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WhenEngineDeadlineExpires_ReportsTimeoutAndSealsAndArchives()
    {
        using var caller = new CancellationTokenSource();
        var engineCancellationObserved = false;
        var harness = new ExchangeHarness(async token =>
        {
            try
            {
                await WaitForCancellationAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                engineCancellationObserved = true;
                throw;
            }

            throw new InvalidOperationException("The cancellation sentinel unexpectedly completed.");
        });

        var report = await harness.ExecuteAsync(timeoutSeconds: 1, caller.Token);

        harness.AssertSeam(backstopCancelled: false);
        engineCancellationObserved.ShouldBeTrue();
        caller.IsCancellationRequested.ShouldBeFalse();
        await harness.AssertSealedAndArchivedAsync(DeadlineError);

        AssertReport(report, "timeout", "caller", timeoutSeconds: 1);
        // Only a lower budget bound: the seam snapshot above, not elapsed < 6s, proves that
        // the engine beat the backstop. Allow modest timer/rounding jitter at the 1s boundary.
        report.GetProperty("elapsedSeconds").GetDouble().ShouldBeGreaterThanOrEqualTo(0.8d);
        report.GetProperty("retryAdvised").GetBoolean().ShouldBeTrue();
        var message = report.GetProperty("message").GetString().ShouldNotBeNull();
        message.ShouldContain("timed out");
        message.ShouldContain("budget was exhausted");
        message.ShouldNotContain("NOT exhausted");
    }

    /// <summary>
    /// Cancelling a child of the actual handle token is a genuine target-side cancellation,
    /// not caller intent or an engine deadline. Cancellation must not flow back to its parent.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WhenTargetCancelsItsChildToken_ReportsTargetUnavailableAndSealsAndArchives()
    {
        const string targetError = "Target cancelled its own prompt.";
        using var caller = new CancellationTokenSource();
        var childWasCancelled = false;
        var handleTokenWasCancelled = false;
        var harness = new ExchangeHarness(token =>
        {
            using var child = CancellationTokenSource.CreateLinkedTokenSource(token);
            child.Cancel();
            childWasCancelled = child.IsCancellationRequested;
            handleTokenWasCancelled = token.IsCancellationRequested;
            throw new OperationCanceledException(targetError, child.Token);
        });

        // A generous real tool budget keeps this a synchronous target cancellation rather
        // than accidentally exercising the 1s engine deadline on a heavily loaded test host.
        var report = await harness.ExecuteAsync(timeoutSeconds: 600, caller.Token);

        childWasCancelled.ShouldBeTrue();
        handleTokenWasCancelled.ShouldBeFalse();
        caller.IsCancellationRequested.ShouldBeFalse();
        harness.AssertSeam(backstopCancelled: false);
        await harness.AssertSealedAndArchivedAsync(targetError);
        AssertReport(report, "targetUnavailable", "target", timeoutSeconds: 600);
        report.GetProperty("message").GetString().ShouldNotBeNull().ShouldContain("NOT exhausted");
    }

    /// <summary>
    /// A caller abort inside the real prompt must remain retryable: no seal, error or archive.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WhenCallerAbortsDuringPrompt_ReportsCallerAbortedAndLeavesSessionActive()
    {
        using var caller = new CancellationTokenSource();
        var handleTokenWasCancelled = false;
        var harness = new ExchangeHarness(token =>
        {
            caller.Cancel();
            handleTokenWasCancelled = token.IsCancellationRequested;
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Caller cancellation did not reach the prompt.");
        });

        var report = await harness.ExecuteAsync(timeoutSeconds: 600, caller.Token);

        caller.IsCancellationRequested.ShouldBeTrue();
        handleTokenWasCancelled.ShouldBeTrue();
        harness.AssertSeam(backstopCancelled: true);
        await harness.AssertActiveAndNotArchivedAsync();
        AssertReport(report, "callerAborted", "caller", timeoutSeconds: 600);
        report.GetProperty("retryAdvised").GetBoolean().ShouldBeFalse();
    }

    /// <summary>
    /// Deterministically observes engine cancellation first, then cancels the caller before
    /// rethrowing from the prompt. Both causes are live at the engine's catch: caller must win.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WhenCallerAbortsAfterEngineDeadlineButBeforeUnwind_CallerWinsWithoutSeal()
    {
        using var caller = new CancellationTokenSource();
        var deadlineObservedBeforeCallerAbort = false;
        ExchangeHarness? harness = null;
        harness = new ExchangeHarness(async token =>
        {
            try
            {
                await WaitForCancellationAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Snapshot before cancelling ambient: neither ancestor token may explain the
                // observed handle cancellation. The engine's deadline is the only other source.
                var activeHarness = harness.ShouldNotBeNull();
                deadlineObservedBeforeCallerAbort = !caller.IsCancellationRequested
                    && !activeHarness.Recorder.BackstopToken.IsCancellationRequested;
                caller.Cancel();
                throw;
            }

            throw new InvalidOperationException("The cancellation sentinel unexpectedly completed.");
        });

        var report = await harness.ExecuteAsync(timeoutSeconds: 1, caller.Token);

        deadlineObservedBeforeCallerAbort.ShouldBeTrue();
        caller.IsCancellationRequested.ShouldBeTrue();
        harness.AssertSeam(backstopCancelled: true);
        await harness.AssertActiveAndNotArchivedAsync();
        AssertReport(report, "callerAborted", "caller", timeoutSeconds: 1);
        report.GetProperty("elapsedSeconds").GetDouble().ShouldBeGreaterThanOrEqualTo(0.8d);
        report.GetProperty("retryAdvised").GetBoolean().ShouldBeFalse();
    }

    private static Task WaitForCancellationAsync(CancellationToken token)
        // The finite WaitAsync is only a diagnostic hang guard, never coordination or a
        // cancellation source. The infinite sentinel can finish only when the real token fires.
        => Task.Delay(Timeout.InfiniteTimeSpan, token).WaitAsync(DiagnosticHangGuard);

    private static void AssertReport(JsonElement report, string cause, string cancelledBy, int timeoutSeconds)
    {
        report.GetProperty("cancelled").GetBoolean().ShouldBeTrue();
        report.GetProperty("cancellationCause").GetString().ShouldBe(cause);
        report.GetProperty("cancelledBy").GetString().ShouldBe(cancelledBy);
        report.GetProperty("timeoutSeconds").GetInt32().ShouldBe(timeoutSeconds);
        report.GetProperty("targetAgentId").GetString().ShouldBe("agent-c");
        report.GetProperty("message").GetString().ShouldNotBeNull().ShouldNotContain("A task was canceled");
    }

    private sealed class ExchangeHarness
    {
        private static readonly AgentId Initiator = AgentId.From("test-agent");
        private static readonly AgentId Target = AgentId.From("agent-c");
        private readonly InMemorySessionStore _sessions = new();
        private readonly InMemoryConversationStore _conversations = new();
        private readonly Mock<IAgentHandle> _handle = new();
        private readonly AgentConverseTool _tool;

        public ExchangeHarness(Func<CancellationToken, Task<AgentResponse>> prompt)
        {
            var registry = new Mock<IAgentRegistry>();
            registry.Setup(r => r.Get(Initiator)).Returns(new AgentDescriptor
            {
                AgentId = Initiator,
                DisplayName = "Initiator",
                ModelId = "gpt-5-mini",
                ApiProvider = "copilot",
                SubAgentIds = [Target.Value]
            });
            registry.Setup(r => r.Contains(Target)).Returns(true);
            _handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((string _, CancellationToken token) => prompt(token));
            var supervisor = new Mock<IAgentSupervisor>();
            supervisor.Setup(s => s.GetOrCreateAsync(Target, It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(_handle.Object);

            // Explicit real engine, not a fake service that waits for the tool's 6s backstop.
            var engine = new AgentExchangeTurnEngine(
                _sessions, _conversations, NullLogger.Instance, budgetTracker: null);
            var service = new AgentExchangeService(
                registry.Object,
                supervisor.Object,
                _sessions,
                _conversations,
                Options.Create(new GatewayOptions()),
                NullLogger<AgentExchangeService>.Instance,
                turnEngine: engine);
            Recorder = new RecordingExchangeService(service);
            _tool = new AgentConverseTool(
                Recorder, _sessions, Initiator, SessionId.From("initiating-session"));
        }

        public RecordingExchangeService Recorder { get; }

        public async Task<JsonElement> ExecuteAsync(int timeoutSeconds, CancellationToken callerToken)
        {
            var result = await _tool.ExecuteAsync("deadline-seam-call", new Dictionary<string, object?>
            {
                ["agentId"] = Target.Value,
                ["message"] = "Exercise the real cancellation seam",
                ["timeoutSeconds"] = timeoutSeconds,
                ["maxTurns"] = 1
            }, callerToken).WaitAsync(DiagnosticHangGuard);

            var text = result.Content.Single(item => item.Type == AgentToolContentType.Text).Value;
            using var payload = JsonDocument.Parse(text);
            return payload.RootElement.Clone();
        }

        public void AssertSeam(bool backstopCancelled)
        {
            _handle.Verify(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
            var request = Recorder.Request.ShouldNotBeNull();
            request.InitiatorId.ShouldBe(Initiator);
            request.TargetId.ShouldBe(Target);
            request.MaxTurns.ShouldBe(1);
            request.Deadline.ShouldNotBeNull();
            Recorder.BackstopToken.CanBeCanceled.ShouldBeTrue();
            Recorder.CancellationCrossedSeam.ShouldBeTrue();
            // Captured IN the decorator catch, before the tool builds a report or disposes its
            // CTS. Checking the token after ExecuteAsync would not pin the relevant instant.
            Recorder.BackstopCancelledAtCatch.ShouldBe(backstopCancelled);
        }

        public async Task AssertSealedAndArchivedAsync(string error)
        {
            var session = await ReadExchangeSessionAsync();
            session.Status.ShouldBe(GatewaySessionStatus.Sealed);
            session.Metadata.ShouldContainKey("error");
            session.Metadata["error"].ShouldBe(error);
            var conversation = (await _conversations.ListAsync()).ShouldHaveSingleItem();
            conversation.ConversationId.ShouldBe(session.ConversationId);
            conversation.Status.ShouldBe(ConversationStatus.Archived);
            conversation.ActiveSessionId.ShouldBeNull();
        }

        public async Task AssertActiveAndNotArchivedAsync()
        {
            var session = await ReadExchangeSessionAsync();
            session.Status.ShouldBe(GatewaySessionStatus.Active);
            session.Metadata.ContainsKey("error").ShouldBeFalse();
            session.Metadata.TryGetValue("conversationStatus", out var status);
            (status as string).ShouldNotBe("error");
            var conversation = (await _conversations.ListAsync()).ShouldHaveSingleItem();
            conversation.ConversationId.ShouldBe(session.ConversationId);
            conversation.Status.ShouldBe(ConversationStatus.Active);
            conversation.ActiveSessionId.ShouldBe(session.SessionId);
        }

        private async Task<GatewaySession> ReadExchangeSessionAsync()
        {
            var existence = (await _sessions.GetExistenceAsync(Initiator, new ExistenceQuery()))
                .ShouldHaveSingleItem();
            return (await _sessions.GetAsync(existence.SessionId)).ShouldNotBeNull();
        }
    }

    // Observational only: request, token, result and exception pass through untouched. In
    // particular this decorator cannot repair the lost deadline provenance being tested.
    private sealed class RecordingExchangeService(IAgentExchangeService inner) : IAgentExchangeService
    {
        public AgentExchangeRequest? Request { get; private set; }
        public CancellationToken BackstopToken { get; private set; }
        public bool CancellationCrossedSeam { get; private set; }
        public bool? BackstopCancelledAtCatch { get; private set; }

        public async Task<AgentExchangeResult> ConverseAsync(
            AgentExchangeRequest request, CancellationToken cancellationToken = default)
        {
            Request = request;
            BackstopToken = cancellationToken;
            try
            {
                return await inner.ConverseAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                BackstopCancelledAtCatch = cancellationToken.IsCancellationRequested;
                CancellationCrossedSeam = true;
                throw;
            }
        }
    }
}
