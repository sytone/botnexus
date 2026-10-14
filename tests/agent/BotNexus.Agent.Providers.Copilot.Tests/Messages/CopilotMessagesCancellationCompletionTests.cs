using System.Net;
using System.Text;
using BotNexus.Agent.Providers.Copilot.Messages;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Streaming;

namespace BotNexus.Agent.Providers.Copilot.Tests.Messages;

public sealed class CopilotMessagesCancellationCompletionTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private const string PartialBody = "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_cancel\",\"usage\":{\"input_tokens\":7,\"output_tokens\":0}}}\n\n"
        + "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n"
        + "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"partial answer\"}}\n\n"
        + "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n";

    [Fact]
    public async Task Stream_AlreadyCanceled_CompletesAbortedWithoutRequestOrPayload()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Unexpected HTTP request"));
        using var client = new HttpClient(handler);
        var provider = new CopilotMessagesProvider(client);
        var payloadCalls = 0;
        var stream = provider.Stream(Model(), Context(), new CopilotMessagesOptions
        {
            ApiKey = "synthetic-cancellation-key", CancellationToken = cancellation.Token,
            OnPayload = (payload, _) =>
            {
                payloadCalls++;
                throw new InvalidOperationException("Canceled producer must not build a payload");
            }
        });

        var (events, result) = await DrainAsync(stream);

        AssertTerminal(events, result, StopReason.Aborted);
        events.ShouldHaveSingleItem();
        result.Content.ShouldBeEmpty();
        result.ErrorMessage.ShouldBeNull();
        payloadCalls.ShouldBe(0);
        handler.RequestCount.ShouldBe(0);
    }

    [Fact]
    public async Task Stream_CanceledWhileQueued_CompletesAbortedWithoutRequestOrPayload()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Unexpected HTTP request"));
        using var client = new HttpClient(handler);
        var queue = new ProducerQueue();
        var provider = new CopilotMessagesProvider(client) { ScheduleProducer = queue.Enqueue };
        var payloadCalls = 0;
        var stream = provider.Stream(Model(), Context(), new CopilotMessagesOptions
        {
            ApiKey = "synthetic-cancellation-key", CancellationToken = cancellation.Token,
            OnPayload = (payload, _) =>
            {
                payloadCalls++;
                throw new InvalidOperationException("Canceled producer must not build a payload");
            }
        });

        // Stream returned, but the producer is explicitly still queued, not merely racing us.
        queue.EnqueueCount.ShouldBe(1);
        stream.GetResultAsync().IsCompleted.ShouldBeFalse();
        handler.RequestCount.ShouldBe(0);
        cancellation.Cancel();
        await queue.DispatchAsync().WaitAsync(Deadline);
        var (events, result) = await DrainAsync(stream);

        AssertTerminal(events, result, StopReason.Aborted);
        events.ShouldHaveSingleItem();
        result.Content.ShouldBeEmpty();
        result.ErrorMessage.ShouldBeNull();
        payloadCalls.ShouldBe(0);
        handler.RequestCount.ShouldBe(0);
    }

    [Fact]
    public async Task Stream_CanceledDuringHttpRequest_CompletesAborted()
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler(async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Canceled request must not return");
        });
        using var client = new HttpClient(handler);
        var stream = new CopilotMessagesProvider(client).Stream(Model(), Context(), Options(cancellation.Token));
        await entered.Task.WaitAsync(Deadline);
        cancellation.Cancel();

        var (events, result) = await DrainAsync(stream);

        AssertTerminal(events, result, StopReason.Aborted);
        result.ErrorMessage.ShouldBeNull();
        handler.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task Stream_CanceledDuringBodyRead_PreservesDeliveredContentAndCompletesAborted()
    {
        using var cancellation = new CancellationTokenSource();
        using var body = new ControlledBody();
        using var handler = BodyHandler(body);
        using var client = new HttpClient(handler);
        var stream = new CopilotMessagesProvider(client).Stream(Model(), Context(), Options(cancellation.Token));
        var drain = DrainAsync(stream);
        await body.ReadBlocked.Task.WaitAsync(Deadline);
        cancellation.Cancel();

        var (events, result) = await drain;

        AssertTerminal(events, result, StopReason.Aborted);
        result.ErrorMessage.ShouldBeNull();
        result.Content.OfType<TextContent>().ShouldHaveSingleItem().Text.ShouldBe("partial answer");
        events.OfType<TextDeltaEvent>().ShouldHaveSingleItem().Delta.ShouldBe("partial answer");
        events.OfType<TextEndEvent>().ShouldHaveSingleItem();
        handler.RequestCount.ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stream_BodyFailureWithoutCancellation_CompletesErrorAndPreservesDeliveredContent(bool cancellationException)
    {
        using var body = new ControlledBody();
        using var handler = BodyHandler(body);
        using var client = new HttpClient(handler);
        var stream = new CopilotMessagesProvider(client).Stream(Model(), Context(), Options());
        var drain = DrainAsync(stream);
        await body.ReadBlocked.Task.WaitAsync(Deadline);
        body.Release.TrySetException(cancellationException
            ? new OperationCanceledException("unsolicited body cancellation")
            : new IOException("body read failed"));

        var (events, result) = await drain;

        AssertTerminal(events, result, StopReason.Error);
        result.ErrorMessage.ShouldBe(cancellationException ? "unsolicited body cancellation" : "body read failed");
        result.Content.OfType<TextContent>().ShouldHaveSingleItem().Text.ShouldBe("partial answer");
        handler.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task Stream_HttpFailure_CompletesErrorWithStatusAndBody()
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)
        { Content = new StringContent("synthetic upstream failure") }));
        using var client = new HttpClient(handler);
        var stream = new CopilotMessagesProvider(client).Stream(Model(), Context(), Options());

        var (events, result) = await DrainAsync(stream);

        AssertTerminal(events, result, StopReason.Error);
        var error = result.ErrorMessage;
        error.ShouldNotBeNull();
        error.ShouldContain("502");
        error.ShouldContain("synthetic upstream failure");
        handler.RequestCount.ShouldBe(1);
    }

    private static CopilotMessagesOptions Options(CancellationToken token = default)
        => new() { ApiKey = "synthetic-cancellation-key", CancellationToken = token };

    private static LlmModel Model() => new("claude-test", "claude-test", CopilotMessagesProvider.ApiId,
        "github-copilot", "https://example.test", false, ["text"], new ModelCost(0, 0, 0, 0), 1000, 100);
    private static Context Context() => new("system", [new UserMessage(new UserMessageContent("hello"), 1)], []);

    private static async Task<(List<AssistantMessageEvent> Events, AssistantMessage Result)> DrainAsync(LlmStream stream)
    {
        // The diagnostic deadline is independent of the operation token: cancellation must
        // complete the provider's result AND event channel, not cancel the test's consumer.
        using var deadline = new CancellationTokenSource(Deadline);
        var resultTask = stream.GetResultAsync().WaitAsync(deadline.Token);
        var events = new List<AssistantMessageEvent>();
        await foreach (var evt in stream.WithCancellation(deadline.Token))
            events.Add(evt);
        return (events, await resultTask);
    }

    private static void AssertTerminal(List<AssistantMessageEvent> events, AssistantMessage result, StopReason reason)
    {
        events.OfType<DoneEvent>().ShouldBeEmpty();
        var error = events.OfType<ErrorEvent>().ShouldHaveSingleItem();
        events.Last().ShouldBeSameAs(error);
        error.Reason.ShouldBe(reason);
        error.Error.ShouldBeSameAs(result);
        result.StopReason.ShouldBe(reason);
    }

    private static Handler BodyHandler(Stream body) => new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    { Content = new StreamContent(body) }));

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return send(request, cancellationToken);
        }
    }

    private sealed class ProducerQueue
    {
        private Func<Task>? _producer;
        private CancellationToken _token;
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int EnqueueCount { get; private set; }

        public Task Enqueue(Func<Task> producer, CancellationToken token)
        {
            EnqueueCount++;
            _producer = producer;
            _token = token;
            return _completion.Task;
        }

        public async Task DispatchAsync()
        {
            var producer = _producer ?? throw new InvalidOperationException("No queued producer");
            _producer = null;
            // Model Task.Run's cancellation gate at dispatch, without relying on thread-pool timing.
            if (_token.IsCancellationRequested)
                _completion.SetCanceled(_token);
            else
            {
                await producer();
                _completion.SetResult();
            }
        }
    }

    private sealed class ControlledBody : Stream
    {
        private readonly MemoryStream _prefix = new(Encoding.UTF8.GetBytes(PartialBody));
        public TaskCompletionSource ReadBlocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<int> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = await _prefix.ReadAsync(buffer, cancellationToken);
            if (count > 0) return count;
            ReadBlocked.TrySetResult();
            return await Release.Task.WaitAsync(cancellationToken);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) _prefix.Dispose();
            base.Dispose(disposing);
        }
    }
}
