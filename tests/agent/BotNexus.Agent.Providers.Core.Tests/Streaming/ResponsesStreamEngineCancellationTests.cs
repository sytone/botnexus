using System.Net;
using System.Text.Json.Nodes;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Streaming;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Agent.Providers.Core.Tests.Streaming;

public class ResponsesStreamEngineCancellationTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task StreamAsync_AlreadyCancelled_CompletesWithSingleAbortedErrorWithoutRequestWork()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var fixture = new TransportFixture();

        var stream = fixture.Start(cancellation.Token);

        await AssertAbortedAsync(stream);
        fixture.AssertNoRequestWork();
    }

    [Fact]
    public async Task StreamAsync_CancelledBeforeProducerDispatch_CompletesWithSingleAbortedErrorWithoutRequestWork()
    {
        using var cancellation = new CancellationTokenSource();
        using var fixture = new TransportFixture();
        var scheduler = new QueuedTaskScheduler();
        var stream = fixture.Start(cancellation.Token, scheduler);
        scheduler.QueuedCount.ShouldBe(1);
        fixture.AssertNoRequestWork();

        cancellation.Cancel();
        scheduler.Dispatch();

        await AssertAbortedAsync(stream);
        fixture.AssertNoRequestWork();
    }

    [Fact]
    public async Task StreamAsync_CancelledDuringHttpSend_CancelsTransportAndCompletesWithSingleAbortedError()
    {
        using var cancellation = new CancellationTokenSource();
        using var fixture = new TransportFixture(blockHttpSend: true);
        var stream = fixture.Start(cancellation.Token);
        await fixture.Handler.SendStarted.Task.WaitAsync(Deadline);

        cancellation.Cancel();

        await fixture.Handler.CancellationObserved.Task.WaitAsync(Deadline);
        await AssertAbortedAsync(stream);
        fixture.Handler.SendCount.ShouldBe(1);
    }

    [Fact]
    public async Task StreamAsync_CancelledDuringBodyRead_CancelsBodyAndCompletesWithSingleAbortedError()
    {
        using var cancellation = new CancellationTokenSource();
        using var body = new CancellationBodyStream();
        using var fixture = new TransportFixture(body: body);
        var stream = fixture.Start(cancellation.Token);
        await body.ReadStarted.Task.WaitAsync(Deadline);

        cancellation.Cancel();

        await body.CancellationObserved.Task.WaitAsync(Deadline);
        await AssertAbortedAsync(stream);
        fixture.Handler.SendCount.ShouldBe(1);
    }

    private static async Task AssertAbortedAsync(LlmStream stream)
    {
        // Neither consumer uses the cancelled request token: the producer must close both paths.
        var result = await stream.GetResultAsync().WaitAsync(Deadline);
        var events = await DrainAsync(stream).WaitAsync(Deadline);
        var error = events.ShouldHaveSingleItem().ShouldBeOfType<ErrorEvent>();
        error.Reason.ShouldBe(StopReason.Aborted);
        error.Error.ShouldBeSameAs(result);
        result.StopReason.ShouldBe(StopReason.Aborted);
        result.ErrorMessage.ShouldBe("Request was cancelled");
        result.Content.ShouldBeEmpty();
    }

    private static async Task<List<AssistantMessageEvent>> DrainAsync(LlmStream stream)
    {
        var events = new List<AssistantMessageEvent>();
        await foreach (var item in stream)
            events.Add(item);
        return events;
    }

    private sealed class QueuedTaskScheduler : TaskScheduler
    {
        private readonly Queue<Task> _tasks = new();
        public int QueuedCount => _tasks.Count;
        protected override IEnumerable<Task> GetScheduledTasks() => _tasks.ToArray();
        protected override void QueueTask(Task task) => _tasks.Enqueue(task);
        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;

        public void Dispatch()
        {
            while (_tasks.TryDequeue(out var task))
                TryExecuteTask(task);
        }
    }

    private sealed class TransportFixture : IDisposable
    {
        private readonly HttpClient _client;
        private readonly ResponsesTransportProfile _profile;
        private int _payloadCount;
        private int _onPayloadCount;
        private int _authenticationCount;
        private static readonly LlmModel Model = new(
            Id: "cancellation-model", Name: "Cancellation model", Api: "openai-responses",
            Provider: "openai", BaseUrl: "https://example.invalid", Reasoning: false,
            Input: ["text"], Cost: new ModelCost(0, 0, 0, 0), ContextWindow: 8192, MaxTokens: 2048);

        public CancellationHandler Handler { get; }

        public TransportFixture(bool blockHttpSend = false, Stream? body = null)
        {
            Handler = new CancellationHandler(blockHttpSend, body);
            _client = new HttpClient(Handler);
            _profile = new ResponsesTransportProfile(
                Api: Model.Api,
                ActivityName: "test.responses.cancellation",
                BuildPayload: (_, _, _, _, _) =>
                {
                    _payloadCount++;
                    return new JsonObject();
                },
                Parse: async (_, reader, _, _, _, _, ct) =>
                {
                    _ = await reader.ReadLineAsync(ct);
                    throw new InvalidOperationException("Expected body cancellation");
                },
                DecorateHeaders: (_, _, _, _) => { },
                ThrowForError: (_, _, _) => throw new InvalidOperationException("Unexpected HTTP error"),
                AuthenticateRequest: (_, _) =>
                {
                    _authenticationCount++;
                    return ValueTask.CompletedTask;
                },
                ResolveApiKey: false);
        }

        public LlmStream Start(CancellationToken ct, TaskScheduler? scheduler = null)
        {
            var options = new StreamOptions
            {
                CancellationToken = ct,
                OnPayload = (payload, _) =>
                {
                    _onPayloadCount++;
                    return Task.FromResult<object?>(payload);
                }
            };
            var context = new Context(SystemPrompt: null, Messages: []);
            return scheduler is null
                ? ResponsesStreamEngine.StreamAsync(_profile, _client, NullLogger.Instance, Model, context, options)
                : ResponsesStreamEngine.StreamAsync(_profile, _client, NullLogger.Instance, Model, context, options, scheduler);
        }

        public void AssertNoRequestWork()
        {
            _payloadCount.ShouldBe(0);
            _onPayloadCount.ShouldBe(0);
            _authenticationCount.ShouldBe(0);
            Handler.SendCount.ShouldBe(0);
        }

        public void Dispose() => _client.Dispose();
    }

    private sealed class CancellationHandler(bool blockHttpSend, Stream? body) : HttpMessageHandler
    {
        public TaskCompletionSource<bool> SendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int SendCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SendCount++;
            SendStarted.TrySetResult(true);
            if (blockHttpSend)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    CancellationObserved.TrySetResult(true);
                    throw;
                }
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(body ?? new MemoryStream())
            };
        }
    }

    private sealed class CancellationBodyStream : Stream
    {
        public TaskCompletionSource<bool> ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult(true);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved.TrySetResult(true);
                throw;
            }
            return 0;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
