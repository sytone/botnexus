using System.Net;
using System.Text;
using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Extensions.Channels.Telegram;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Tests.Channels;

/// <summary>
/// Tests for the Rich Markdown (Bot API 10.1) outbound path on <see cref="TelegramChannelAdapter"/>
/// added in R2: non-streaming sends prefer <c>sendRichMessage</c> with the raw markdown passed
/// through (no MarkdownV2 escaping), and fall back to the legacy MarkdownV2/plain path when Telegram
/// rejects the rich send. Rich Markdown is on by default (<see cref="TelegramBotConfig.RichMessages"/>).
/// </summary>
public sealed class TelegramChannelAdapterRichTests
{
    [Fact]
    public async Task SendAsync_RichEnabled_SendsRichMarkdownUnescaped()
    {
        var calls = new List<CapturedCall>();
        var adapter = CreateRichAdapter(calls, richSucceeds: true);

        await adapter.SendAsync(new OutboundMessage
        {
            ChannelType = ChannelKey.From("telegram"),
            ChannelAddress = ChannelAddress.From("42"),
            // A table + bold: the whole point of Rich Markdown. None of this should be escaped.
            Content = "**Results**\n\n| Name | Age |\n| --- | --- |\n| Alice | 30 |"
        });

        var rich = calls.SingleOrDefault(c => c.Method == "sendRichMessage");
        rich.ShouldNotBeNull();
        // Legacy sendMessage must NOT be called when the rich send succeeds.
        calls.ShouldNotContain(c => c.Method == "sendMessage");

        using var json = JsonDocument.Parse(rich!.Body);
        json.RootElement.GetProperty("chat_id").GetInt64().ShouldBe(42);
        var markdown = json.RootElement.GetProperty("rich_message").GetProperty("markdown").GetString();
        markdown.ShouldNotBeNull();
        // Markdown passes through verbatim — no MarkdownV2 backslash escaping of . | * etc.
        markdown.ShouldBe("**Results**\n\n| Name | Age |\n| --- | --- |\n| Alice | 30 |");
        markdown.ShouldNotContain("\\.");
        markdown.ShouldNotContain("\\|");
    }

    [Fact]
    public async Task SendAsync_RichEnabled_WithForumTopic_RoutesThreadId()
    {
        var calls = new List<CapturedCall>();
        var adapter = CreateRichAdapter(calls, richSucceeds: true, allowedChatId: -1001234567890);

        await adapter.SendAsync(new OutboundMessage
        {
            ChannelType = ChannelKey.From("telegram"),
            ChannelAddress = ChannelAddress.From("-1001234567890/topic:67"),
            Content = "hello"
        });

        var rich = calls.Single(c => c.Method == "sendRichMessage");
        using var json = JsonDocument.Parse(rich.Body);
        json.RootElement.GetProperty("message_thread_id").GetInt32().ShouldBe(67);
    }

    [Fact]
    public async Task SendAsync_RichRejected_FallsBackToMarkdownV2()
    {
        // Simulate a rich 400 (e.g. older client). The adapter must fall back to the legacy
        // sendMessage MarkdownV2 path so the message still arrives.
        var calls = new List<CapturedCall>();
        var adapter = CreateRichAdapter(calls, richSucceeds: false);

        await adapter.SendAsync(new OutboundMessage
        {
            ChannelType = ChannelKey.From("telegram"),
            ChannelAddress = ChannelAddress.From("42"),
            Content = "**bold**"
        });

        // Rich was attempted first, then the legacy MarkdownV2 send.
        calls.ShouldContain(c => c.Method == "sendRichMessage");
        var legacy = calls.SingleOrDefault(c => c.Method == "sendMessage");
        legacy.ShouldNotBeNull();

        using var json = JsonDocument.Parse(legacy!.Body);
        // Fallback path uses MarkdownV2 conversion: **bold** -> *bold*.
        json.RootElement.GetProperty("text").GetString().ShouldBe("*bold*");
        json.RootElement.GetProperty("parse_mode").GetString().ShouldBe("MarkdownV2");
    }

    [Fact]
    public async Task SendAsync_RichDisabled_UsesLegacyPathDirectly()
    {
        var calls = new List<CapturedCall>();
        var adapter = CreateRichAdapter(calls, richSucceeds: true, richEnabled: false);

        await adapter.SendAsync(new OutboundMessage
        {
            ChannelType = ChannelKey.From("telegram"),
            ChannelAddress = ChannelAddress.From("42"),
            Content = "**bold**"
        });

        // RichMessages off -> never calls the rich endpoint.
        calls.ShouldNotContain(c => c.Method == "sendRichMessage");
        var legacy = calls.Single(c => c.Method == "sendMessage");
        using var json = JsonDocument.Parse(legacy.Body);
        json.RootElement.GetProperty("text").GetString().ShouldBe("*bold*");
    }

    // ── SplitMarkdown: line-boundary chunking ────────────────────────────────

    [Fact]
    public void SplitMarkdown_ContentUnderLimit_ReturnsSingleChunk()
    {
        var result = TelegramChannelAdapter.SplitMarkdown("line1\nline2", 100).ToList();
        result.Count.ShouldBe(1);
        result[0].ShouldBe("line1\nline2");
    }

    [Fact]
    public void SplitMarkdown_SplitsOnLineBoundaries_NotMidLine()
    {
        // Three 8-char lines; limit 20 forces a split, but never mid-line.
        var content = "AAAAAAAA\nBBBBBBBB\nCCCCCCCC"; // 8 + 1 + 8 + 1 + 8 = 26
        var result = TelegramChannelAdapter.SplitMarkdown(content, 20).ToList();
        result.Count.ShouldBeGreaterThan(1);
        // Every chunk is composed of whole original lines (no partial line).
        var rejoined = string.Join("\n", result.SelectMany(c => c.Split('\n')));
        rejoined.ShouldBe(content);
        foreach (var chunk in result)
            chunk.Length.ShouldBeLessThanOrEqualTo(20);
    }

    [Fact]
    public void SplitMarkdown_SingleLineLongerThanLimit_HardSplits()
    {
        var content = new string('x', 50);
        var result = TelegramChannelAdapter.SplitMarkdown(content, 20).ToList();
        result.Count.ShouldBe(3); // 20 + 20 + 10
        string.Concat(result).ShouldBe(content);
    }

    // ── harness ──────────────────────────────────────────────────────────────

    private static TelegramChannelAdapter CreateRichAdapter(
        List<CapturedCall> calls,
        bool richSucceeds,
        bool richEnabled = true,
        long allowedChatId = 42)
    {
        var handler = new CapturingHandler(async (request, ct) =>
        {
            var method = request.RequestUri?.Segments.LastOrDefault()?.Trim('/') ?? string.Empty;
            var body = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(ct);
            calls.Add(new CapturedCall(method, body));

            if (method == "sendRichMessage" && !richSucceeds)
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(
                        "{\"ok\":false,\"error_code\":400,\"description\":\"Bad Request: RICH_MESSAGE_UNSUPPORTED\"}",
                        Encoding.UTF8, "application/json")
                };
            }

            object result = method == "sendRichMessage" || method == "sendMessage"
                ? new TelegramMessage { MessageId = 1, Chat = new TelegramChat { Id = 42 } }
                : true;
            return JsonOk(result);
        });

        var options = new TelegramGatewayOptions
        {
            BotToken = "token",
            AllowedChatIds = { allowedChatId },
            RichMessages = richEnabled
        };
        var factory = new StubHttpClientFactory(_ => new HttpClient(handler));
        return new TelegramChannelAdapter(NullLogger<TelegramChannelAdapter>.Instance, Options.Create(options), factory);
    }

    private static HttpResponseMessage JsonOk(object result)
    {
        var payload = JsonSerializer.Serialize(new TelegramApiResponse<object> { Ok = true, Result = result });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
    }

    private sealed record CapturedCall(string Method, string Body);

    private sealed class CapturingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request, cancellationToken);
    }

    private sealed class StubHttpClientFactory(Func<string, HttpClient> factory) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => factory(name);
    }
}
