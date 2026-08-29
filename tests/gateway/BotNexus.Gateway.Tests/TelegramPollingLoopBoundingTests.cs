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

    /// <summary>
    /// Hang guard for signals the polling loop raises essentially immediately.
    /// </summary>
    /// <remarks>
    /// This is deliberately NOT a scheduling budget (#3583, same remedy as #3303/#3447). Every
    /// observation these tests make is already signal-gated: the fake handler parks, the polling
    /// task completes, or the injected retry-delay callback fires. A wall clock adds nothing to
    /// the assertions, and a 2-second cap on a contended 4-CPU gate container running ~17k tests
    /// was failing the wait rather than the adapter (bare `System.TimeoutException`, no Shouldly
    /// assertion message). The only remaining job for a clock is to turn a genuine dead loop into
    /// a failing test rather than a hung one, which is why the value is generous: when the test is
    /// green it is never waited on.
    /// </remarks>
    private static readonly TimeSpan PollingSignalHangGuard = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Iterations for the in-suite determinism proof. The scenario drives no real clock - the
    /// retry delay is an injected callback - so repeating it is cheap and is the only evidence
    /// that survives past the session that produced it (a hand-run loop expires immediately).
    /// </summary>
    private const int DeterminismIterations = 25;

    [Fact]
    public async Task Conflict409_StopsThePollingLoopInsteadOfSpinning()
    {
        var handler = new CountingTelegramHandler();
        handler.FailGetUpdatesWith(HttpStatusCode.Conflict);

        var adapter = CreateAdapter(handler);
        await adapter.StartAsync(new NoOpDispatcher(), CancellationToken.None);

        var pollingTask = adapter.GetPollingTask(Bot).ShouldNotBeNull();
        await pollingTask.WaitAsync(PollingSignalHangGuard);

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
        await pollingTask.WaitAsync(PollingSignalHangGuard);

        handler.GetUpdatesCalls.ShouldBe(1);

        await adapter.StopAsync(CancellationToken.None);
    }

    [Fact]
    public Task TransientFailure_DoesNotStopTheLoopAndBacksOff()
        => RunTransientFailureBackoffScenarioAsync();

    /// <summary>
    /// #3583 determinism proof, kept IN the suite rather than in a throwaway local loop: the
    /// previously-flaky scenario is replayed <see cref="DeterminismIterations"/> times and the
    /// iteration index is attached to any failure, so a residual race shows up as "failed on
    /// iteration 17" on the gate instead of as a one-in-N mystery on an unrelated PR.
    /// </summary>
    [Fact]
    public async Task TransientFailureBackoff_IsDeterministicAcrossRepeatedRuns()
    {
        for (var iteration = 1; iteration <= DeterminismIterations; iteration++)
        {
            try
            {
                await RunTransientFailureBackoffScenarioAsync();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Transient-failure backoff scenario failed on iteration {iteration} of {DeterminismIterations}: {ex.Message}",
                    ex);
            }
        }
    }

    private static async Task RunTransientFailureBackoffScenarioAsync()
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

        var requestedDelay = await retryDelayStarted.Task.WaitAsync(PollingSignalHangGuard);
        requestedDelay.ShouldBe(TimeSpan.FromSeconds(2));
        handler.GetUpdatesCalls.ShouldBe(1);

        releaseRetryDelay.TrySetResult();
        await handler.Parked.Task.WaitAsync(PollingSignalHangGuard);

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

        await handler.Parked.Task.WaitAsync(PollingSignalHangGuard);

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
