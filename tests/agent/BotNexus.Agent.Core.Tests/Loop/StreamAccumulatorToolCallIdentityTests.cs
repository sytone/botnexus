using System.Reflection;
using BotNexus.Agent.Core.Loop;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Streaming;

namespace BotNexus.Agent.Core.Tests.Loop;

/// <summary>
/// Consumer-side proof for #3290: <see cref="StreamAccumulator"/> attributes a streamed argument
/// fragment to the call the producer named, not to a call it inferred from
/// <c>ContentIndex</c>.
/// <para>
/// Before #3290 the accumulator ran <c>ResolveToolCallIdentity</c>, which indexed
/// <c>message.ToolCalls[contentIndex]</c> and, when the index ran past the end of that list, returned
/// <c>message.ToolCalls[^1]</c> - "the most recent tool call". <c>ContentIndex</c> is allocated over
/// every content block, text and thinking included, so the out-of-range branch was reachable on any
/// turn that narrated before calling a tool, and its answer was a guess. The scenario below is
/// exactly that shape.
/// </para>
/// </summary>
public class StreamAccumulatorToolCallIdentityTests
{
    /// <summary>
    /// A text block, then two concurrent tool calls at content indices 1 and 2 while occupying
    /// <c>ToolCalls</c> positions 0 and 1. Every emitted update must name the call whose fragment it
    /// carries.
    /// </summary>
    [Fact]
    public async Task AccumulateAsync_InterleavedToolCalls_AttributesEachDeltaToItsOwnCall()
    {
        var alpha = new ToolCallContent("call-alpha", "search", new Dictionary<string, object?>());
        var beta = new ToolCallContent("call-beta", "lookup", new Dictionary<string, object?>());
        var message = BuildMessage([new TextContent("thinking out loud"), alpha, beta]);

        var stream = new LlmStream();
        stream.Push(new StartEvent(message));
        stream.Push(new TextStartEvent(0, message));
        stream.Push(new TextDeltaEvent(0, "thinking out loud", message));
        stream.Push(new TextEndEvent(0, "thinking out loud", message));
        stream.Push(new ToolCallStartEvent(1, message, alpha.Id, alpha.Name));
        stream.Push(new ToolCallStartEvent(2, message, beta.Id, beta.Name));
        stream.Push(new ToolCallDeltaEvent(1, "{\"query\":\"weather\"}", message, alpha.Id, alpha.Name));
        stream.Push(new ToolCallDeltaEvent(2, "{\"id\":\"42\"}", message, beta.Id, beta.Name));
        stream.Push(new ToolCallEndEvent(1, alpha, message));
        stream.Push(new ToolCallEndEvent(2, beta, message));
        stream.Push(new DoneEvent(StopReason.ToolUse, message));
        stream.End(message);

        var updates = new List<MessageUpdateEvent>();
        await StreamAccumulator.AccumulateAsync(
            stream,
            evt =>
            {
                if (evt is MessageUpdateEvent update)
                    updates.Add(update);
                return Task.CompletedTask;
            },
            CancellationToken.None,
            [new BotNexus.Agent.Core.Types.UserMessage("prompt")]);

        var argumentUpdates = updates.Where(u => u.ArgumentsDelta is not null).ToList();
        argumentUpdates.Count.ShouldBe(2, "both argument fragments must surface as updates");

        var alphaUpdate = argumentUpdates.Where(u => u.ArgumentsDelta!.Contains("weather")).ShouldHaveSingleItem();
        alphaUpdate.ToolCallId.ShouldBe("call-alpha",
            "the 'weather' fragment belongs to call-alpha. Resolving identity from ContentIndex 1 " +
            "would read ToolCalls[1], which is call-beta - the #3290 misattribution.");
        alphaUpdate.ToolName.ShouldBe("search");

        var betaUpdate = argumentUpdates.Where(u => u.ArgumentsDelta!.Contains("42")).ShouldHaveSingleItem();
        betaUpdate.ToolCallId.ShouldBe("call-beta",
            "the 'id: 42' fragment belongs to call-beta. ContentIndex 2 is past the end of a " +
            "two-element ToolCalls list, so the old fallback returned 'the most recent call'.");
        betaUpdate.ToolName.ShouldBe("lookup");
    }

    /// <summary>
    /// Structural fence for acceptance criterion 3: <c>ResolveToolCallIdentity</c> and its
    /// <c>ToolCalls[^1]</c> fallback are deleted, not merely bypassed.
    /// <para>
    /// A behavioural test alone cannot see a dormant helper. If the guess were reintroduced as a
    /// fallback for the null case it would be invisible until the day a producer regressed to
    /// emitting null - which is precisely when a silent wrong answer is most expensive.
    /// </para>
    /// </summary>
    [Fact]
    public void StreamAccumulator_NoLongerDeclaresIndexBasedIdentityResolution()
    {
        var method = typeof(StreamAccumulator).GetMethod(
            "ResolveToolCallIdentity",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

        method.ShouldBeNull(
            "ResolveToolCallIdentity guessed a tool call's identity from ContentIndex and fell back " +
            "to ToolCalls[^1]. #3290 carries the identity on the event instead, so the guess must be " +
            "gone rather than left behind for a future caller to reach for.");
    }

    private static AssistantMessage BuildMessage(List<ContentBlock> content) => new(
        Content: content,
        Api: "test-api",
        Provider: "test-provider",
        ModelId: "test-model",
        Usage: Usage.Empty(),
        StopReason: StopReason.ToolUse,
        ErrorMessage: null,
        ResponseId: "resp_1",
        Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
}
