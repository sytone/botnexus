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
/// #2447 defect 2 - the booby trap. <c>TelegramChannelAdapter.OnStartAsync</c> starts configured
/// bots in SEQUENCE. In the observed incident bot <c>farnsworth</c> had already completed
/// <c>deleteWebhook</c> and entered its polling loop before bot <c>keel</c>'s call threw 502. The
/// exception aborted <c>OnStartAsync</c>, the host recorded the adapter as FAILED, yet
/// farnsworth's poller kept issuing <c>getUpdates</c> for minutes afterwards.
///
/// <para>
/// Naively retrying that start would launch a SECOND poller on an already-live bot token - the
/// duplicate-poller condition behind the 2026-07-24 HTTP 409 storm ("terminated by other
/// getUpdates request", ~4,700 errors over 7 hours). These tests assert the retry is resumable:
/// it starts only the bots that never got going.
/// </para>
/// </summary>
public sealed class TelegramAdapterPartialStartTests
{
    private const string LiveBot = "farnsworth";
    private const string FailingBot = "keel";

    [Fact]
    public async Task RetryAfterPartialStart_DoesNotStartASecondPollerOnTheAlreadyLiveBot()
    {
        var handler = new ScriptedTelegramHandler();

        // First attempt: farnsworth's deleteWebhook succeeds (its poller goes live), keel's throws
        // 502 - exactly the incident.
        handler.FailDeleteWebhookFor(FailingBot, HttpStatusCode.BadGateway, times: 1);

        var adapter = CreateAdapter(handler);

        await Should.ThrowAsync<HttpRequestException>(
            () => adapter.StartAsync(new NoOpDispatcher(), CancellationToken.None));

        handler.DeleteWebhookCalls(LiveBot).ShouldBe(1);
        handler.DeleteWebhookCalls(FailingBot).ShouldBe(1);

        // The retry. Upstream has recovered, so keel now succeeds.
        await adapter.StartAsync(new NoOpDispatcher(), CancellationToken.None);

        // THE ASSERTION THAT MATTERS: the already-live bot was NOT started again. A second
        // deleteWebhook here means a second getUpdates poller on a live token - the 409 storm.
        handler.DeleteWebhookCalls(LiveBot).ShouldBe(1);

        // ...while the bot that never got going did start on the retry.
        handler.DeleteWebhookCalls(FailingBot).ShouldBe(2);

        adapter.IsRunning.ShouldBeTrue();

        await adapter.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RepeatedSuccessfulStart_DoesNotRestartAnyBot()
    {
        var handler = new ScriptedTelegramHandler();
        var adapter = CreateAdapter(handler);

        await adapter.StartAsync(new NoOpDispatcher(), CancellationToken.None);
        await adapter.StartAsync(new NoOpDispatcher(), CancellationToken.None);

        handler.DeleteWebhookCalls(LiveBot).ShouldBe(1);
        handler.DeleteWebhookCalls(FailingBot).ShouldBe(1);

        await adapter.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopThenStart_StartsBotsAgain()
    {
        // The latch must be released by Stop, otherwise a legitimate restart is silently a no-op.
        var handler = new ScriptedTelegramHandler();
        var adapter = CreateAdapter(handler);

        await adapter.StartAsync(new NoOpDispatcher(), CancellationToken.None);
        await adapter.StopAsync(CancellationToken.None);
        await adapter.StartAsync(new NoOpDispatcher(), CancellationToken.None);

        handler.DeleteWebhookCalls(LiveBot).ShouldBe(2);

        await adapter.StopAsync(CancellationToken.None);
    }

    private static TelegramChannelAdapter CreateAdapter(ScriptedTelegramHandler handler)
    {
        var options = new TelegramGatewayOptions();

        // Ordered so the live bot is started BEFORE the failing one, reproducing the incident.
        options.Bots[LiveBot] = new TelegramBotConfig { BotToken = $"token-{LiveBot}", PollingTimeoutSeconds = 1 };
        options.Bots[FailingBot] = new TelegramBotConfig { BotToken = $"token-{FailingBot}", PollingTimeoutSeconds = 1 };

        return new TelegramChannelAdapter(
            NullLogger<TelegramChannelAdapter>.Instance,
            Options.Create(options),
            new StubHttpClientFactory(handler));
    }

    private sealed class StubHttpClientFactory(ScriptedTelegramHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class NoOpDispatcher : IChannelDispatcher
    {
        public Task DispatchAsync(InboundMessage message, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    /// <summary>
    /// Minimal fake Telegram Bot API. Counts <c>deleteWebhook</c> per bot token (the observable
    /// proxy for "a poller was started for this bot") and can be scripted to fail for one bot.
    /// </summary>
    private sealed class ScriptedTelegramHandler : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, int> _deleteWebhookCalls = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, (HttpStatusCode Status, int Remaining)> _scriptedFailures = new(StringComparer.Ordinal);

        public void FailDeleteWebhookFor(string botName, HttpStatusCode status, int times)
            => _scriptedFailures[botName] = (status, times);

        public int DeleteWebhookCalls(string botName)
            => _deleteWebhookCalls.TryGetValue(botName, out var n) ? n : 0;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            var botName = ExtractBotName(url);

            if (url.EndsWith("/deleteWebhook", StringComparison.Ordinal))
            {
                _deleteWebhookCalls.AddOrUpdate(botName, 1, (_, n) => n + 1);

                if (_scriptedFailures.TryGetValue(botName, out var failure) && failure.Remaining > 0)
                {
                    _scriptedFailures[botName] = (failure.Status, failure.Remaining - 1);
                    return Task.FromResult(new HttpResponseMessage(failure.Status)
                    {
                        Content = new StringContent("Bad Gateway")
                    });
                }

                return Task.FromResult(Ok("true"));
            }

            // getUpdates and anything else: return an empty successful result so a live polling
            // loop keeps spinning harmlessly without touching the network.
            return Task.FromResult(Ok("[]"));
        }

        private static HttpResponseMessage Ok(string resultJson) => new(HttpStatusCode.OK)
        {
            Content = new StringContent($"{{\"ok\":true,\"result\":{resultJson}}}", System.Text.Encoding.UTF8, "application/json")
        };

        // Token shape is "token-<botName>"; the URL is https://api.telegram.org/bot<token>/<method>
        private static string ExtractBotName(string url)
        {
            const string marker = "/bottoken-";
            var start = url.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0)
                return "unknown";

            start += marker.Length;
            var end = url.IndexOf('/', start);
            return end < 0 ? url[start..] : url[start..end];
        }
    }
}
