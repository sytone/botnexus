using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using BotNexus.Extensions.Channels.Telegram;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// #2386 - the Telegram polling loop retried every failure after a flat 2s sleep, forever. An
/// HTTP 409 ("terminated by other getUpdates request") is permanent for as long as the rival
/// poller lives, and produced ~4,700 ERR lines over 7 hours. These tests drive the real adapter
/// and assert the observable consequence: the loop stops calling getUpdates.
/// </summary>
public sealed class TelegramPollingLoopBoundingTests
{
    private const string Bot = "farnsworth";

    [Fact]
    public async Task Conflict409_StopsThePollingLoopInsteadOfSpinning()
    {
        var handler = new CountingTelegramHandler();
        handler.FailGetUpdatesWith(HttpStatusCode.Conflict);

        var adapter = CreateAdapter(handler);
        await adapter.StartAsync(new NoOpDispatcher(), CancellationToken.None);

        var pollingTask = adapter.GetPollingTask(Bot).ShouldNotBeNull();
        await pollingTask.WaitAsync(TimeSpan.FromSeconds(2));

        // The loop must have parked after the terminal 409. One in-flight attempt is the whole
        // budget; the pre-fix loop kept issuing getUpdates indefinitely.
        handler.GetUpdatesCalls.ShouldBe(1);

        await adapter.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task UnclassifiableFailure_FailsClosedAndStopsThePollingLoop()
    {
        var handler = new CountingTelegramHandler();
        handler.ThrowOnGetUpdates = true;

        var adapter = CreateAdapter(handler);
        await adapter.StartAsync(new NoOpDispatcher(), CancellationToken.None);

        var pollingTask = adapter.GetPollingTask(Bot).ShouldNotBeNull();
        await pollingTask.WaitAsync(TimeSpan.FromSeconds(2));

        handler.GetUpdatesCalls.ShouldBe(1);

        await adapter.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task TransientFailure_DoesNotStopTheLoopAndBacksOff()
    {
        var handler = new CountingTelegramHandler();
        handler.FailGetUpdatesWith(HttpStatusCode.BadGateway);
        handler.ParkAfterGetUpdatesCall = 2;
        var retryDelayStarted = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRetryDelay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var adapter = CreateAdapter(handler, (delay, cancellationToken) =>
        {
            retryDelayStarted.TrySetResult(delay);
            return releaseRetryDelay.Task.WaitAsync(cancellationToken);
        });
        await adapter.StartAsync(new NoOpDispatcher(), CancellationToken.None);

        var requestedDelay = await retryDelayStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        requestedDelay.ShouldBe(TimeSpan.FromSeconds(2));
        handler.GetUpdatesCalls.ShouldBe(1);

        releaseRetryDelay.TrySetResult();
        await handler.Parked.Task.WaitAsync(TimeSpan.FromSeconds(2));

        handler.GetUpdatesCalls.ShouldBe(2);

        await adapter.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task HealthyLoop_KeepsPollingUnaffected()
    {
        var handler = new CountingTelegramHandler();
        handler.ParkAfterGetUpdatesCall = 3;

        var adapter = CreateAdapter(handler);
        await adapter.StartAsync(new NoOpDispatcher(), CancellationToken.None);

        await handler.Parked.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // Success path is untouched: no breaker-imposed delay on a healthy transport.
        handler.GetUpdatesCalls.ShouldBe(3);

        await adapter.StopAsync(CancellationToken.None);
    }

    private static TelegramChannelAdapter CreateAdapter(
        CountingTelegramHandler handler,
        Func<TimeSpan, CancellationToken, Task>? retryDelay = null)
    {
        var options = new TelegramGatewayOptions();
        options.Bots[Bot] = new TelegramBotConfig { BotToken = $"token-{Bot}", PollingTimeoutSeconds = 1 };

        return new TelegramChannelAdapter(
            NullLogger<TelegramChannelAdapter>.Instance,
            Options.Create(options),
            new StubHttpClientFactory(handler),
            retryDelay: retryDelay);
    }

    private sealed class StubHttpClientFactory(CountingTelegramHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class NoOpDispatcher : IChannelDispatcher
    {
        public Task DispatchAsync(InboundMessage message, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    /// <summary>
    /// Counts getUpdates calls - the direct observable of "is the polling loop still spinning?".
    /// </summary>
    private sealed class CountingTelegramHandler : HttpMessageHandler
    {
        private int _getUpdatesCalls;

        public int GetUpdatesCalls => Volatile.Read(ref _getUpdatesCalls);
        public int? ParkAfterGetUpdatesCall { get; set; }
        public TaskCompletionSource Parked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public HttpStatusCode? GetUpdatesFailureStatus { get; private set; }

        /// <summary>When set, getUpdates throws a type the classifier does not recognise.</summary>
        public bool ThrowOnGetUpdates { get; set; }

        public void FailGetUpdatesWith(HttpStatusCode status) => GetUpdatesFailureStatus = status;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();

            if (url.EndsWith("/getUpdates", StringComparison.Ordinal))
            {
                var call = Interlocked.Increment(ref _getUpdatesCalls);

                if (call == ParkAfterGetUpdatesCall)
                {
                    Parked.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }

                if (ThrowOnGetUpdates)
                    throw new NotSupportedException("an exception nobody classified");

                if (GetUpdatesFailureStatus is { } status)
                    return new HttpResponseMessage(status)
                    {
                        Content = new StringContent("{\"ok\":false,\"description\":\"terminated by other getUpdates request\"}")
                    };

                return Ok("[]");
            }

            return Ok("true");
        }

        private static HttpResponseMessage Ok(string resultJson) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $"{{\"ok\":true,\"result\":{resultJson}}}",
                System.Text.Encoding.UTF8,
                "application/json")
        };
    }
}
