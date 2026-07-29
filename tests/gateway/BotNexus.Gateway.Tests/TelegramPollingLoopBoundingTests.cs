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

        // Give the loop far longer than the old flat 2s retry needed to fire repeatedly.
        var observed = await WaitForStableGetUpdatesCountAsync(handler);

        // The loop must have parked after the terminal 409. One in-flight attempt is the whole
        // budget; the pre-fix loop kept issuing getUpdates indefinitely.
        observed.ShouldBe(1);

        await adapter.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task UnclassifiableFailure_FailsClosedAndStopsThePollingLoop()
    {
        var handler = new CountingTelegramHandler();
        handler.ThrowOnGetUpdates = true;

        var adapter = CreateAdapter(handler);
        await adapter.StartAsync(new NoOpDispatcher(), CancellationToken.None);

        var observed = await WaitForStableGetUpdatesCountAsync(handler);

        observed.ShouldBe(1);

        await adapter.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task TransientFailure_DoesNotStopTheLoopAndBacksOff()
    {
        var handler = new CountingTelegramHandler();
        handler.FailGetUpdatesWith(HttpStatusCode.BadGateway);

        var adapter = CreateAdapter(handler);
        await adapter.StartAsync(new NoOpDispatcher(), CancellationToken.None);

        // First failure schedules a 2s backoff, so a second attempt lands after it. The loop
        // must still be alive - a transient upstream blip must not park the transport.
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (handler.GetUpdatesCalls < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(100);

        handler.GetUpdatesCalls.ShouldBeGreaterThanOrEqualTo(2);

        // ...but it must be BOUNDED. Without backoff the old loop would have burned far more
        // than a handful of attempts in this window.
        handler.GetUpdatesCalls.ShouldBeLessThan(8);

        await adapter.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task HealthyLoop_KeepsPollingUnaffected()
    {
        var handler = new CountingTelegramHandler();

        var adapter = CreateAdapter(handler);
        await adapter.StartAsync(new NoOpDispatcher(), CancellationToken.None);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (handler.GetUpdatesCalls < 3 && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        // Success path is untouched: no breaker-imposed delay on a healthy transport.
        handler.GetUpdatesCalls.ShouldBeGreaterThanOrEqualTo(3);

        await adapter.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// Polls until the getUpdates call count stops changing, then returns it. A parked loop
    /// settles; a spinning loop does not.
    /// </summary>
    private static async Task<int> WaitForStableGetUpdatesCountAsync(CountingTelegramHandler handler)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        var last = -1;
        var stableFor = 0;

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(200);
            var current = handler.GetUpdatesCalls;
            if (current == last)
            {
                stableFor++;
                if (stableFor >= 10)
                    return current;
            }
            else
            {
                stableFor = 0;
                last = current;
            }
        }

        return handler.GetUpdatesCalls;
    }

    private static TelegramChannelAdapter CreateAdapter(CountingTelegramHandler handler)
    {
        var options = new TelegramGatewayOptions();
        options.Bots[Bot] = new TelegramBotConfig { BotToken = $"token-{Bot}", PollingTimeoutSeconds = 1 };

        return new TelegramChannelAdapter(
            NullLogger<TelegramChannelAdapter>.Instance,
            Options.Create(options),
            new StubHttpClientFactory(handler));
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

        public HttpStatusCode? GetUpdatesFailureStatus { get; private set; }

        /// <summary>When set, getUpdates throws a type the classifier does not recognise.</summary>
        public bool ThrowOnGetUpdates { get; set; }

        public void FailGetUpdatesWith(HttpStatusCode status) => GetUpdatesFailureStatus = status;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();

            if (url.EndsWith("/getUpdates", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _getUpdatesCalls);

                if (ThrowOnGetUpdates)
                    throw new NotSupportedException("an exception nobody classified");

                if (GetUpdatesFailureStatus is { } status)
                    return Task.FromResult(new HttpResponseMessage(status)
                    {
                        Content = new StringContent("{\"ok\":false,\"description\":\"terminated by other getUpdates request\"}")
                    });

                return Task.FromResult(Ok("[]"));
            }

            return Task.FromResult(Ok("true"));
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
