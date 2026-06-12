using BotNexus.Agent.Core.Configuration;
using BotNexus.Agent.Core.Loop;
using BotNexus.Agent.Core.Tests.TestUtils;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Core.Tests.Loop;

using AgentUserMessage = BotNexus.Agent.Core.Types.UserMessage;

public class RunMetricsTests
{
    [Fact]
    public async Task RunAsync_SingleTurn_ReportsCorrectMetrics()
    {
        using var provider = RegisterProvider(TestStreamFactory.CreateTextResponse("hello"));
        var config = TestHelpers.CreateTestConfig();
        var context = TestHelpers.CreateEmptyContext();

        AgentEndEvent? endEvent = null;
        Task Emit(AgentEvent evt)
        {
            if (evt is AgentEndEvent end) endEvent = end;
            return Task.CompletedTask;
        }

        await AgentLoopRunner.RunAsync([new AgentUserMessage("hi")], context, config, Emit, CancellationToken.None);

        endEvent.ShouldNotBeNull();
        endEvent.Metrics.ShouldNotBeNull();
        endEvent.Metrics.TurnCount.ShouldBe(1);
        endEvent.Metrics.InputTokens.ShouldBe(10);
        endEvent.Metrics.OutputTokens.ShouldBe(5);
        endEvent.Metrics.ToolCallCount.ShouldBe(0);
        endEvent.Metrics.Duration.ShouldBeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public async Task RunAsync_MultiTurnWithToolCalls_AccumulatesMetrics()
    {
        var callCount = 0;
        using var provider = RegisterProvider(() =>
        {
            if (Interlocked.Increment(ref callCount) == 1)
            {
                return TestStreamFactory.CreateToolCallResponse(
                    ("call-1", "calculate", new Dictionary<string, object?> { ["expression"] = "1+1" }),
                    ("call-2", "calculate", new Dictionary<string, object?> { ["expression"] = "2+2" }));
            }

            return TestStreamFactory.CreateTextResponse("done");
        });

        var tool = new CalculateTool();
        var config = TestHelpers.CreateTestConfig();
        var context = new AgentContext(null, [], [tool]);

        AgentEndEvent? endEvent = null;
        Task Emit(AgentEvent evt)
        {
            if (evt is AgentEndEvent end) endEvent = end;
            return Task.CompletedTask;
        }

        await AgentLoopRunner.RunAsync([new AgentUserMessage("calc")], context, config, Emit, CancellationToken.None);

        endEvent.ShouldNotBeNull();
        endEvent.Metrics.ShouldNotBeNull();
        endEvent.Metrics.TurnCount.ShouldBe(2);
        endEvent.Metrics.InputTokens.ShouldBe(20); // 10 per turn × 2
        endEvent.Metrics.OutputTokens.ShouldBe(10); // 5 per turn × 2
        endEvent.Metrics.ToolCallCount.ShouldBe(2);
    }

    [Fact]
    public async Task RunAsync_ErrorResponse_StillReportsMetrics()
    {
        using var provider = RegisterProvider(TestStreamFactory.CreateErrorResponse("something broke"));
        var config = TestHelpers.CreateTestConfig();
        var context = TestHelpers.CreateEmptyContext();

        AgentEndEvent? endEvent = null;
        Task Emit(AgentEvent evt)
        {
            if (evt is AgentEndEvent end) endEvent = end;
            return Task.CompletedTask;
        }

        await AgentLoopRunner.RunAsync([new AgentUserMessage("hi")], context, config, Emit, CancellationToken.None);

        endEvent.ShouldNotBeNull();
        endEvent.Metrics.ShouldNotBeNull();
        endEvent.Metrics.TurnCount.ShouldBe(1);
        endEvent.Metrics.ToolCallCount.ShouldBe(0);
    }

    [Fact]
    public async Task RunAsync_NullUsage_TreatsAsZeroTokens()
    {
        // Create a stream with no usage info
        var stream = new BotNexus.Agent.Providers.Core.Streaming.LlmStream();
        var message = new AssistantMessage(
            Content: [new TextContent("no usage")],
            Api: "test-api",
            Provider: "test-provider",
            ModelId: "test-model",
            Usage: null!,
            StopReason: StopReason.Stop,
            ErrorMessage: null,
            ResponseId: "r1",
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        stream.Push(new BotNexus.Agent.Providers.Core.Streaming.StartEvent(message));
        stream.Push(new BotNexus.Agent.Providers.Core.Streaming.TextStartEvent(0, message));
        stream.Push(new BotNexus.Agent.Providers.Core.Streaming.TextDeltaEvent(0, "no usage", message));
        stream.Push(new BotNexus.Agent.Providers.Core.Streaming.TextEndEvent(0, "no usage", message));
        stream.Push(new BotNexus.Agent.Providers.Core.Streaming.DoneEvent(StopReason.Stop, message));
        stream.End(message);

        using var provider = RegisterProvider(stream);
        var config = TestHelpers.CreateTestConfig();
        var context = TestHelpers.CreateEmptyContext();

        AgentEndEvent? endEvent = null;
        Task Emit(AgentEvent evt)
        {
            if (evt is AgentEndEvent end) endEvent = end;
            return Task.CompletedTask;
        }

        await AgentLoopRunner.RunAsync([new AgentUserMessage("hi")], context, config, Emit, CancellationToken.None);

        endEvent.ShouldNotBeNull();
        endEvent.Metrics.ShouldNotBeNull();
        endEvent.Metrics.InputTokens.ShouldBe(0);
        endEvent.Metrics.OutputTokens.ShouldBe(0);
    }

    [Fact]
    public async Task ContinueAsync_ReportsMetrics()
    {
        using var provider = RegisterProvider(TestStreamFactory.CreateTextResponse("continued"));
        var config = TestHelpers.CreateTestConfig();
        var context = new AgentContext(null, [new AgentUserMessage("start")], []);

        AgentEndEvent? endEvent = null;
        Task Emit(AgentEvent evt)
        {
            if (evt is AgentEndEvent end) endEvent = end;
            return Task.CompletedTask;
        }

        await AgentLoopRunner.ContinueAsync(context, config, Emit, CancellationToken.None);

        endEvent.ShouldNotBeNull();
        endEvent.Metrics.ShouldNotBeNull();
        endEvent.Metrics.TurnCount.ShouldBe(1);
        endEvent.Metrics.InputTokens.ShouldBe(10);
        endEvent.Metrics.OutputTokens.ShouldBe(5);
    }

    private static IDisposable RegisterProvider(BotNexus.Agent.Providers.Core.Streaming.LlmStream stream)
    {
        var provider = new TestApiProvider("test-api", simpleStreamFactory: (_, _, _) => stream);
        return TestHelpers.RegisterProvider(provider);
    }

    private static IDisposable RegisterProvider(Func<BotNexus.Agent.Providers.Core.Streaming.LlmStream> factory)
    {
        var provider = new TestApiProvider("test-api", simpleStreamFactory: (_, _, _) => factory());
        return TestHelpers.RegisterProvider(provider);
    }
}
