using System.Net;
using System.Text.Json.Nodes;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Streaming;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Agent.Providers.Core.Tests.Streaming;

public class CompletionsProducerCancellationTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData("openai-completions", "openai")]
    [InlineData("github-copilot-completions", "github-copilot")]
    public async Task StreamAsync_PreCanceled_CompletesOnceWithoutStartingWork(string api, string provider)
    {
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        using var handler = new CountingHandler();
        using var client = new HttpClient(handler);
        var payloads = 0;
        var stream = CompletionsStreamEngine.StreamAsync(
            Profile(api, () => payloads++), client, NullLogger.Instance, Model(api, provider),
            new Context(SystemPrompt: null, Messages: []), new StreamOptions { CancellationToken = caller.Token });

        await AssertAbortedAsync(stream, api, provider);
        payloads.ShouldBe(0);
        handler.Requests.ShouldBe(0);
    }

    [Theory]
    [InlineData("openai-completions", "openai")]
    [InlineData("github-copilot-completions", "github-copilot")]
    public async Task StreamAsync_CanceledBeforeDispatch_CompletesOnceWithoutStartingWork(string api, string provider)
    {
        using var caller = new CancellationTokenSource();
        using var handler = new CountingHandler();
        using var client = new HttpClient(handler);
        var dispatch = new HeldDispatch();
        var payloads = 0;
        var stream = CompletionsStreamEngine.StreamAsync(
            Profile(api, () => payloads++), client, NullLogger.Instance, Model(api, provider),
            new Context(SystemPrompt: null, Messages: []), new StreamOptions { CancellationToken = caller.Token }, dispatch.Schedule);

        // Schedule has captured the producer, but no worker can execute until Release is signaled.
        dispatch.Captured.ShouldBeTrue();
        caller.Cancel();
        dispatch.Release();
        await dispatch.Finished.Task.WaitAsync(Deadline);

        await AssertAbortedAsync(stream, api, provider);
        payloads.ShouldBe(0);
        handler.Requests.ShouldBe(0);
    }

    [Fact]
    public async Task StreamAsync_CanceledDuringBodyRead_CompletesOnceAndCancelsIo()
    {
        using var caller = new CancellationTokenSource();
        using var body = new CancellationReadStream();
        using var handler = new CountingHandler(body);
        using var client = new HttpClient(handler);
        var stream = CompletionsStreamEngine.StreamAsync(
            Profile("openai-completions", () => { }), client, NullLogger.Instance,
            Model("openai-completions", "openai"), new Context(SystemPrompt: null, Messages: []),
            new StreamOptions { ApiKey = "test-key", StreamIdleTimeoutMs = 0, CancellationToken = caller.Token });

        await body.ReadStarted.Task.WaitAsync(Deadline);
        caller.Cancel();
        await AssertAbortedAsync(stream, "openai-completions", "openai");
        await body.ReadCanceled.Task.WaitAsync(Deadline);
        handler.Requests.ShouldBe(1);
    }

    private static async Task AssertAbortedAsync(LlmStream stream, string api, string provider)
    {
        var eventsTask = DrainAsync(stream);
        var resultTask = stream.GetResultAsync();
        var result = await resultTask.WaitAsync(Deadline);
        var events = await eventsTask.WaitAsync(Deadline);
        var terminal = events.OfType<ErrorEvent>().ShouldHaveSingleItem();
        terminal.Reason.ShouldBe(StopReason.Aborted);
        terminal.Error.ShouldBeSameAs(result);
        events.OfType<DoneEvent>().ShouldBeEmpty();
        events.Count(e => e is ErrorEvent or DoneEvent).ShouldBe(1);
        result.StopReason.ShouldBe(StopReason.Aborted);
        result.ErrorMessage.ShouldBe("Request was cancelled");
        result.Api.ShouldBe(api);
        result.Provider.ShouldBe(provider);
        result.ModelId.ShouldBe("cancellation-model");
        (await stream.GetResultAsync()).ShouldBeSameAs(result);
    }

    private static async Task<List<AssistantMessageEvent>> DrainAsync(LlmStream stream)
    {
        var events = new List<AssistantMessageEvent>();
        await foreach (var evt in stream)
            events.Add(evt);
        return events;
    }

    private static CompletionsTransportProfile Profile(string api, Action onPayload) => new(
        Api: api,
        ActivityName: "test.completions.cancellation",
        BuildPayload: (_, _, _, _, _, _) =>
        {
            onPayload();
            return new JsonObject { ["stream"] = true };
        },
        DecorateHeaders: (_, _, _, _) => { },
        ThrowForError: (_, error, _) => throw new HttpRequestException(error));

    private static LlmModel Model(string api, string provider) => new(
        Id: "cancellation-model", Name: "Cancellation model", Api: api, Provider: provider,
        BaseUrl: "https://example.test/v1", Reasoning: false, Input: ["text"],
        Cost: new ModelCost(0, 0, 0, 0), ContextWindow: 8192, MaxTokens: 2048);

    private sealed class HeldDispatch
    {
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Captured { get; private set; }

        public Task Schedule(Func<Task> producer, CancellationToken schedulingToken)
        {
            Captured = true;
            return RunAsync(producer, schedulingToken);
        }

        public void Release() => _release.TrySetResult(true);

        private async Task RunAsync(Func<Task> producer, CancellationToken schedulingToken)
        {
            await _release.Task;
            try
            {
                await Task.Run(producer, schedulingToken);
            }
            catch (OperationCanceledException) when (schedulingToken.IsCancellationRequested)
            {
                // Observe scheduler cancellation; the returned stream must still terminalize.
            }
            finally
            {
                Finished.TrySetResult(true);
            }
        }
    }

    private sealed class CountingHandler(Stream? body = null) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = body is null ? new StringContent("data: [DONE]\n\n") : new StreamContent(body)
            });
        }
    }

    private sealed class CancellationReadStream : Stream
    {
        public TaskCompletionSource<bool> ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReadCanceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult(true);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                ReadCanceled.TrySetResult(true);
                throw;
            }
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
