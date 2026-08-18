using System.Collections.Concurrent;
using System.Text.Json;
using BotNexus.Agent.Core.Configuration;
using BotNexus.Agent.Core.Hooks;
using BotNexus.Agent.Core.Loop;
using BotNexus.Agent.Core.Tests.TestUtils;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;
using Moq;

namespace BotNexus.Agent.Core.Tests.Security;

/// <summary>
/// Issue #3356: the <c>BeforeToolCall</c> budget is armed with wall-clock
/// <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/>, so a host suspend spanning the hook
/// was charged against it — a 4h41m workstation sleep produced "hook timed out after 16945.1s
/// (budget 15.0s)" and denied the tool call fail-closed.
/// </summary>
/// <remarks>
/// <para>
/// Suspend is simulated through an injected <see cref="IHostSuspendDetector"/> rather than by
/// sleeping the test host: the property under test is "how much time did the process actually spend
/// running", which is exactly what the seam reports. No test here asserts a wall-clock upper bound
/// (the flake family of #2988/#2801/#3324/#3333) — every assertion is on the observable outcome:
/// did the tool run, was the result an error, and what did the diagnostic say.
/// </para>
/// <para>
/// Non-vacuity (AC5) is structural. <see cref="AlwaysSuspendedDetector"/> is the always-true mutant
/// of the suspend predicate and <see cref="NeverSuspendedDetector"/> the always-false one, and the
/// suite contains a test pinned to each: forcing always-true turns
/// <see cref="GenuinelySlowHook_StillBlocksFailClosed_EvenThoughDetectorIsConsulted"/> red, and
/// forcing always-false turns <see cref="HookSpanningHostSuspend_IsNotReportedAsTimeout_AndToolRuns"/>
/// red. A change that deleted the budget altogether fails the former.
/// </para>
/// </remarks>
public sealed class BeforeToolCallSuspendTests
{
    private static readonly TimeSpan ShortBudget = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// AC1: a hook whose wall-clock window was blown by a host suspend, but whose running time
    /// stayed inside the budget, must not be reported as a hook timeout and must not block the call.
    /// </summary>
    /// <remarks>
    /// The hook is held open until the budget cancellation has actually fired on its own token, so
    /// the breach is guaranteed to have occurred by construction rather than by racing a timer. On
    /// the retry after the discarded measurement it answers immediately — modelling the real case,
    /// where the policy provider is responsive again the moment the host is awake.
    /// </remarks>
    [Fact]
    public async Task HookSpanningHostSuspend_IsNotReportedAsTimeout_AndToolRuns()
    {
        var diagnostics = new ConcurrentQueue<string>();
        var toolInvoked = false;
        var tool = CreateTool("write", _ =>
        {
            toolInvoked = true;
            return Task.FromResult(Ok("executed"));
        });

        var attempts = 0;
        var config = TestHelpers.CreateTestConfig(
            beforeToolCall: async (_, ct) =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    // First attempt spans the "suspend": wait until the wall-clock budget fires.
                    var budgetFired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    await using var registration = ct.Register(() => budgetFired.TrySetResult(true)).ConfigureAwait(false);
                    if (ct.IsCancellationRequested)
                    {
                        budgetFired.TrySetResult(true);
                    }

                    await budgetFired.Task.ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                }

                // Post-resume: an unambiguous allow, produced promptly.
                return null;
            },
            beforeToolCallTimeout: ShortBudget,
            onDiagnostic: diagnostics.Enqueue,
            suspendDetector: new AlwaysSuspendedDetector());

        var results = await ExecuteAsync(config, tool, "write", CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(30));

        toolInvoked.ShouldBeTrue();
        results.ShouldHaveSingleItem();
        results[0].IsError.ShouldBeFalse();
        results[0].Result.Content[0].Value.ShouldBe("executed");

        // The tool call was not denied, and no message accused the hook of timing out.
        diagnostics.ShouldNotContain(m => m.Contains("hook timed out", StringComparison.Ordinal));
        diagnostics.ShouldNotContain(m => m.Contains("Tool call blocked", StringComparison.Ordinal));
    }

    /// <summary>
    /// AC2 (and the non-vacuity anchor): a hook that genuinely burns its budget in RUNNING time is
    /// still blocked fail-closed with the existing message, even though the suspend detector is now
    /// consulted on every breach.
    /// </summary>
    /// <remarks>
    /// This is the test that a broken fix cannot pass. If the suspend predicate were mutated to
    /// always-true, or if the budget were removed entirely, the hook here would be allowed to
    /// answer <c>Block: false</c> and the tool would execute — both assertions below fail.
    /// </remarks>
    [Fact]
    public async Task GenuinelySlowHook_StillBlocksFailClosed_EvenThoughDetectorIsConsulted()
    {
        var diagnostics = new ConcurrentQueue<string>();
        var toolInvoked = false;
        var tool = CreateTool("dangerous", _ =>
        {
            toolInvoked = true;
            return Task.FromResult(Ok("executed"));
        });

        var invocations = 0;
        var config = TestHelpers.CreateTestConfig(
            beforeToolCall: async (_, ct) =>
            {
                Interlocked.Increment(ref invocations);
                await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
                return new BeforeToolCallResult(Block: false);
            },
            beforeToolCallTimeout: ShortBudget,
            onDiagnostic: diagnostics.Enqueue,
            // The host was awake throughout: active time tracked wall-clock, so the hook really
            // did overrun. This is the honest reading of the real, genuinely-slow case.
            suspendDetector: new NeverSuspendedDetector());

        var results = await ExecuteAsync(config, tool, "dangerous", CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(30));

        toolInvoked.ShouldBeFalse();
        results[0].IsError.ShouldBeTrue();
        results[0].Result.Content[0].Value.ShouldContain("timed out");
        results[0].Result.Content[0].Value.ShouldContain("Tool call blocked");
        diagnostics.ShouldContain(m => m.Contains("BeforeToolCall hook timed out", StringComparison.Ordinal));

        // No suspend was detected, so no second window is granted: the hook is called exactly once
        // and denied on its first breach. This is what kills the always-true mutant of the suspend
        // predicate, which would hand a genuinely-wedged provider a free extra budget.
        invocations.ShouldBe(1);
        diagnostics.ShouldNotContain(m => m.Contains("measurement discarded", StringComparison.Ordinal));
    }

    /// <summary>
    /// AC3: the suspend-attributed case is distinguishable in TEXT from a genuine budget breach, so
    /// an operator is not sent to investigate a policy provider that never ran slowly.
    /// </summary>
    [Fact]
    public async Task SuspendAttributedMeasurement_EmitsADistinctDiagnostic()
    {
        var diagnostics = new ConcurrentQueue<string>();
        var tool = CreateTool("write", _ => Task.FromResult(Ok("executed")));

        var attempts = 0;
        var config = TestHelpers.CreateTestConfig(
            beforeToolCall: async (_, ct) =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    var budgetFired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    await using var registration = ct.Register(() => budgetFired.TrySetResult(true)).ConfigureAwait(false);
                    if (ct.IsCancellationRequested)
                    {
                        budgetFired.TrySetResult(true);
                    }

                    await budgetFired.Task.ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                }

                return null;
            },
            beforeToolCallTimeout: ShortBudget,
            onDiagnostic: diagnostics.Enqueue,
            suspendDetector: new AlwaysSuspendedDetector());

        await ExecuteAsync(config, tool, "write", CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(30));

        var suspendMessage = diagnostics.ShouldHaveSingleItem();
        suspendMessage.ShouldContain("measurement discarded");
        suspendMessage.ShouldContain("host was suspended");
        suspendMessage.ShouldContain("write");
        suspendMessage.ShouldContain("tc-1");

        // The distinction is the whole point: it must NOT read like the fail-closed breach.
        suspendMessage.ShouldNotContain("hook timed out");
        suspendMessage.ShouldNotContain("Tool call blocked because no policy decision was reached");
    }

    /// <summary>
    /// AC4: genuine ambient turn cancellation still surfaces as cancellation, never as a hook
    /// timeout and never as a suspend-attributed retry — even with a detector that would otherwise
    /// classify every breach as a suspend.
    /// </summary>
    [Fact]
    public async Task AmbientCancellation_StillPropagates_AndIsNotRetriedAsSuspend()
    {
        var diagnostics = new ConcurrentQueue<string>();
        var hookEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocations = 0;
        var tool = CreateTool("dangerous", _ => Task.FromResult(Ok("executed")));

        var config = TestHelpers.CreateTestConfig(
            beforeToolCall: async (_, ct) =>
            {
                Interlocked.Increment(ref invocations);
                hookEntered.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
                return new BeforeToolCallResult(Block: false);
            },
            beforeToolCallTimeout: TimeSpan.FromSeconds(60),
            onDiagnostic: diagnostics.Enqueue,
            suspendDetector: new AlwaysSuspendedDetector());

        using var cts = new CancellationTokenSource();
        var executeTask = ExecuteAsync(config, tool, "dangerous", cts.Token);

        await hookEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => executeTask.WaitAsync(TimeSpan.FromSeconds(30)));
        diagnostics.ShouldBeEmpty();
        // A cancelled turn must not be re-driven through the hook by the suspend retry.
        invocations.ShouldBe(1);
    }

    /// <summary>
    /// The suspend allowance is used at most once per tool call. A hook that keeps breaching must
    /// eventually fail closed, otherwise a detector that always claims suspend would convert the
    /// budget into an unbounded retry loop and reinstate the liveness bug #2518 fixed.
    /// </summary>
    [Fact]
    public async Task RepeatedBreaches_FailClosed_EvenWhenAlwaysAttributedToSuspend()
    {
        var toolInvoked = false;
        var tool = CreateTool("dangerous", _ =>
        {
            toolInvoked = true;
            return Task.FromResult(Ok("executed"));
        });

        var invocations = 0;
        var config = TestHelpers.CreateTestConfig(
            beforeToolCall: async (_, ct) =>
            {
                Interlocked.Increment(ref invocations);
                await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
                return new BeforeToolCallResult(Block: false);
            },
            beforeToolCallTimeout: ShortBudget,
            suspendDetector: new AlwaysSuspendedDetector());

        var results = await ExecuteAsync(config, tool, "dangerous", CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(30));

        toolInvoked.ShouldBeFalse();
        results[0].IsError.ShouldBeTrue();
        results[0].Result.Content[0].Value.ShouldContain("timed out");
        invocations.ShouldBe(2);
    }

    /// <summary>
    /// The platform detector must report a real, non-negative running time and must never report a
    /// suspend it did not observe: two readings taken back to back over a short spin are well inside
    /// any sane budget. Asserts an ordering property, not a duration bound.
    /// </summary>
    [Fact]
    public void PlatformDetector_ReportsNonNegativeActiveTime_AndDoesNotRunBackwards()
    {
        var detector = HostSuspendDetector.Instance;

        var start = detector.GetTimestamp();
        var first = detector.GetElapsedActiveTime(start);
        Thread.SpinWait(10_000);
        var second = detector.GetElapsedActiveTime(start);

        first.ShouldBeGreaterThanOrEqualTo(TimeSpan.Zero);
        second.ShouldBeGreaterThanOrEqualTo(first);
    }

    /// <summary>The always-true mutant of the suspend predicate: no time is ever charged as running.</summary>
    private sealed class AlwaysSuspendedDetector : IHostSuspendDetector
    {
        public long GetTimestamp() => 0;

        public TimeSpan GetElapsedActiveTime(long startTimestamp) => TimeSpan.Zero;
    }

    /// <summary>The always-false mutant: the host was awake, so active time tracks wall clock.</summary>
    private sealed class NeverSuspendedDetector : IHostSuspendDetector
    {
        public long GetTimestamp() => 0;

        public TimeSpan GetElapsedActiveTime(long startTimestamp) => TimeSpan.MaxValue;
    }

    private static Task<IReadOnlyList<ToolResultAgentMessage>> ExecuteAsync(
        AgentLoopConfig config,
        IAgentTool tool,
        string toolName,
        CancellationToken cancellationToken)
    {
        var context = new AgentContext(null, [], [tool]);
        var assistant = new AssistantAgentMessage(
            string.Empty,
            [new ToolCallContent("tc-1", toolName, new Dictionary<string, object?>())],
            StopReason.ToolUse);

        return ToolExecutor.ExecuteAsync(context, assistant, config, _ => Task.CompletedTask, cancellationToken);
    }

    private static AgentToolResult Ok(string text) =>
        new([new AgentToolContent(AgentToolContentType.Text, text)]);

    private static IAgentTool CreateTool(string name, Func<CancellationToken, Task<AgentToolResult>> execute)
    {
        var mock = new Mock<IAgentTool>();
        mock.Setup(t => t.Name).Returns(name);
        mock.Setup(t => t.Label).Returns(name);
        mock.Setup(t => t.Definition).Returns(
            new Tool(name, name, JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone()));
        mock.Setup(t => t.PrepareArgumentsAsync(It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<string, object?> args, CancellationToken _) => args);
        mock.Setup(t => t.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyDictionary<string, object?>>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<AgentToolUpdateCallback?>()))
            .Returns((string _, IReadOnlyDictionary<string, object?> _, CancellationToken ct, AgentToolUpdateCallback? _) => execute(ct));
        return mock.Object;
    }
}
