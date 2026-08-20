using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Streaming;

namespace BotNexus.Agent.Providers.Core.Tests.Streaming;

public class LlmStreamTests
{
    private static AssistantMessage MakeMessage(
        StopReason reason = StopReason.Stop,
        string? error = null,
        IReadOnlyList<ContentBlock>? content = null) => new(
        Content: content ?? [],
        Api: "test-api",
        Provider: "test",
        ModelId: "test-model",
        Usage: Usage.Empty(),
        StopReason: reason,
        ErrorMessage: error,
        ResponseId: null,
        Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    [Fact]
    public async Task PushTextEvents_ConsumedViaAsyncEnumeration()
    {
        var stream = new LlmStream();
        var partial = MakeMessage();
        var final = MakeMessage();

        stream.Push(new TextDeltaEvent(0, "hello", partial));
        stream.Push(new DoneEvent(StopReason.Stop, final));
        stream.End(final);

        var events = new List<AssistantMessageEvent>();
        await foreach (var evt in stream)
            events.Add(evt);

        events.Count().ShouldBe(2);
        events[0].ShouldBeOfType<TextDeltaEvent>();
        events[1].ShouldBeOfType<DoneEvent>();
    }

    [Fact]
    public async Task DoneEvent_TerminatesStream()
    {
        var stream = new LlmStream();
        var final = MakeMessage();

        stream.Push(new DoneEvent(StopReason.Stop, final));

        var count = 0;
        await foreach (var _ in stream)
            count++;

        count.ShouldBe(1);
    }

    [Fact]
    public async Task ErrorEvent_TerminatesStream()
    {
        var stream = new LlmStream();
        var errorMsg = MakeMessage(StopReason.Error, "boom");

        stream.Push(new ErrorEvent(StopReason.Error, errorMsg));

        var events = new List<AssistantMessageEvent>();
        await foreach (var evt in stream)
            events.Add(evt);

        events.ShouldHaveSingleItem();
        events[0].ShouldBeOfType<ErrorEvent>();
    }

    [Fact]
    public async Task GetResultAsync_ReturnsFinalMessageOnDone()
    {
        var stream = new LlmStream();
        var final = MakeMessage();

        stream.Push(new DoneEvent(StopReason.Stop, final));

        var result = await stream.GetResultAsync();

        result.StopReason.ShouldBe(StopReason.Stop);
        result.Api.ShouldBe("test-api");
    }

    [Fact]
    public async Task GetResultAsync_ReturnsErrorMessageOnError()
    {
        var stream = new LlmStream();
        var errorMsg = MakeMessage(StopReason.Error, "failure");

        stream.Push(new ErrorEvent(StopReason.Error, errorMsg));

        var result = await stream.GetResultAsync();

        result.StopReason.ShouldBe(StopReason.Error);
        result.ErrorMessage.ShouldBe("failure");
    }

    [Fact]
    public async Task MultipleEventsInSequence_AllConsumed()
    {
        var stream = new LlmStream();
        var partial = MakeMessage();
        var final = MakeMessage(content: [new TextContent("done")]);

        stream.Push(new StartEvent(partial));
        stream.Push(new TextStartEvent(0, partial));
        stream.Push(new TextDeltaEvent(0, "hel", partial));
        stream.Push(new TextDeltaEvent(0, "lo", partial));
        stream.Push(new TextEndEvent(0, "hello", partial));
        stream.Push(new DoneEvent(StopReason.Stop, final));
        stream.End(final);

        var types = new List<string>();
        await foreach (var evt in stream)
            types.Add(evt.Type);

        types.ShouldBe(new[] { "start", "text_start", "text_delta", "text_delta", "text_end", "done" });
    }

    [Fact]
    public async Task Stream_WithCancellationToken_Cancels()
    {
        var stream = new LlmStream();
        var cts = new CancellationTokenSource();
        var partial = MakeMessage();

        stream.Push(new TextDeltaEvent(0, "data", partial));

        cts.Cancel();

        var act = async () =>
        {
            await foreach (var _ in stream.WithCancellation(cts.Token))
            {
            }
        };

        await act.ShouldThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task EmptyStream_JustDone()
    {
        var stream = new LlmStream();
        var final = MakeMessage();

        stream.Push(new DoneEvent(StopReason.Stop, final));

        var events = new List<AssistantMessageEvent>();
        await foreach (var evt in stream)
            events.Add(evt);

        events.ShouldHaveSingleItem()
            .ShouldBeOfType<DoneEvent>();
    }

    /// <summary>
    /// #3293 AC1/AC2: terminating a stream with no result must complete <c>GetResultAsync</c>.
    /// Before the fix this path (<c>End(null)</c>) completed the event channel but left the result
    /// task pending forever, so this await would never return. The bounded wait is the whole point
    /// of the test: a regression re-strands the awaiter and this fails on the timeout rather than
    /// hanging the suite.
    /// </summary>
    [Fact]
    public async Task EndWithoutResult_FaultsGetResultAsync_WithinBoundedWait()
    {
        var stream = new LlmStream();

        stream.EndWithoutResult("transport closed mid-parse");

        var resultTask = stream.GetResultAsync();
        var completed = await Task.WhenAny(resultTask, Task.Delay(TimeSpan.FromSeconds(5)));
        completed.ShouldBeSameAs(resultTask, "GetResultAsync must complete, not hang, when the stream ends with no result");

        var ex = await Should.ThrowAsync<LlmStreamIncompleteException>(() => resultTask);
        ex.Reason.ShouldBe("transport closed mid-parse");
        ex.Message.ShouldContain("ended without a result");
        ex.Message.ShouldContain("transport closed mid-parse");
    }

    /// <summary>
    /// #3293: the no-result path must also complete the event channel, so a consumer already
    /// enumerating the stream is released rather than blocked on a reader that never finishes.
    /// </summary>
    [Fact]
    public async Task EndWithoutResult_CompletesEventEnumeration()
    {
        var stream = new LlmStream();
        var partial = MakeMessage();

        stream.Push(new TextDeltaEvent(0, "partial", partial));
        stream.EndWithoutResult("aborted");

        var events = new List<AssistantMessageEvent>();
        var drain = Task.Run(async () =>
        {
            await foreach (var evt in stream)
                events.Add(evt);
        });

        var completed = await Task.WhenAny(drain, Task.Delay(TimeSpan.FromSeconds(5)));
        completed.ShouldBeSameAs(drain, "enumeration must terminate when the stream ends without a result");
        await drain;

        events.ShouldHaveSingleItem().ShouldBeOfType<TextDeltaEvent>();
    }

    /// <summary>
    /// #3293: a result already captured from a terminal event wins over a later abort. Without the
    /// <c>TrySet</c> semantics a late <c>EndWithoutResult</c> would retroactively fail a turn that
    /// had genuinely succeeded.
    /// </summary>
    [Fact]
    public async Task EndWithoutResult_AfterDoneEvent_PreservesTerminalResult()
    {
        var stream = new LlmStream();
        var final = MakeMessage(content: [new TextContent("complete")]);

        stream.Push(new DoneEvent(StopReason.Stop, final));
        stream.EndWithoutResult("late abort");

        var result = await stream.GetResultAsync();

        result.StopReason.ShouldBe(StopReason.Stop);
        result.Content.OfType<TextContent>().Single().Text.ShouldBe("complete");
    }

    /// <summary>
    /// #3293 AC1: the invalid state is unrepresentable at the type level - <c>End</c> no longer
    /// accepts a null result, and rejects one passed through a nullable reference at runtime.
    /// </summary>
    [Fact]
    public void End_WithNullResult_Throws()
    {
        var stream = new LlmStream();
        AssistantMessage? nothing = null;

        Should.Throw<ArgumentNullException>(() => stream.End(nothing!));
    }

    /// <summary>
    /// #3293: a no-result termination must name its own cause, so an empty reason is refused.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EndWithoutResult_WithBlankReason_Throws(string reason)
    {
        var stream = new LlmStream();

        Should.Throw<ArgumentException>(() => stream.EndWithoutResult(reason));
    }

    /// <summary>
    /// #3382 AC1: a stream ended while its token is signalled must NOT fault the result task with an
    /// <see cref="LlmStreamIncompleteException"/>. Nothing awaits <c>GetResultAsync</c> on a cancelled
    /// turn, so a faulted task is never observed and escapes from the finalizer thread as an
    /// <c>UnobservedTaskException</c> - which the last-chance handler renders as a fatal breadcrumb on
    /// a perfectly healthy gateway. A cancelled task is never reported as unobserved, which is the
    /// property this pins.
    /// </summary>
    [Fact]
    public async Task EndWithoutResult_WhenTokenSignalled_CancelsResultInsteadOfFaulting()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var stream = new LlmStream();

        stream.EndWithoutResult("Copilot Responses stream parse failed: The operation was canceled.", cts.Token);

        var resultTask = stream.GetResultAsync();
        var completed = await Task.WhenAny(resultTask, Task.Delay(TimeSpan.FromSeconds(5)));
        completed.ShouldBeSameAs(resultTask, "a cancelled stream must still complete its result task, not hang");

        await Should.ThrowAsync<OperationCanceledException>(() => resultTask);
        resultTask.Status.ShouldBe(
            TaskStatus.Canceled,
            "a Canceled task is exempt from unobserved-exception escalation; a Faulted one is not");
        resultTask.IsFaulted.ShouldBeFalse();
        resultTask.Exception.ShouldBeNull("no exception object may be left for the finalizer to escalate");
    }

    /// <summary>
    /// #3382 AC2: the guard keys off TOKEN STATE, not exception type. A genuine parse fault - raised
    /// with no cancellation requested - keeps the existing incomplete-result diagnostic path exactly
    /// as #3293 defined it. This is what stops the AC1 fix degenerating into a blanket swallow, and it
    /// covers the adversarial case of an <c>OperationCanceledException</c> thrown by a library while
    /// the token is quiet.
    /// </summary>
    [Fact]
    public async Task EndWithoutResult_WhenTokenNotSignalled_StillFaultsWithIncompleteException()
    {
        using var cts = new CancellationTokenSource();
        var stream = new LlmStream();

        stream.EndWithoutResult("Copilot Responses stream parse failed: malformed frame", cts.Token);

        var resultTask = stream.GetResultAsync();
        var completed = await Task.WhenAny(resultTask, Task.Delay(TimeSpan.FromSeconds(5)));
        completed.ShouldBeSameAs(resultTask);

        var ex = await Should.ThrowAsync<LlmStreamIncompleteException>(() => resultTask);
        ex.Reason.ShouldBe("Copilot Responses stream parse failed: malformed frame");
        resultTask.Status.ShouldBe(TaskStatus.Faulted, "a real fault must remain a fault");
    }

    /// <summary>
    /// #3382: the cancelled path must still release a consumer that is mid-enumeration, exactly as the
    /// fault path does. A guard that leaves the event channel open would trade a noisy breadcrumb for
    /// a hung turn.
    /// </summary>
    [Fact]
    public async Task EndWithoutResult_WhenTokenSignalled_CompletesEventEnumeration()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var stream = new LlmStream();
        stream.Push(new TextDeltaEvent(0, "partial", MakeMessage()));

        stream.EndWithoutResult("cancelled mid-parse", cts.Token);

        var events = new List<AssistantMessageEvent>();
        var drain = Task.Run(async () =>
        {
            await foreach (var evt in stream)
                events.Add(evt);
        });

        var completed = await Task.WhenAny(drain, Task.Delay(TimeSpan.FromSeconds(5)));
        completed.ShouldBeSameAs(drain, "enumeration must terminate when a cancelled stream ends");
        await drain;
        events.ShouldHaveSingleItem().ShouldBeOfType<TextDeltaEvent>();
    }

    /// <summary>
    /// #3382: a late cancellation must not retroactively cancel a turn that already produced a result,
    /// mirroring the <c>TrySet</c> guarantee the fault path already carries (#3293).
    /// </summary>
    [Fact]
    public async Task EndCancelled_AfterDoneEvent_PreservesTerminalResult()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var stream = new LlmStream();
        var final = MakeMessage(content: [new TextContent("complete")]);
        stream.Push(new DoneEvent(StopReason.Stop, final));

        stream.EndCancelled(cts.Token);

        var result = await stream.GetResultAsync();
        result.StopReason.ShouldBe(StopReason.Stop);
        result.Content.OfType<TextContent>().Single().Text.ShouldBe("complete");
    }

    /// <summary>
    /// #3382: the token-aware overload keeps the #3293 contract that a no-result termination must name
    /// its own cause. Validation runs before the cancellation branch so a blank reason is refused even
    /// on a cancelled token - the argument contract does not silently soften under cancellation.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EndWithoutResult_WithBlankReason_AndSignalledToken_StillThrows(string reason)
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var stream = new LlmStream();

        Should.Throw<ArgumentException>(() => stream.EndWithoutResult(reason, cts.Token));
    }

    [Fact]
    public async Task Stream_WithToolCallEvents_AllConsumed()
    {
        var stream = new LlmStream();
        var partial = MakeMessage();
        var toolCall = new ToolCallContent("tc1", "read_file", new Dictionary<string, object?> { ["path"] = "/tmp" });
        var final = MakeMessage(content: [toolCall]);

        stream.Push(new ToolCallStartEvent(0, partial));
        stream.Push(new ToolCallDeltaEvent(0, "{\"path\":\"/tmp\"}", partial));
        stream.Push(new ToolCallEndEvent(0, toolCall, partial));
        stream.Push(new DoneEvent(StopReason.ToolUse, final));
        stream.End(final);

        var types = new List<string>();
        await foreach (var evt in stream)
            types.Add(evt.Type);

        types.ShouldBe(new[] { "toolcall_start", "toolcall_delta", "toolcall_end", "done" });
    }

    [Fact]
    public async Task Stream_WithThinkingEvents_AllConsumed()
    {
        var stream = new LlmStream();
        var partial = MakeMessage();
        var final = MakeMessage(content: [new ThinkingContent("thought")]);

        stream.Push(new ThinkingStartEvent(0, partial));
        stream.Push(new ThinkingDeltaEvent(0, "thought", partial));
        stream.Push(new ThinkingEndEvent(0, "thought", partial));
        stream.Push(new DoneEvent(StopReason.Stop, final));
        stream.End(final);

        var types = new List<string>();
        await foreach (var evt in stream)
            types.Add(evt.Type);

        types.ShouldBe(new[] { "thinking_start", "thinking_delta", "thinking_end", "done" });
    }
}
