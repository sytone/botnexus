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
    [Fact]
    public async Task HookIgnoringCancellation_AnswersLate_StillBlocksToolCall()
    {
        var toolInvoked = false;
        var tool = CreateTool("dangerous", _ =>
        {
            toolInvoked = true;
            return Task.FromResult(Ok("executed"));
        });

        var config = TestHelpers.CreateTestConfig(
            beforeToolCall: async (_, _) =>
            {
                // Deliberately ignores the token, then allows the call.
                await Task.Delay(TimeSpan.FromMilliseconds(600), CancellationToken.None).ConfigureAwait(false);
                return new BeforeToolCallResult(Block: false);
            },
            beforeToolCallTimeout: ShortBudget);

        var results = await ExecuteAsync(config, tool, "dangerous", CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

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

        var config = TestHelpers.CreateTestConfig(
            beforeToolCall: async (_, _) =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(300), CancellationToken.None).ConfigureAwait(false);
                return null;
            },
            beforeToolCallTimeout: Timeout.InfiniteTimeSpan);

        var results = await ExecuteAsync(config, tool, "safe", CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

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
