using System.Net;
using System.Text;
using System.Text.Json;
using BotNexus.Extensions.Channels.Telegram;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests.Channels;

/// <summary>
/// Request-shape tests for the Telegram Rich Message (Bot API 10.1) methods on
/// <see cref="TelegramBotApiClient"/>: <c>sendRichMessage</c>, <c>sendRichMessageDraft</c>,
/// and the rich <c>editMessageText</c>. These assert the exact JSON Telegram receives —
/// the entire contract at the API-client layer — plus the markdown/plain fallback signalling.
/// </summary>
public sealed class TelegramBotApiClientRichTests
{
    private const string Token = "test-token";

    // ─── sendRichMessage ─────────────────────────────────────────────────────

    [Fact]
    public async Task SendRichMessageAsync_PostsToSendRichMessage_WithMarkdownField()
    {
        CapturedRequest? captured = null;
        var client = CreateClient((req, body) => { captured = new CapturedRequest(req.RequestUri!, body); return JsonOk(new TelegramMessage { MessageId = 7 }); });

        var result = await client.SendRichMessageAsync(42, "**bold** and a | table |");

        result.MessageId.ShouldBe(7);
        captured.ShouldNotBeNull();
        captured!.Uri.AbsoluteUri.ShouldEndWith("/sendRichMessage");

        using var json = JsonDocument.Parse(captured.Body);
        json.RootElement.GetProperty("chat_id").GetInt64().ShouldBe(42);
        // markdown goes inside the rich_message object — sent nearly as-is, no MarkdownV2 escaping.
        var rich = json.RootElement.GetProperty("rich_message");
        rich.GetProperty("markdown").GetString().ShouldBe("**bold** and a | table |");
        // Only the markdown field is present; null html/is_rtl/skip_entity_detection are omitted.
        rich.TryGetProperty("html", out _).ShouldBeFalse();
        rich.TryGetProperty("is_rtl", out _).ShouldBeFalse();
        rich.TryGetProperty("skip_entity_detection", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task SendRichMessageAsync_WithForumTopic_IncludesMessageThreadId()
    {
        CapturedRequest? captured = null;
        var client = CreateClient((req, body) => { captured = new CapturedRequest(req.RequestUri!, body); return JsonOk(new TelegramMessage { MessageId = 1 }); });

        await client.SendRichMessageAsync(42, "hi", messageThreadId: 67);

        using var json = JsonDocument.Parse(captured!.Body);
        json.RootElement.GetProperty("message_thread_id").GetInt32().ShouldBe(67);
    }

    [Fact]
    public async Task SendRichMessageAsync_GeneralTopic_OmitsMessageThreadId()
    {
        // threadId == 1 is the forum "General" topic; Telegram rejects an explicit message_thread_id=1.
        CapturedRequest? captured = null;
        var client = CreateClient((req, body) => { captured = new CapturedRequest(req.RequestUri!, body); return JsonOk(new TelegramMessage { MessageId = 1 }); });

        await client.SendRichMessageAsync(42, "hi", messageThreadId: 1);

        using var json = JsonDocument.Parse(captured!.Body);
        json.RootElement.TryGetProperty("message_thread_id", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task SendRichMessageAsync_On400_ThrowsMarkdownParseException_ForFallback()
    {
        // A 400 (e.g. recipient client predates Bot API 10.1) must surface as the markdown-parse
        // signal so the adapter can fall back to the legacy MarkdownV2/plain path.
        var client = CreateClient((_, _) => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"ok\":false,\"error_code\":400,\"description\":\"Bad Request: RICH_MESSAGE_UNSUPPORTED\"}", Encoding.UTF8, "application/json")
        });

        await Should.ThrowAsync<TelegramMarkdownParseException>(
            async () => await client.SendRichMessageAsync(42, "**bold**"));
    }

    // ─── sendRichMessageDraft ────────────────────────────────────────────────

    [Fact]
    public async Task SendRichMessageDraftAsync_PostsDraft_WithDraftIdAndMarkdown()
    {
        CapturedRequest? captured = null;
        var client = CreateClient((req, body) => { captured = new CapturedRequest(req.RequestUri!, body); return JsonOk(true); });

        var ok = await client.SendRichMessageDraftAsync(42, draftId: 99, markdown: "partial **bo");

        ok.ShouldBeTrue();
        captured!.Uri.AbsoluteUri.ShouldEndWith("/sendRichMessageDraft");

        using var json = JsonDocument.Parse(captured.Body);
        json.RootElement.GetProperty("chat_id").GetInt64().ShouldBe(42);
        json.RootElement.GetProperty("draft_id").GetInt64().ShouldBe(99);
        json.RootElement.GetProperty("rich_message").GetProperty("markdown").GetString().ShouldBe("partial **bo");
    }

    [Fact]
    public async Task SendRichMessageDraftAsync_On400_ThrowsMarkdownParseException_ForFallback()
    {
        var client = CreateClient((_, _) => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"ok\":false,\"error_code\":400,\"description\":\"Bad Request\"}", Encoding.UTF8, "application/json")
        });

        await Should.ThrowAsync<TelegramMarkdownParseException>(
            async () => await client.SendRichMessageDraftAsync(42, 1, "x"));
    }

    // ─── editMessageText (rich_message) ──────────────────────────────────────

    [Fact]
    public async Task EditMessageRichAsync_PostsEditWithRichMessage()
    {
        CapturedRequest? captured = null;
        var client = CreateClient((req, body) => { captured = new CapturedRequest(req.RequestUri!, body); return JsonOk(new TelegramMessage { MessageId = 5 }); });

        var result = await client.EditMessageRichAsync(42, messageId: 5, markdown: "# Heading");

        result.MessageId.ShouldBe(5);
        captured!.Uri.AbsoluteUri.ShouldEndWith("/editMessageText");

        using var json = JsonDocument.Parse(captured.Body);
        json.RootElement.GetProperty("message_id").GetInt32().ShouldBe(5);
        json.RootElement.GetProperty("rich_message").GetProperty("markdown").GetString().ShouldBe("# Heading");
        // Rich edit uses the rich_message field, not text/parse_mode.
        json.RootElement.TryGetProperty("text", out _).ShouldBeFalse();
        json.RootElement.TryGetProperty("parse_mode", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task EditMessageRichAsync_MessageNotModified_ReturnsNoOpSuccess()
    {
        // "message is not modified" during streaming is benign — treated as a no-op success,
        // returning the same message id rather than throwing.
        var client = CreateClient((_, _) => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"ok\":false,\"error_code\":400,\"description\":\"Bad Request: message is not modified\"}", Encoding.UTF8, "application/json")
        });

        var result = await client.EditMessageRichAsync(42, messageId: 13, markdown: "same");

        result.MessageId.ShouldBe(13);
    }

    // ─── harness ─────────────────────────────────────────────────────────────

    private static TelegramBotApiClient CreateClient(Func<HttpRequestMessage, string, HttpResponseMessage> responder)
    {
        var handler = new CapturingHandler(responder);
        var http = new HttpClient(handler);
        return new TelegramBotApiClient(http, Token, NullLogger.Instance);
    }

    private static HttpResponseMessage JsonOk<T>(T result)
    {
        var payload = JsonSerializer.Serialize(new TelegramApiResponse<T> { Ok = true, Result = result });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
    }

    private sealed record CapturedRequest(Uri Uri, string Body);

    private sealed class CapturingHandler(Func<HttpRequestMessage, string, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);
            return responder(request, body);
        }
    }
}
