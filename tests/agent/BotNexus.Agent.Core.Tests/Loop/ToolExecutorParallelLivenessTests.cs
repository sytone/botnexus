using System.Collections.Concurrent;
using System.Text.Json;
using BotNexus.Agent.Core.Configuration;
using BotNexus.Agent.Core.Loop;
using BotNexus.Agent.Core.Tests.TestUtils;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Core.Tests.Loop;

/// <summary>
/// Regression coverage for issue #4128: one parallel tool that ignores cancellation must not
/// withhold completed siblings, wedge the batch, or contaminate later executor calls.
/// </summary>
public sealed class ToolExecutorParallelLivenessTests
{
    private static readonly TimeSpan LivenessBound = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromMilliseconds(100);

    [Fact]
    public async Task ExecuteAsync_ParallelNonCooperativeTool_FastSiblingTerminalEventIsObservableBeforeRelease()
    {
        var blocked = new NonCooperativeTool("blocked");
        var fast = new FastTool("fast", "fast-result");
        var context = new AgentContext(null, [], [blocked, fast]);
        var assistant = CreateAssistant(("blocked-call", "blocked"), ("fast-call", "fast"));
        var config = CreateParallelConfig();
        var fastTerminal = new TaskCompletionSource<ToolExecutionEndEvent>(TaskCreationOptions.RunContinuationsAsynchronously);

        var execution = ToolExecutor.ExecuteAsync(
            context,
            assistant,
            config,
            evt =>
            {
                if (evt is ToolExecutionEndEvent { ToolCallId: "fast-call" } terminal)
                {
                    fastTerminal.TrySetResult(terminal);
                }

                return Task.CompletedTask;
            },
            CancellationToken.None);

        try
        {
            await blocked.Entered.WaitAsync(LivenessBound);
            await fast.Completed.WaitAsync(LivenessBound);

            var terminal = await fastTerminal.Task.WaitAsync(LivenessBound);

            blocked.WasReleased.ShouldBeFalse();
            terminal.IsError.ShouldBeFalse();
            terminal.Result.Content.ShouldHaveSingleItem().Value.ShouldBe("fast-result");
        }
        finally
        {
            await ReleaseAndDrainAsync(blocked, execution);
        }
    }

    [Fact]
    public async Task ExecuteAsync_ParallelNonCooperativeTool_ReturnsExplicitIncompleteResultInSourceOrder()
    {
        var blocked = new NonCooperativeTool("blocked");
        var fast = new FastTool("fast", "fast-result");
        var context = new AgentContext(null, [], [blocked, fast]);
        var assistant = CreateAssistant(("blocked-call", "blocked"), ("fast-call", "fast"));
        var execution = ToolExecutor.ExecuteAsync(
            context,
            assistant,
            CreateParallelConfig(),
            _ => Task.CompletedTask,
            CancellationToken.None);

        try
        {
            await blocked.Entered.WaitAsync(LivenessBound);
            await fast.Completed.WaitAsync(LivenessBound);

            var results = await execution.WaitAsync(LivenessBound);

            results.Select(result => result.ToolCallId).ShouldBe(["blocked-call", "fast-call"]);
            results[0].IsError.ShouldBeTrue();
            results[0].Result.Content.ShouldHaveSingleItem().Value.ShouldContain("timed out");
            results[0].Result.Content.ShouldHaveSingleItem().Value.ShouldContain("did not complete");
            results[1].IsError.ShouldBeFalse();
            results[1].Result.Content.ShouldHaveSingleItem().Value.ShouldBe("fast-result");
            blocked.WasReleased.ShouldBeFalse();
        }
        finally
        {
            await ReleaseAndDrainAsync(blocked, execution);
        }
    }

    [Fact]
    public async Task ExecuteAsync_ParallelNonCooperativeTool_LateUpdateAndCompletionDoNotEmitStaleTerminalEvents()
    {
        var blocked = new NonCooperativeTool("blocked", emitUpdateAfterRelease: true);
        var fast = new FastTool("fast", "fast-result");
        var context = new AgentContext(null, [], [blocked, fast]);
        var assistant = CreateAssistant(("blocked-call", "blocked"), ("fast-call", "fast"));
        var events = new ConcurrentQueue<AgentEvent>();
        var execution = ToolExecutor.ExecuteAsync(
            context,
            assistant,
            CreateParallelConfig(),
            evt =>
            {
                events.Enqueue(evt);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        try
        {
            await blocked.Entered.WaitAsync(LivenessBound);
            await fast.Completed.WaitAsync(LivenessBound);
            _ = await execution.WaitAsync(LivenessBound);

            events.OfType<ToolExecutionEndEvent>()
                .Where(evt => evt.ToolCallId == "blocked-call")
                .ShouldHaveSingleItem()
                .IsError.ShouldBeTrue();

            blocked.Release();
            await blocked.LateUpdateInvoked.WaitAsync(LivenessBound);
            await blocked.Exited.WaitAsync(LivenessBound);

            events.OfType<ToolExecutionUpdateEvent>()
                .ShouldNotContain(evt => evt.ToolCallId == "blocked-call");
            events.OfType<ToolExecutionEndEvent>()
                .Where(evt => evt.ToolCallId == "blocked-call")
                .ShouldHaveSingleItem()
                .IsError.ShouldBeTrue();
        }
        finally
        {
            await ReleaseAndDrainAsync(blocked, execution);
        }
    }

    [Fact]
    public async Task ExecuteAsync_AfterParallelNonCooperativeTimeout_SubsequentCallSucceedsWithoutReset()
    {
        var blocked = new NonCooperativeTool("blocked");
        var fast = new FastTool("fast", "fast-result");
        var context = new AgentContext(null, [], [blocked, fast]);
        var firstExecution = ToolExecutor.ExecuteAsync(
            context,
            CreateAssistant(("blocked-call", "blocked")),
            CreateParallelConfig(),
            _ => Task.CompletedTask,
            CancellationToken.None);

        try
        {
            await blocked.Entered.WaitAsync(LivenessBound);
            var firstResults = await firstExecution.WaitAsync(LivenessBound);
            firstResults.ShouldHaveSingleItem().IsError.ShouldBeTrue();
            blocked.WasReleased.ShouldBeFalse();

            var secondResults = await ToolExecutor.ExecuteAsync(
                    context,
                    CreateAssistant(("next-call", "fast")),
                    CreateParallelConfig(),
                    _ => Task.CompletedTask,
                    CancellationToken.None)
                .WaitAsync(LivenessBound);

            secondResults.ShouldHaveSingleItem().ToolCallId.ShouldBe("next-call");
            secondResults[0].IsError.ShouldBeFalse();
            secondResults[0].Result.Content.ShouldHaveSingleItem().Value.ShouldBe("fast-result");
            blocked.WasReleased.ShouldBeFalse();
        }
        finally
        {
            await ReleaseAndDrainAsync(blocked, firstExecution);
        }
    }

    private static AgentLoopConfig CreateParallelConfig()
        => TestHelpers.CreateTestConfig(
            toolExecutionMode: ToolExecutionMode.Parallel,
            toolTimeout: ToolTimeout);

    private static AssistantAgentMessage CreateAssistant(params (string Id, string Name)[] calls)
        => new(
            string.Empty,
            calls.Select(call => new ToolCallContent(
                call.Id,
                call.Name,
                new Dictionary<string, object?>())).ToList(),
            StopReason.ToolUse);

    private static async Task ReleaseAndDrainAsync(
        NonCooperativeTool blocked,
        Task<IReadOnlyList<ToolResultAgentMessage>> execution)
    {
        blocked.Release();
        await blocked.Exited.WaitAsync(LivenessBound);
        _ = await execution.WaitAsync(LivenessBound);
    }

    private sealed class NonCooperativeTool(string name, bool emitUpdateAfterRelease = false) : IAgentTool
    {
        private static readonly JsonElement Schema = JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _lateUpdateInvoked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _wasReleased;

        public string Name => name;
        public string Label => name;
        public Tool Definition => new(name, "non-cooperative test tool", Schema);
        public Task Entered => _entered.Task;
        public Task Exited => _exited.Task;
        public Task LateUpdateInvoked => _lateUpdateInvoked.Task;
        public bool WasReleased => Volatile.Read(ref _wasReleased) != 0;

        public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken = default)
            => Task.FromResult(arguments);

        public async Task<AgentToolResult> ExecuteAsync(
            string toolCallId,
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken = default,
            AgentToolUpdateCallback? onUpdate = null)
        {
            _entered.TrySetResult();
            try
            {
                // Intentionally ignores cancellation to model a stuck provider or native operation.
                await _release.Task.ConfigureAwait(false);
                if (emitUpdateAfterRelease)
                {
                    onUpdate?.Invoke(new AgentToolResult(
                        [new AgentToolContent(AgentToolContentType.Text, "late-update")]));
                    _lateUpdateInvoked.TrySetResult();
                }

                return new AgentToolResult(
                    [new AgentToolContent(AgentToolContentType.Text, "late-completion")]);
            }
            finally
            {
                _exited.TrySetResult();
            }
        }

        public void Release()
        {
            Interlocked.Exchange(ref _wasReleased, 1);
            _release.TrySetResult();
        }
    }

    private sealed class FastTool(string name, string value) : IAgentTool
    {
        private static readonly JsonElement Schema = JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();
        private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Name => name;
        public string Label => name;
        public Tool Definition => new(name, "fast test tool", Schema);
        public Task Completed => _completed.Task;

        public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken = default)
            => Task.FromResult(arguments);

        public Task<AgentToolResult> ExecuteAsync(
            string toolCallId,
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken = default,
            AgentToolUpdateCallback? onUpdate = null)
        {
            _completed.TrySetResult();
            return Task.FromResult(new AgentToolResult(
                [new AgentToolContent(AgentToolContentType.Text, value)]));
        }
    }
}
