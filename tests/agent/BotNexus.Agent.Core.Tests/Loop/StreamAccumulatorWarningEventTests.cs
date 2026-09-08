using BotNexus.Agent.Core.Loop;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Streaming;

namespace BotNexus.Agent.Core.Tests.Loop;

/// <summary>
/// Consumer-side proof that a non-terminal <see cref="WarningEvent"/> is handled without ending
/// accumulation (#3291, AC5).
/// </summary>
/// <remarks>
/// The accumulator's <c>switch</c> has no default arm, so an unhandled event type is silently
/// ignored and would pass a naive "did it survive" assertion. These tests therefore assert the two
/// things that distinguish handling from accident: no <c>MessageEnd</c> is emitted at the warning,
/// and the events that follow the warning are still accumulated into the final message.
/// </remarks>
public class StreamAccumulatorWarningEventTests
{
    private static AssistantMessage Message(string text, StopReason reason = StopReason.Stop) => new(
        Content: [new TextContent(text)],
        Api: "test-api",
        Provider: "test-provider",
        ModelId: "test-model",
        Usage: Usage.Empty(),
        StopReason: reason,
        ErrorMessage: null,
        ResponseId: "resp_warn",
        Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    [Fact]
    public async Task AccumulateAsync_WarningEvent_DoesNotTerminateAccumulationOrEmitMessageEnd()
    {
        var stream = new LlmStream();
        var partial = Message("");
        var completion = Message("hello world");
        var eventTypes = new List<AgentEventType>();

        stream.Push(new StartEvent(partial));
        stream.Push(new WarningEvent(WarningCodes.StreamAssemblyMismatch, "assembled/final length differ", partial));
        stream.Push(new TextDeltaEvent(0, "hello world", completion));
        stream.Push(new DoneEvent(StopReason.Stop, completion));

        var result = await StreamAccumulator.AccumulateAsync(
            stream,
            evt =>
            {
                eventTypes.Add(evt.Type);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        // Exactly one MessageEnd, and it is last: the warning produced none. Were the warning
        // treated as terminal, a MessageEnd would sit at index 1 and the text delta would never
        // have been accumulated.
        eventTypes.ShouldBe(new[]
        {
            AgentEventType.MessageStart,
            AgentEventType.MessageUpdate,
            AgentEventType.MessageEnd
        });

        result.Content.ShouldBe("hello world");
        result.FinishReason.ShouldBe(StopReason.Stop);
    }

    [Fact]
    public async Task AccumulateAsync_WarningEvent_EmitsNoGatewayEventOfItsOwn()
    {
        var stream = new LlmStream();
        var completion = Message("done");
        var eventTypes = new List<AgentEventType>();

        stream.Push(new StartEvent(completion));
        stream.Push(new WarningEvent(WarningCodes.MalformedChunkSkipped, "frame skipped", completion));
        stream.Push(new WarningEvent(WarningCodes.MalformedChunkSkipped, "frame skipped", completion));
        stream.Push(new DoneEvent(StopReason.Stop, completion));

        _ = await StreamAccumulator.AccumulateAsync(
            stream,
            evt =>
            {
                eventTypes.Add(evt.Type);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        // Two warnings produced zero additional gateway events. Presentation of a warning on a
        // channel is a separate contract (#2078); this stage adds the channel only.
        eventTypes.ShouldBe(new[] { AgentEventType.MessageStart, AgentEventType.MessageEnd });
    }

    [Fact]
    public async Task AccumulateAsync_WarningEvent_DoesNotCorruptContextMessages()
    {
        var stream = new LlmStream();
        var completion = Message("final");
        var context = new List<AgentMessage>();

        stream.Push(new StartEvent(Message("")));
        stream.Push(new WarningEvent(WarningCodes.StreamAssemblyMismatch, "mismatch", Message("")));
        stream.Push(new DoneEvent(StopReason.Stop, completion));

        _ = await StreamAccumulator.AccumulateAsync(
            stream,
            _ => Task.CompletedTask,
            CancellationToken.None,
            context);

        context.ShouldHaveSingleItem().ShouldBeOfType<AssistantAgentMessage>()
            .Content.ShouldBe("final");
    }
}
