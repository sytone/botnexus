using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Streaming;

namespace BotNexus.Agent.Providers.Anthropic.Tests;

public sealed class AnthropicCancellationCompletionTests
{
    [Fact]
    public async Task Stream_AlreadyCancelledToken_CompletesWithExclusiveAbortedErrorWithoutRequest()
    {
        using var cts = new CancellationTokenSource();
        using var handler = new RequestStartedHandler();
        using var client = new HttpClient(handler);
        var provider = new AnthropicProvider(client);

        // Cancellation precedes scheduling: the producer cannot win a race and start first (#4891).
        cts.Cancel();
        var stream = provider.Stream(TestHelpers.MakeModel(), TestHelpers.MakeContext(), new StreamOptions
        {
            ApiKey = "test-key",
            CancellationToken = cts.Token
        });

        try
        {
            await AssertAbortedCompletionAsync(stream, handler);
        }
        finally
        {
            handler.RequestCount.ShouldBe(0, "an already-cancelled turn must not send an HTTP request");
        }
    }

    [Fact]
    public async Task Stream_CancelAfterRequestStarted_CompletesWithExclusiveAbortedError()
    {
        using var cts = new CancellationTokenSource();
        using var handler = new RequestStartedHandler();
        using var client = new HttpClient(handler);
        var provider = new AnthropicProvider(client);
        var stream = provider.Stream(TestHelpers.MakeModel(), TestHelpers.MakeContext(), new StreamOptions
        {
            ApiKey = "test-key",
            CancellationToken = cts.Token
        });

        try
        {
            // This deadline diagnoses a missing request; it does not decide when to cancel the turn.
            await handler.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cts.Cancel();
            await AssertAbortedCompletionAsync(stream, handler);
            handler.RequestCount.ShouldBe(1);
        }
        finally
        {
            cts.Cancel();
        }
    }

    private static async Task AssertAbortedCompletionAsync(LlmStream stream, RequestStartedHandler handler)
    {
        var events = new List<AssistantMessageEvent>();
        var resultTask = stream.GetResultAsync();
        // One diagnostic budget covers both channel drain and final result, independently of the turn token.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        AssistantMessage result;
        try
        {
            await foreach (var evt in stream.WithCancellation(deadline.Token))
                events.Add(evt);
            result = await resultTask.WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException ex) when (deadline.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Anthropic cancellation did not complete within 10 seconds: events={events.Count}, " +
                $"HTTP requests={handler.RequestCount}, result status={resultTask.Status}.", ex);
        }

        var error = events.OfType<ErrorEvent>().ShouldHaveSingleItem();
        error.Reason.ShouldBe(StopReason.Aborted);
        error.Error.StopReason.ShouldBe(StopReason.Aborted);
        events.OfType<DoneEvent>().ShouldBeEmpty("a cancelled turn must not emit a success completion");
        events[^1].ShouldBeOfType<ErrorEvent>();
        result.StopReason.ShouldBe(StopReason.Aborted);
    }

    private sealed class RequestStartedHandler : HttpMessageHandler
    {
        private int _requestCount;
        public int RequestCount => Volatile.Read(ref _requestCount);
        public TaskCompletionSource<bool> RequestStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            RequestStarted.TrySetResult(true);
            // No success response is possible: only cancellation releases the in-flight request.
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The cancellation sentinel unexpectedly completed.");
        }
    }
}
