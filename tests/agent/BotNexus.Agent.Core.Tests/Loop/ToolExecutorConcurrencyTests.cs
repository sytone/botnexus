using System.Text.Json;
using BotNexus.Agent.Core.Configuration;
using BotNexus.Agent.Core.Loop;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Tests.TestUtils;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Core.Tests.Loop;

public sealed class ToolExecutorConcurrencyTests
{
    [Fact]
    [Trait("Category", "Security")]
    public async Task ParallelMode_OneToolCrashes_OthersComplete()
    {
        var tools = new IAgentTool[] { new CrashTool("crash"), new DelayTool("ok") };
        var assistant = CreateAssistant(("tc1", "crash"), ("tc2", "ok"));
        var config = TestHelpers.CreateTestConfig(toolExecutionMode: ToolExecutionMode.Parallel);
        var context = new AgentContext(null, [], tools);

        var results = await ToolExecutor.ExecuteAsync(context, assistant, config, _ => Task.CompletedTask, CancellationToken.None);

        results.Count().ShouldBe(2);
        results.Count(r => r.IsError).ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ParallelMode_AllToolsCrash_AllErrorsCollected()
    {
        var tools = new IAgentTool[] { new CrashTool("a"), new CrashTool("b"), new CrashTool("c") };
        var assistant = CreateAssistant(("tc1", "a"), ("tc2", "b"), ("tc3", "c"));
        var config = TestHelpers.CreateTestConfig(toolExecutionMode: ToolExecutionMode.Parallel);
        var context = new AgentContext(null, [], tools);

        var results = await ToolExecutor.ExecuteAsync(context, assistant, config, _ => Task.CompletedTask, CancellationToken.None);
        results.Count().ShouldBe(3);
        results.ShouldAllBe(r => r.IsError);
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Category", "SecurityGap")]
    public async Task SequentialMode_CrashDoesNotStopExecution_CurrentBehavior()
    {
        var tools = new IAgentTool[] { new CrashTool("bad"), new DelayTool("good") };
        var assistant = CreateAssistant(("tc1", "bad"), ("tc2", "good"));
        var config = TestHelpers.CreateTestConfig(toolExecutionMode: ToolExecutionMode.Sequential);
        var context = new AgentContext(null, [], tools);

        var results = await ToolExecutor.ExecuteAsync(context, assistant, config, _ => Task.CompletedTask, CancellationToken.None);
        results.Count().ShouldBe(2);
        results[0].IsError.ShouldBeTrue();
        results[1].IsError.ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Category", "SecurityGap")]
    public async Task MaxParallelismIsNotEnforced_CurrentBehavior()
    {
        const int callCount = 100;
        var tracker = new ConcurrencyTrackerTool("track", callCount);
        var calls = Enumerable.Range(0, callCount).Select(i => ($"tc{i}", "track")).ToArray();
        var assistant = CreateAssistant(calls);
        var config = TestHelpers.CreateTestConfig(toolExecutionMode: ToolExecutionMode.Parallel);
        var context = new AgentContext(null, [], [tracker]);

        var execution = ToolExecutor.ExecuteAsync(context, assistant, config, _ => Task.CompletedTask, CancellationToken.None);
        await tracker.AllCallsEntered.WaitAsync(TimeSpan.FromSeconds(10));
        tracker.ReleaseCalls();

        var results = await execution;
        results.Count().ShouldBe(callCount);
        tracker.MaxConcurrency.ShouldBe(callCount);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task SameToolCalledTwiceInParallel_ExecutesIndependently()
    {
        var tool = new ArgumentEchoTool("echo");
        var assistant = new AssistantAgentMessage(
            string.Empty,
            [
                new ToolCallContent("tc1", "echo", new Dictionary<string, object?> { ["value"] = "first" }),
                new ToolCallContent("tc2", "echo", new Dictionary<string, object?> { ["value"] = "second" })
            ],
            StopReason.ToolUse);
        var config = TestHelpers.CreateTestConfig(toolExecutionMode: ToolExecutionMode.Parallel);
        var context = new AgentContext(null, [], [tool]);

        var results = await ToolExecutor.ExecuteAsync(context, assistant, config, _ => Task.CompletedTask, CancellationToken.None);

        var values = results.Select(r => r.Result.Content[0].Value).ToList();
        values.ShouldContain("first");
        values.ShouldContain("second");
    }

    private static AssistantAgentMessage CreateAssistant(params (string id, string name)[] calls)
    {
        return new AssistantAgentMessage(
            string.Empty,
            calls.Select(c => new ToolCallContent(c.id, c.name, new Dictionary<string, object?>())).ToList(),
            StopReason.ToolUse);
    }

    private sealed class CrashTool(string name) : IAgentTool
    {
        public string Name => name;
        public string Label => name;
        public Tool Definition => new(name, name, JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone());
        public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default) => Task.FromResult(arguments);
        public Task<AgentToolResult> ExecuteAsync(string toolCallId, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default, AgentToolUpdateCallback? onUpdate = null)
            => throw new InvalidOperationException("boom");
    }

    private sealed class DelayTool(string name) : IAgentTool
    {
        public string Name => name;
        public string Label => name;
        public Tool Definition => new(name, name, JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone());
        public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default) => Task.FromResult(arguments);
        public Task<AgentToolResult> ExecuteAsync(string toolCallId, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default, AgentToolUpdateCallback? onUpdate = null)
            => Task.FromResult(new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "ok")]));
    }

    private sealed class ConcurrencyTrackerTool(string name, int expectedCalls) : IAgentTool
    {
        private int _current;
        private int _max;
        private readonly TaskCompletionSource _allCallsEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseCalls = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int MaxConcurrency => _max;
        public Task AllCallsEntered => _allCallsEntered.Task;
        public string Name => name;
        public string Label => name;
        public Tool Definition => new(name, name, JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone());
        public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default) => Task.FromResult(arguments);
        public async Task<AgentToolResult> ExecuteAsync(string toolCallId, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default, AgentToolUpdateCallback? onUpdate = null)
        {
            var now = Interlocked.Increment(ref _current);
            while (true)
            {
                var snapshot = _max;
                if (now <= snapshot || Interlocked.CompareExchange(ref _max, now, snapshot) == snapshot)
                {
                    break;
                }
            }

            if (now == expectedCalls)
                _allCallsEntered.TrySetResult();

            await _releaseCalls.Task.WaitAsync(cancellationToken);
            Interlocked.Decrement(ref _current);
            return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "ok")]);
        }

        public void ReleaseCalls() => _releaseCalls.TrySetResult();
    }

    private sealed class ArgumentEchoTool(string name) : IAgentTool
    {
        public string Name => name;
        public string Label => name;
        public Tool Definition => new(name, name, JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone());
        public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default) => Task.FromResult(arguments);
        public Task<AgentToolResult> ExecuteAsync(string toolCallId, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default, AgentToolUpdateCallback? onUpdate = null)
        {
            var value = arguments.TryGetValue("value", out var raw) ? raw?.ToString() ?? string.Empty : string.Empty;
            return Task.FromResult(new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, value)]));
        }
    }
}
