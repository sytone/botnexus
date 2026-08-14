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
/// Issue #2518: the <c>BeforeToolCall</c> pre-execution policy gate must be bounded by a
/// wall-clock budget, and a breach of that budget must fail CLOSED (the tool call is blocked,
/// never executed). These tests assert the observable outcome — whether the tool actually ran
/// and what result the loop produced — not merely that a timeout token fired.
/// </summary>
public sealed class BeforeToolCallTimeoutTests
{
    private static readonly TimeSpan ShortBudget = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// A hook that never returns must not stall the turn: the call is blocked within the budget
    /// and the tool body never executes.
    /// </summary>
    [Fact]
    public async Task HangingHook_BlocksToolCall_AndToolNeverExecutes()
    {
        var toolInvoked = false;
        var tool = CreateTool("dangerous", _ =>
        {
            toolInvoked = true;
            return Task.FromResult(Ok("executed"));
        });

        var config = TestHelpers.CreateTestConfig(
            beforeToolCall: async (_, ct) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
                return new BeforeToolCallResult(Block: false);
            },
            beforeToolCallTimeout: ShortBudget);

        var results = await ExecuteAsync(config, tool, "dangerous", CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        toolInvoked.ShouldBeFalse();
        results.ShouldHaveSingleItem();
        results[0].IsError.ShouldBeTrue();
        results[0].Result.Content[0].Value.ShouldContain("timed out");
    }

    /// <summary>
    /// The timeout breach must be reported through the diagnostic sink with elapsed time, the
    /// budget, and the offending tool identity so a slow policy provider is diagnosable.
    /// </summary>
    [Fact]
    public async Task HangingHook_ReportsWarningWithElapsedAndToolIdentity()
    {
        var diagnostics = new ConcurrentQueue<string>();
        var tool = CreateTool("dangerous", _ => Task.FromResult(Ok("executed")));

        var config = TestHelpers.CreateTestConfig(
            beforeToolCall: async (_, ct) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
                return new BeforeToolCallResult(Block: false);
            },
            beforeToolCallTimeout: ShortBudget,
            onDiagnostic: diagnostics.Enqueue);

        await ExecuteAsync(config, tool, "dangerous", CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        var messages = diagnostics.ToArray();
        messages.ShouldNotBeEmpty();
        var breach = messages.ShouldHaveSingleItem();
        breach.ShouldContain("BeforeToolCall hook timed out");
        breach.ShouldContain("dangerous");
        breach.ShouldContain("tc-1");
        breach.ShouldContain("budget");
    }

    /// <summary>
    /// A hook that ignores its cancellation token and answers late is still outside the budget it
    /// was given, so its verdict must not be honoured. Fail closed.
    /// </summary>
    /// <remarks>
    /// #3179: "late" used to be established by a 600 ms <c>Task.Delay</c> outrunning a 150 ms
    /// budget — two real timers competing for threadpool scheduling, which starved under parallel
    /// CI load and let the hook's <c>Block: false</c> be observed first. Lateness is now enforced
    /// by construction: the hook cannot return until the budget cancellation has actually been
    /// observed on its own token and the test has released it. No wall-clock margin remains, so
    /// the ordering holds on an arbitrarily loaded runner.
    /// </remarks>
    [Fact]
    public async Task HookIgnoringCancellation_AnswersLate_StillBlocksToolCall()
    {
        var toolInvoked = false;
        var tool = CreateTool("dangerous", _ =>
        {
            toolInvoked = true;
            return Task.FromResult(Ok("executed"));
        });

        var budgetObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHook = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var config = TestHelpers.CreateTestConfig(
            beforeToolCall: async (_, ct) =>
            {
                // Deliberately ignores the token for control flow, but reports when the budget
                // cancellation fires so the test can guarantee the answer lands strictly after it.
                await using var registration = ct.Register(() => budgetObserved.TrySetResult(true))
                    .ConfigureAwait(false);
                if (ct.IsCancellationRequested)
                {
                    budgetObserved.TrySetResult(true);
                }

                await releaseHook.Task.ConfigureAwait(false);
                return new BeforeToolCallResult(Block: false);
            },
            beforeToolCallTimeout: ShortBudget);

        var executeTask = ExecuteAsync(config, tool, "dangerous", CancellationToken.None);

        // Wait for the breach itself, not for a duration. Only then is the hook allowed to answer.
        await budgetObserved.Task.WaitAsync(TimeSpan.FromSeconds(30));
        releaseHook.SetResult(true);

        var results = await executeTask.WaitAsync(TimeSpan.FromSeconds(30));

        toolInvoked.ShouldBeFalse();
        results[0].IsError.ShouldBeTrue();
        results[0].Result.Content[0].Value.ShouldContain("timed out");
    }

    /// <summary>
    /// A hook that completes inside the budget is entirely unaffected: the tool runs and no
    /// timeout diagnostic is emitted.
    /// </summary>
    [Fact]
    public async Task FastHook_AllowsToolCall_AndEmitsNoWarning()
    {
        var diagnostics = new ConcurrentQueue<string>();
        var toolInvoked = false;
        var tool = CreateTool("safe", _ =>
        {
            toolInvoked = true;
            return Task.FromResult(Ok("executed"));
        });

        var config = TestHelpers.CreateTestConfig(
            beforeToolCall: (_, _) => Task.FromResult<BeforeToolCallResult?>(null),
            beforeToolCallTimeout: TimeSpan.FromSeconds(5),
            onDiagnostic: diagnostics.Enqueue);

        var results = await ExecuteAsync(config, tool, "safe", CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        toolInvoked.ShouldBeTrue();
        results[0].IsError.ShouldBeFalse();
        results[0].Result.Content[0].Value.ShouldBe("executed");
        diagnostics.ShouldBeEmpty();
    }

    /// <summary>
    /// A hook that blocks inside the budget keeps its own block reason — the timeout wrapper must
    /// not overwrite a genuine policy verdict.
    /// </summary>
    [Fact]
    public async Task FastHookThatBlocks_KeepsItsOwnReason()
    {
        var toolInvoked = false;
        var tool = CreateTool("dangerous", _ =>
        {
            toolInvoked = true;
            return Task.FromResult(Ok("executed"));
        });

        var config = TestHelpers.CreateTestConfig(
            beforeToolCall: (_, _) =>
                Task.FromResult<BeforeToolCallResult?>(new BeforeToolCallResult(Block: true, Reason: "denied by policy")),
            beforeToolCallTimeout: TimeSpan.FromSeconds(5));

        var results = await ExecuteAsync(config, tool, "dangerous", CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        toolInvoked.ShouldBeFalse();
        results[0].IsError.ShouldBeTrue();
        results[0].Result.Content[0].Value.ShouldBe("denied by policy");
    }

    /// <summary>
    /// Ambient turn cancellation while the hook is in flight must surface as cancellation, not as
    /// a hook timeout — otherwise a user-cancelled turn masquerades as a policy failure.
    /// </summary>
    [Fact]
    public async Task AmbientCancellationDuringHook_SurfacesAsCancellation_NotHookTimeout()
    {
        var diagnostics = new ConcurrentQueue<string>();
        var hookEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tool = CreateTool("dangerous", _ => Task.FromResult(Ok("executed")));

        var config = TestHelpers.CreateTestConfig(
            beforeToolCall: async (_, ct) =>
            {
                hookEntered.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
                return new BeforeToolCallResult(Block: false);
            },
            // Budget far longer than the test, so any cancellation observed comes from the
            // ambient token rather than the hook budget.
            beforeToolCallTimeout: TimeSpan.FromSeconds(60),
            onDiagnostic: diagnostics.Enqueue);

        using var cts = new CancellationTokenSource();
        var executeTask = ExecuteAsync(config, tool, "dangerous", cts.Token);

        await hookEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => executeTask.WaitAsync(TimeSpan.FromSeconds(10)));
        diagnostics.ShouldBeEmpty();
    }

    /// <summary>
    /// An explicitly infinite budget preserves the prior unbounded behaviour for callers that
    /// opt out; the hook's own verdict is honoured however long it takes.
    /// </summary>
    [Fact]
    public async Task InfiniteBudget_DisablesTheHookTimeout()
    {
        var toolInvoked = false;
        var tool = CreateTool("safe", _ =>
        {
            toolInvoked = true;
            return Task.FromResult(Ok("executed"));
        });

        // #3179: the sibling audit. This previously leaned on a 300 ms delay to represent "slow",
        // which is the same wall-clock shape. The hook is now held open by a signal the test
        // releases, and it records whether its token was cancelled — proving the infinite budget
        // never armed a timer at all, rather than merely outlasting one.
        var hookEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHook = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hookTokenWasCancelled = true;

        var config = TestHelpers.CreateTestConfig(
            beforeToolCall: async (_, ct) =>
            {
                hookEntered.TrySetResult(true);
                await releaseHook.Task.ConfigureAwait(false);
                hookTokenWasCancelled = ct.IsCancellationRequested;
                return null;
            },
            beforeToolCallTimeout: Timeout.InfiniteTimeSpan);

        var executeTask = ExecuteAsync(config, tool, "safe", CancellationToken.None);

        await hookEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        releaseHook.SetResult(true);

        var results = await executeTask.WaitAsync(TimeSpan.FromSeconds(30));

        hookTokenWasCancelled.ShouldBeFalse();
        toolInvoked.ShouldBeTrue();
        results[0].IsError.ShouldBeFalse();
    }

    /// <summary>The shipped default budget is 15 seconds.</summary>
    [Fact]
    public void DefaultBudget_IsFifteenSeconds()
    {
        AgentLoopConfig.DefaultBeforeToolCallTimeout.ShouldBe(TimeSpan.FromSeconds(15));
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
