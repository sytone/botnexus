using System.Text.Json;
using BotNexus.AgentCore.Configuration;
using BotNexus.AgentCore.Loop;
using BotNexus.AgentCore.Tools;
using BotNexus.AgentCore.Tests.TestUtils;
using BotNexus.AgentCore.Types;
using BotNexus.Providers.Core.Models;
using BotNexus.Providers.Core.Streaming;
using FluentAssertions;

namespace BotNexus.AgentCore.Tests.Loop;

using AgentUserMessage = BotNexus.AgentCore.Types.UserMessage;

public sealed class AgentLoopSafetyTests
{
    [Fact]
    [Trait("Category", "Security")]
    [Trait("Category", "SecurityGap")]
    public async Task InfiniteToolLoop_StopsOnlyByCancellation_CurrentBehavior()
    {
        using var provider = TestHelpers.RegisterProvider(new TestApiProvider(
            "loop-safety",
            simpleStreamFactory: (_, _, _) => TestStreamFactory.CreateToolCallResponse(("tc1", "echo", new Dictionary<string, object?> { ["value"] = "x" }))));

        var config = CreateConfig("loop-safety");
        var context = new AgentContext(null, [], [new EchoTool()]);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        var act = () => AgentLoopRunner.RunAsync([new AgentUserMessage("go")], context, config, _ => Task.CompletedTask, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Category", "SecurityGap")]
    public async Task RapidToolRequests_OneThousandCalls_AreAllExecuted_CurrentBehavior()
    {
        var toolCalls = Enumerable.Range(1, 1000)
            .Select(i => ($"tc{i}", "echo", new Dictionary<string, object?> { ["value"] = i.ToString() }))
            .ToArray();
        var attempt = 0;
        using var provider = TestHelpers.RegisterProvider(new TestApiProvider(
            "loop-many-tools",
            simpleStreamFactory: (_, _, _) => Interlocked.Increment(ref attempt) == 1
                ? TestStreamFactory.CreateToolCallResponse(toolCalls)
                : TestStreamFactory.CreateTextResponse("done")));
        var counterTool = new CounterTool();
        var config = CreateConfig("loop-many-tools");
        var context = new AgentContext(null, [], [counterTool]);

        var produced = await AgentLoopRunner.RunAsync([new AgentUserMessage("go")], context, config, _ => Task.CompletedTask, CancellationToken.None);

        counterTool.Count.Should().Be(1000);
        produced.OfType<ToolResultAgentMessage>().Should().HaveCount(1000);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ContextOverflowRecovery_PreservesConversationAndRetries()
    {
        var attempts = 0;
        using var provider = TestHelpers.RegisterProvider(new TestApiProvider(
            "overflow-safety",
            simpleStreamFactory: (_, _, _) =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    throw new InvalidOperationException("context length exceeded");
                }

                return TestStreamFactory.CreateTextResponse("recovered");
            }));

        var config = CreateConfig("overflow-safety");
        var context = new AgentContext(null, [], []);

        var messages = await AgentLoopRunner.RunAsync([new AgentUserMessage("hello")], context, config, _ => Task.CompletedTask, CancellationToken.None);
        messages.OfType<AssistantAgentMessage>().Last().Content.Should().Be("recovered");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task CancellationDuringToolExecution_CancelsRun()
    {
        using var provider = TestHelpers.RegisterProvider(new TestApiProvider(
            "cancel-tools",
            simpleStreamFactory: (_, _, _) => TestStreamFactory.CreateToolCallResponse(("tc1", "slow", new Dictionary<string, object?>()))));
        var config = CreateConfig("cancel-tools");
        var context = new AgentContext(null, [], [new SlowTool()]);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var act = () => AgentLoopRunner.RunAsync([new AgentUserMessage("go")], context, config, _ => Task.CompletedTask, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task CancellationDuringStreaming_StopsAndCapturesPartial()
    {
        using var provider = TestHelpers.RegisterProvider(new TestApiProvider(
            "stream-cancel",
            simpleStreamFactory: (_, _, _) =>
            {
                var stream = new LlmStream();
                var partial = CreateAssistant("partial");
                _ = Task.Run(async () =>
                {
                    stream.Push(new StartEvent(partial));
                    await Task.Delay(200);
                    stream.End(partial);
                });
                return stream;
            }));
        var config = CreateConfig("stream-cancel");
        var context = new AgentContext(null, [], []);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var act = () => AgentLoopRunner.RunAsync([new AgentUserMessage("go")], context, config, _ => Task.CompletedTask, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ToolWithSideEffectsCalledTwice_DoesNotCrash()
    {
        var attempt = 0;
        using var provider = TestHelpers.RegisterProvider(new TestApiProvider(
            "double-side-effects",
            simpleStreamFactory: (_, _, _) => Interlocked.Increment(ref attempt) == 1
                ? TestStreamFactory.CreateToolCallResponse(
                    ("tc1", "effect", new Dictionary<string, object?>()),
                    ("tc2", "effect", new Dictionary<string, object?>()))
                : TestStreamFactory.CreateTextResponse("done")));
        var sideEffectTool = new CounterTool("effect");
        var config = CreateConfig("double-side-effects");
        var context = new AgentContext(null, [], [sideEffectTool]);

        var produced = await AgentLoopRunner.RunAsync([new AgentUserMessage("go")], context, config, _ => Task.CompletedTask, CancellationToken.None);

        sideEffectTool.Count.Should().Be(2);
        produced.OfType<ToolResultAgentMessage>().Should().HaveCount(2);
    }

    private static AgentLoopConfig CreateConfig(string api)
    {
        return TestHelpers.CreateTestConfig(model: TestHelpers.CreateTestModel(api));
    }

    private static AssistantMessage CreateAssistant(string text)
    {
        return new AssistantMessage(
            [new TextContent(text)],
            "test-api",
            "test-provider",
            "test-model",
            Usage.Empty(),
            StopReason.Stop,
            null,
            "resp",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    private sealed class EchoTool : IAgentTool
    {
        private static readonly Tool DefinitionValue = new("echo", "echo", JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone());
        public string Name => "echo";
        public string Label => "echo";
        public Tool Definition => DefinitionValue;
        public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default) => Task.FromResult(arguments);
        public Task<AgentToolResult> ExecuteAsync(string toolCallId, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default, AgentToolUpdateCallback? onUpdate = null)
            => Task.FromResult(new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "ok")]));
    }

    private sealed class SlowTool : IAgentTool
    {
        private static readonly Tool DefinitionValue = new("slow", "slow", JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone());
        public string Name => "slow";
        public string Label => "slow";
        public Tool Definition => DefinitionValue;
        public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default) => Task.FromResult(arguments);
        public async Task<AgentToolResult> ExecuteAsync(string toolCallId, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default, AgentToolUpdateCallback? onUpdate = null)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "done")]);
        }
    }

    private sealed class CounterTool(string name = "echo") : IAgentTool
    {
        private readonly string _name = name;
        private int _count;
        public int Count => _count;
        public string Name => _name;
        public string Label => _name;
        public Tool Definition => new(_name, _name, JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone());
        public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default) => Task.FromResult(arguments);
        public Task<AgentToolResult> ExecuteAsync(string toolCallId, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default, AgentToolUpdateCallback? onUpdate = null)
        {
            Interlocked.Increment(ref _count);
            return Task.FromResult(new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "ok")]));
        }
    }
}
