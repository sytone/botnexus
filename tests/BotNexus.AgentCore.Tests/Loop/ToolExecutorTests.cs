using System.Diagnostics;
using System.Text.Json;
using BotNexus.AgentCore.Configuration;
using BotNexus.AgentCore.Hooks;
using BotNexus.AgentCore.Loop;
using BotNexus.AgentCore.Tools;
using BotNexus.AgentCore.Tests.TestUtils;
using BotNexus.AgentCore.Types;
using BotNexus.Providers.Core.Models;
using FluentAssertions;

namespace BotNexus.AgentCore.Tests.Loop;

public class ToolExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_SequentialMode_EmitsOrderedStartAndEndEvents()
    {
        var tool = new RecordingTool("echo", delayMs: 30);
        var context = new AgentContext(null, [], [tool]);
        var assistant = CreateAssistantMessage(("t1", "echo", "first"), ("t2", "echo", "second"));
        var events = new List<AgentEvent>();
        var config = TestHelpers.CreateTestConfig(toolExecutionMode: ToolExecutionMode.Sequential);

        var results = await ToolExecutor.ExecuteAsync(context, assistant, config, evt =>
        {
            events.Add(evt);
            return Task.CompletedTask;
        }, CancellationToken.None);

        results.Should().HaveCount(2);
        events.Select(evt => evt.Type).Should().Equal(
            AgentEventType.ToolExecutionStart,
            AgentEventType.ToolExecutionEnd,
            AgentEventType.MessageStart,
            AgentEventType.MessageEnd,
            AgentEventType.ToolExecutionStart,
            AgentEventType.ToolExecutionEnd,
            AgentEventType.MessageStart,
            AgentEventType.MessageEnd);
    }

    [Fact]
    public async Task ExecuteAsync_ParallelMode_RunsConcurrently()
    {
        var tool = new RecordingTool("echo", delayMs: 200);
        var context = new AgentContext(null, [], [tool]);
        var assistant = CreateAssistantMessage(("t1", "echo", "first"), ("t2", "echo", "second"));
        var config = TestHelpers.CreateTestConfig(toolExecutionMode: ToolExecutionMode.Parallel);
        var stopwatch = Stopwatch.StartNew();

        var results = await ToolExecutor.ExecuteAsync(context, assistant, config, _ => Task.CompletedTask, CancellationToken.None);
        stopwatch.Stop();

        results.Should().HaveCount(2);
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(350);
    }

    [Fact]
    public async Task ExecuteAsync_WhenToolNotFound_ReturnsErrorResult()
    {
        var context = new AgentContext(null, [], []);
        var assistant = CreateAssistantMessage(("t1", "missing_tool", "first"));
        var config = TestHelpers.CreateTestConfig();

        var results = await ToolExecutor.ExecuteAsync(context, assistant, config, _ => Task.CompletedTask, CancellationToken.None);

        results.Should().ContainSingle();
        results[0].IsError.Should().BeTrue();
        results[0].Result.Content[0].Value.Should().Contain("not registered");
    }

    [Fact]
    public async Task ExecuteAsync_BeforeToolCallCanBlockExecution()
    {
        var tool = new RecordingTool("echo");
        var context = new AgentContext(null, [], [tool]);
        var assistant = CreateAssistantMessage(("t1", "echo", "first"));
        var config = TestHelpers.CreateTestConfig(
            beforeToolCall: (_, _) => Task.FromResult<BeforeToolCallResult?>(new BeforeToolCallResult(true, "blocked")));

        var results = await ToolExecutor.ExecuteAsync(context, assistant, config, _ => Task.CompletedTask, CancellationToken.None);

        results.Should().ContainSingle();
        results[0].IsError.Should().BeTrue();
        results[0].Result.Content[0].Value.Should().Contain("blocked");
        tool.ExecuteCount.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_AfterToolCallCanModifyResult()
    {
        var tool = new RecordingTool("echo");
        var context = new AgentContext(null, [], [tool]);
        var assistant = CreateAssistantMessage(("t1", "echo", "first"));
        var modifiedContent = new AgentToolContent(AgentToolContentType.Text, "modified");
        var config = TestHelpers.CreateTestConfig(
            afterToolCall: (_, _) => Task.FromResult<AfterToolCallResult?>(new AfterToolCallResult([modifiedContent], IsError: false)));

        var results = await ToolExecutor.ExecuteAsync(context, assistant, config, _ => Task.CompletedTask, CancellationToken.None);

        results.Should().ContainSingle();
        results[0].IsError.Should().BeFalse();
        results[0].Result.Content.Should().ContainSingle().Which.Value.Should().Be("modified");
    }

    [Fact]
    public async Task ExecuteAsync_WhenCancelled_ThrowsOperationCanceledException()
    {
        var tool = new RecordingTool("echo", delayMs: 100);
        var context = new AgentContext(null, [], [tool]);
        var assistant = CreateAssistantMessage(("t1", "echo", "first"));
        var config = TestHelpers.CreateTestConfig();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var action = () => ToolExecutor.ExecuteAsync(context, assistant, config, _ => Task.CompletedTask, cts.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ExecuteAsync_WhenToolThrows_ReturnsErrorResult()
    {
        var tool = new ThrowingTool("boom_tool");
        var context = new AgentContext(null, [], [tool]);
        var assistant = CreateAssistantMessage(("t1", "boom_tool", "ignored"));
        var config = TestHelpers.CreateTestConfig();

        var results = await ToolExecutor.ExecuteAsync(context, assistant, config, _ => Task.CompletedTask, CancellationToken.None);

        results.Should().ContainSingle();
        results[0].IsError.Should().BeTrue();
        results[0].Result.Content[0].Value.Should().Contain("failed");
    }

    private static AssistantAgentMessage CreateAssistantMessage(params (string id, string name, string value)[] toolCalls)
    {
        return new AssistantAgentMessage(
            Content: string.Empty,
            ToolCalls: toolCalls.Select(call => new ToolCallContent(
                call.id,
                call.name,
                new Dictionary<string, object?> { ["value"] = call.value })).ToList(),
            FinishReason: StopReason.ToolUse);
    }

    private sealed class RecordingTool(string name, int delayMs = 0) : IAgentTool
    {
        private static readonly JsonElement Schema = JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();
        private int _executeCount;

        public string Name => name;
        public string Label => name;
        public int ExecuteCount => _executeCount;
        public Tool Definition => new(name, "test tool", Schema);

        public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(arguments);
        }

        public async Task<AgentToolResult> ExecuteAsync(
            string toolCallId,
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken = default,
            AgentToolUpdateCallback? onUpdate = null)
        {
            Interlocked.Increment(ref _executeCount);
            if (delayMs > 0)
            {
                await Task.Delay(delayMs, cancellationToken);
            }

            var value = arguments.TryGetValue("value", out var raw) ? raw?.ToString() ?? string.Empty : string.Empty;
            return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, value)]);
        }
    }

    private sealed class ThrowingTool(string name) : IAgentTool
    {
        private static readonly JsonElement Schema = JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();

        public string Name => name;
        public string Label => name;
        public Tool Definition => new(name, "throwing tool", Schema);

        public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken = default) => Task.FromResult(arguments);

        public Task<AgentToolResult> ExecuteAsync(
            string toolCallId,
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken = default,
            AgentToolUpdateCallback? onUpdate = null)
        {
            throw new InvalidOperationException("boom");
        }
    }
}
