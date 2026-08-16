using System.Threading.Channels;
using BotNexus.Agent.Core.Types;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Diagnostics;
using BotNexus.Gateway.Isolation;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Tests.Isolation;

/// <summary>
/// Issue #3230 - archiving a conversation cancels the in-flight turn's token, which is orderly
/// teardown, not a fault. These tests pin the two branches of the subscriber callback's failure
/// classification INDEPENDENTLY: a caller-initiated cancellation is control flow (Debug, no client
/// error event, no faulted channel), and everything else remains a genuine fault (Error, error
/// event, faulted channel).
/// </summary>
/// <remarks>
/// The classification is asserted through <see cref="InProcessAgentHandle.WriteAgentEventAsync"/>,
/// the exact production code path the <c>_agent.Subscribe(...)</c> callback in
/// <c>StreamCoreAsync</c> delegates to. Nothing here re-implements the guard.
/// </remarks>
public sealed class InProcessAgentHandleCancellationLoggingTests
{
    private static readonly AgentId TestAgentId = AgentId.From("agent-3230");
    private static readonly SessionId TestSessionId = SessionId.From("session-3230");

    // A mapper that always produces an event, so the WriteAsync below is what throws on a
    // signalled token - reproducing the observed line 1403 TaskCanceledException exactly.
    private static AgentStreamEvent? MapToContentDelta(AgentEvent _, string messageId)
        => new() { Type = AgentStreamEventType.ContentDelta, ContentDelta = "x", MessageId = messageId };

    [Fact]
    public async Task WriteAgentEvent_WhenCallerCancelled_LogsAtDebugAndNotError()
    {
        // AC1: with the caller's token signalled (archive/disconnect/shutdown), the callback logs
        // at Debug and emits no [ERR] line.
        var logger = new CapturingLogger<InProcessAgentHandle>();
        var channel = Channel.CreateUnbounded<AgentStreamEvent>();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await InProcessAgentHandle.WriteAgentEventAsync(
            new AgentStartEvent(DateTimeOffset.UtcNow),
            "msg-1",
            channel.Writer,
            MapToContentDelta,
            cts.Token,
            () => true,
            logger,
            TestAgentId,
            TestSessionId);

        logger.Records.ShouldNotContain(
            r => r.Level == LogLevel.Error,
            "a caller-initiated cancellation is orderly teardown and must not be logged as an error; got: "
            + string.Join(" | ", logger.Records.Select(r => $"{r.Level}:{r.Message}")));

        var debug = logger.Records.SingleOrDefault(r => r.Level == LogLevel.Debug);
        debug.ShouldNotBeNull("the cancellation must still be observable at Debug");
        debug!.Message.ShouldContain("cancelled");
        debug.Message.ShouldContain("agent-3230");
        debug.Message.ShouldContain("session-3230");
    }

    [Fact]
    public async Task WriteAgentEvent_WhenCallerCancelled_WritesNoErrorEventAndDoesNotFaultChannel()
    {
        // AC2: no synthetic "Internal streaming error" reaches the client, and the channel is not
        // faulted with TryComplete(ex) - a faulted channel is what turned the archive into a
        // client-visible error.
        var logger = new CapturingLogger<InProcessAgentHandle>();
        var channel = Channel.CreateUnbounded<AgentStreamEvent>();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await InProcessAgentHandle.WriteAgentEventAsync(
            new AgentStartEvent(DateTimeOffset.UtcNow),
            "msg-2",
            channel.Writer,
            MapToContentDelta,
            cts.Token,
            () => true,
            logger,
            TestAgentId,
            TestSessionId);

        channel.Reader.TryRead(out var written).ShouldBeFalse(
            $"no event may be written on a caller-initiated cancellation, but got: {written?.Type} / {written?.ErrorMessage}");

        // The channel must still be open and unfaulted: completing it now must succeed, which it
        // cannot if the guard had already faulted it via TryComplete(ex).
        channel.Writer.TryComplete().ShouldBeTrue("the channel must not have been faulted by the cancellation path");
        await channel.Reader.Completion.ShouldNotThrowAsync(
            "the channel completed with a fault, so the client would still see the cancellation as an error");
    }

    [Fact]
    public async Task WriteAgentEvent_WhenMapThrowsNonCancellation_StillLogsErrorAndFaultsChannel()
    {
        // AC3: the guard must not become a blanket swallow. A genuine MapAgentEvent failure keeps
        // the Error log, the client-visible error event and the faulted channel.
        var logger = new CapturingLogger<InProcessAgentHandle>();
        var channel = Channel.CreateUnbounded<AgentStreamEvent>();

        await InProcessAgentHandle.WriteAgentEventAsync(
            new AgentStartEvent(DateTimeOffset.UtcNow),
            "msg-3",
            channel.Writer,
            (_, _) => throw new InvalidOperationException("map-boom-3230"),
            CancellationToken.None,
            () => false,
            logger,
            TestAgentId,
            TestSessionId);

        var error = logger.Records.SingleOrDefault(r => r.Level == LogLevel.Error);
        error.ShouldNotBeNull("a genuine mapping failure must still be logged at Error");
        error!.Message.ShouldContain("Error processing agent event in stream");

        channel.Reader.TryRead(out var written).ShouldBeTrue("the client must still be told about a genuine fault");
        written!.Type.ShouldBe(AgentStreamEventType.Error);
        written.ErrorMessage!.ShouldContain("Internal streaming error");
        written.ErrorMessage!.ShouldContain("map-boom-3230");

        await Should.ThrowAsync<InvalidOperationException>(async () => await channel.Reader.Completion);
    }

    [Fact]
    public async Task WriteAgentEvent_WhenCancellationRaisedWithNoTokenSignalled_IsTreatedAsFault()
    {
        // AC4: the guard keys on TOKEN STATE, not on exception type. An OperationCanceledException
        // thrown while nothing was cancelled is a genuine fault and must keep the Error path -
        // this is the distinction #3116 turned on.
        var logger = new CapturingLogger<InProcessAgentHandle>();
        var channel = Channel.CreateUnbounded<AgentStreamEvent>();

        await InProcessAgentHandle.WriteAgentEventAsync(
            new AgentStartEvent(DateTimeOffset.UtcNow),
            "msg-4",
            channel.Writer,
            (_, _) => throw new TaskCanceledException("unsignalled-3230"),
            CancellationToken.None,
            () => false,
            logger,
            TestAgentId,
            TestSessionId);

        logger.Records.ShouldContain(
            r => r.Level == LogLevel.Error,
            "a cancellation-typed exception with NO token signalled is a real fault and must not be downgraded; got: "
            + string.Join(" | ", logger.Records.Select(r => $"{r.Level}:{r.Message}")));

        channel.Reader.TryRead(out var written).ShouldBeTrue();
        written!.Type.ShouldBe(AgentStreamEventType.Error);

        await Should.ThrowAsync<TaskCanceledException>(async () => await channel.Reader.Completion);
    }

    [Fact]
    public async Task WriteAgentEvent_WhenNotCancelledAndMapSucceeds_WritesMappedEvent()
    {
        // Happy path: the ordinary case is untouched by the new guard.
        var logger = new CapturingLogger<InProcessAgentHandle>();
        var channel = Channel.CreateUnbounded<AgentStreamEvent>();

        await InProcessAgentHandle.WriteAgentEventAsync(
            new AgentStartEvent(DateTimeOffset.UtcNow),
            "msg-5",
            channel.Writer,
            MapToContentDelta,
            CancellationToken.None,
            () => false,
            logger,
            TestAgentId,
            TestSessionId);

        logger.Records.ShouldBeEmpty("a successful write logs nothing");
        channel.Reader.TryRead(out var written).ShouldBeTrue();
        written!.Type.ShouldBe(AgentStreamEventType.ContentDelta);
        written.MessageId.ShouldBe("msg-5");
    }

    [Fact]
    public async Task WriteAgentEvent_WhenMapReturnsNull_WritesNothingAndLogsNothing()
    {
        // An unmapped agent event is not an error and must not reach the channel.
        var logger = new CapturingLogger<InProcessAgentHandle>();
        var channel = Channel.CreateUnbounded<AgentStreamEvent>();

        await InProcessAgentHandle.WriteAgentEventAsync(
            new AgentStartEvent(DateTimeOffset.UtcNow),
            "msg-6",
            channel.Writer,
            (_, _) => null,
            CancellationToken.None,
            () => false,
            logger,
            TestAgentId,
            TestSessionId);

        logger.Records.ShouldBeEmpty();
        channel.Reader.TryRead(out _).ShouldBeFalse();
    }
}
