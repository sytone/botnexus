using System.Net;
using System.Text;
using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Extensions.Channels.Telegram;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Tests.Channels;

/// <summary>
/// Tests for rendering <c>ask_user</c> prompts on Telegram with inline keyboards, and for handling
/// the resulting callback queries (#2323).
/// </summary>
/// <remarks>
/// The load-bearing invariants pinned here are: (1) the prompt is actually SENT (before this change
/// the <c>UserInputRequired</c> event fell through the switch and was silently discarded);
/// (2) callback data stays within Telegram's 64-<b>byte</b> cap; (3) resolution goes through
/// <see cref="IAskUserPromptResolver"/> only, exactly once, even on a double-tap; and
/// (4) a callback from an unauthorized chat or user is rejected, not merely ignored.
/// </remarks>
public sealed class TelegramAskUserKeyboardTests
{
    private const long AllowedChat = 42;
    private const string RequestId = "0123456789abcdef0123456789abcdef"; // Guid "N" shape

    // ── AC1/AC2: the prompt is rendered and sent with an inline keyboard ─────

    [Fact]
    public async Task UserInputRequired_WithChoices_SendsVisiblePromptWithInlineKeyboard()
    {
        var calls = new List<CapturedCall>();
        var adapter = CreateAdapter(calls, new RecordingResolver());

        await adapter.SendStreamEventAsync(
            StreamTargets.For(AllowedChat.ToString()),
            AskEvent("Pick a deployment ring", ("canary", "Canary"), ("prod", "Production")));

        var send = calls.Single(c => c.Method == "sendMessage");
        using var json = JsonDocument.Parse(send.Body);
        json.RootElement.GetProperty("text").GetString()!.ShouldContain("Pick a deployment ring");

        var rows = json.RootElement.GetProperty("reply_markup").GetProperty("inline_keyboard");
        var buttons = rows.EnumerateArray().SelectMany(r => r.EnumerateArray()).ToList();
        buttons.Count.ShouldBe(2);
        buttons.Select(b => b.GetProperty("text").GetString()).ShouldBe(new[] { "Canary", "Production" });
    }

    [Fact]
    public async Task UserInputRequired_NoChoices_StillSendsThePromptText()
    {
        var calls = new List<CapturedCall>();
        var adapter = CreateAdapter(calls, new RecordingResolver());

        await adapter.SendStreamEventAsync(
            StreamTargets.For(AllowedChat.ToString()),
            AskEvent("What is the ticket number?"));

        var send = calls.Single(c => c.Method == "sendMessage");
        using var json = JsonDocument.Parse(send.Body);
        json.RootElement.GetProperty("text").GetString()!.ShouldContain("What is the ticket number?");
        json.RootElement.TryGetProperty("reply_markup", out _).ShouldBeFalse();
    }

    // ── AC5: 64-BYTE callback data cap ──────────────────────────────────────

    [Fact]
    public async Task UserInputRequired_LargeChoiceSet_EveryCallbackDataStaysWithin64Bytes()
    {
        var calls = new List<CapturedCall>();
        var adapter = CreateAdapter(calls, new RecordingResolver());

        // Deliberately long, multi-byte labels: if the implementation ever carried choice TEXT in
        // callback_data instead of an index, this is where it would blow the cap.
        var choices = Enumerable.Range(0, TelegramAskUserPromptRenderer.MaxKeyboardChoices)
            .Select(i => ($"value-{i}", $"\u00c9metteur \u2014 option n\u00b0{i} avec un libell\u00e9 tr\u00e8s long \U0001F680"))
            .ToArray();

        await adapter.SendStreamEventAsync(
            StreamTargets.For(AllowedChat.ToString()),
            AskEvent("Choose", choices));

        var send = calls.Single(c => c.Method == "sendMessage");
        using var json = JsonDocument.Parse(send.Body);
        var buttons = json.RootElement.GetProperty("reply_markup").GetProperty("inline_keyboard")
            .EnumerateArray().SelectMany(r => r.EnumerateArray()).ToList();

        buttons.Count.ShouldBe(choices.Length);
        foreach (var button in buttons)
        {
            var data = button.GetProperty("callback_data").GetString()!;
            // BYTES, not chars. Telegram measures the UTF-8 encoding.
            Encoding.UTF8.GetByteCount(data).ShouldBeLessThanOrEqualTo(64);
        }
    }

    [Fact]
    public void CallbackToken_RequestIdTooLongToFit_IsRefusedRatherThanTruncated()
    {
        // A truncated token would decode to the wrong request (or not at all). Refusing is what lets
        // the renderer degrade the whole prompt to a numbered text list.
        var oversizedId = new string('x', 80);
        TelegramAskUserCallbackToken.TryEncode(oversizedId, 0, out var data).ShouldBeFalse();
        data.ShouldBeEmpty();
    }

    [Fact]
    public void CallbackToken_RoundTrips()
    {
        TelegramAskUserCallbackToken.TryEncode(RequestId, 7, out var data).ShouldBeTrue();
        TelegramAskUserCallbackToken.TryDecode(data, out var id, out var index).ShouldBeTrue();
        id.ShouldBe(RequestId);
        index.ShouldBe(7);
    }

    [Fact]
    public void CallbackToken_ForeignPayload_IsRejected()
    {
        TelegramAskUserCallbackToken.TryDecode("something-else", out _, out _).ShouldBeFalse();
        TelegramAskUserCallbackToken.TryDecode(null, out _, out _).ShouldBeFalse();
        TelegramAskUserCallbackToken.TryDecode("au:abc:", out _, out _).ShouldBeFalse();
        TelegramAskUserCallbackToken.TryDecode("au:abc:-1", out _, out _).ShouldBeFalse();
    }

    // ── AC7: long choice lists degrade gracefully ───────────────────────────

    [Fact]
    public async Task UserInputRequired_TooManyChoices_DegradesToNumberedTextListNotAFailedSend()
    {
        var calls = new List<CapturedCall>();
        var adapter = CreateAdapter(calls, new RecordingResolver());

        var choices = Enumerable.Range(0, TelegramAskUserPromptRenderer.MaxKeyboardChoices + 1)
            .Select(i => ($"v{i}", $"Choice {i}"))
            .ToArray();

        await adapter.SendStreamEventAsync(
            StreamTargets.For(AllowedChat.ToString()),
            AskEvent("Pick one", choices));

        var send = calls.Single(c => c.Method == "sendMessage");
        using var json = JsonDocument.Parse(send.Body);
        json.RootElement.TryGetProperty("reply_markup", out _).ShouldBeFalse();

        var text = json.RootElement.GetProperty("text").GetString()!;
        text.ShouldContain("Choice 0");
        text.ShouldContain($"Choice {choices.Length - 1}");
    }

    // ── AC6/AC8: resolution goes through the resolver, carrying the VALUE ────

    [Fact]
    public async Task CallbackQuery_ResolvesThroughThePromptResolverWithTheChoiceValue()
    {
        var calls = new List<CapturedCall>();
        var resolver = new RecordingResolver();
        var adapter = CreateAdapter(calls, resolver);

        await adapter.SendStreamEventAsync(
            StreamTargets.For(AllowedChat.ToString()),
            AskEvent("Pick a ring", ("canary", "Canary"), ("prod", "Production")));

        await PressAsync(adapter, choiceIndex: 1);

        resolver.Submissions.Count.ShouldBe(1);
        var submission = resolver.Submissions[0];
        // The stable VALUE, not the display label - the tool result must be machine-readable.
        submission.SelectedValues.ShouldBe(new[] { "prod" });
        submission.RequestId.ShouldBe(RequestId);
        submission.OriginChannel!.Value.Value.ShouldBe("telegram");
        submission.Cancelled.ShouldBeFalse();

        // Telegram requires acknowledgement or the button spins forever.
        calls.ShouldContain(c => c.Method == "answerCallbackQuery");
    }

    // ── AC9: idempotency - a double tap resolves exactly once ───────────────

    [Fact]
    public async Task CallbackQuery_DoubleTap_SubmitsToTheResolverExactlyOnce()
    {
        var calls = new List<CapturedCall>();
        var resolver = new RecordingResolver();
        var adapter = CreateAdapter(calls, resolver);

        await adapter.SendStreamEventAsync(
            StreamTargets.For(AllowedChat.ToString()),
            AskEvent("Pick a ring", ("canary", "Canary"), ("prod", "Production")));

        await PressAsync(adapter, choiceIndex: 0);
        await PressAsync(adapter, choiceIndex: 0);

        resolver.Submissions.Count.ShouldBe(1);
        // Both presses are acknowledged; only one is honoured.
        calls.Count(c => c.Method == "answerCallbackQuery").ShouldBe(2);
    }

    [Fact]
    public async Task CallbackQuery_RacingATypedAnswer_IsRejectedByTheResolverNotDoubleResolved()
    {
        // The resolver already owns pending state; the adapter keeps no parallel "answered" flag.
        // Simulate the typed answer having won by making the resolver report no pending prompt.
        var calls = new List<CapturedCall>();
        var resolver = new RecordingResolver { Result = AskUserResolutionResult.NoPendingPrompt("already answered") };
        var adapter = CreateAdapter(calls, resolver);

        await adapter.SendStreamEventAsync(
            StreamTargets.For(AllowedChat.ToString()),
            AskEvent("Pick a ring", ("canary", "Canary")));

        await PressAsync(adapter, choiceIndex: 0);
        await PressAsync(adapter, choiceIndex: 0);

        // The loser is reported once and then forgotten - never retried into a second resolution.
        resolver.Submissions.Count.ShouldBe(1);
    }

    // ── Authorization: a callback is inbound user input ─────────────────────

    [Fact]
    public async Task CallbackQuery_FromUnauthorizedChat_IsRejectedAndNeverReachesTheResolver()
    {
        var calls = new List<CapturedCall>();
        var resolver = new RecordingResolver();
        var adapter = CreateAdapter(calls, resolver);

        await adapter.SendStreamEventAsync(
            StreamTargets.For(AllowedChat.ToString()),
            AskEvent("Pick a ring", ("canary", "Canary")));

        await PressAsync(adapter, choiceIndex: 0, chatId: 999999);

        resolver.Submissions.ShouldBeEmpty();
        // Rejected, not ignored: the press is explicitly answered with a refusal.
        var ack = calls.Last(c => c.Method == "answerCallbackQuery");
        using var json = JsonDocument.Parse(ack.Body);
        json.RootElement.GetProperty("text").GetString().ShouldBe("Not permitted.");
    }

    [Fact]
    public async Task CallbackQuery_FromUnauthorizedUser_IsRejectedAndNeverReachesTheResolver()
    {
        var calls = new List<CapturedCall>();
        var resolver = new RecordingResolver();
        var adapter = CreateAdapter(calls, resolver, allowedUserId: 7);

        await adapter.SendStreamEventAsync(
            StreamTargets.For(AllowedChat.ToString()),
            AskEvent("Pick a ring", ("canary", "Canary")));

        await PressAsync(adapter, choiceIndex: 0, fromId: 999);

        resolver.Submissions.ShouldBeEmpty();
        var ack = calls.Last(c => c.Method == "answerCallbackQuery");
        using var json = JsonDocument.Parse(ack.Body);
        json.RootElement.GetProperty("text").GetString().ShouldBe("Not permitted.");
    }

    [Fact]
    public async Task CallbackQuery_WithNoSender_IsRejected()
    {
        var calls = new List<CapturedCall>();
        var resolver = new RecordingResolver();
        var adapter = CreateAdapter(calls, resolver);

        await adapter.SendStreamEventAsync(
            StreamTargets.For(AllowedChat.ToString()),
            AskEvent("Pick a ring", ("canary", "Canary")));

        TelegramAskUserCallbackToken.TryEncode(RequestId, 0, out var data).ShouldBeTrue();
        await adapter.HandleWebhookUpdateAsync("default", new TelegramUpdate
        {
            UpdateId = 1,
            CallbackQuery = new TelegramCallbackQuery
            {
                Id = "cb-1",
                From = null,
                Message = new TelegramMessage { MessageId = 1, Chat = new TelegramChat { Id = AllowedChat } },
                Data = data
            }
        }, "secret");

        resolver.Submissions.ShouldBeEmpty();
    }

    [Fact]
    public async Task CallbackQuery_ForAnUnknownRequest_IsAcknowledgedWithoutResolving()
    {
        var calls = new List<CapturedCall>();
        var resolver = new RecordingResolver();
        var adapter = CreateAdapter(calls, resolver);

        TelegramAskUserCallbackToken.TryEncode("neverrendered", 0, out var data).ShouldBeTrue();
        await adapter.HandleWebhookUpdateAsync("default", new TelegramUpdate
        {
            UpdateId = 1,
            CallbackQuery = new TelegramCallbackQuery
            {
                Id = "cb-1",
                From = new TelegramUser { Id = 7 },
                Message = new TelegramMessage { MessageId = 1, Chat = new TelegramChat { Id = AllowedChat } },
                Data = data
            }
        }, "secret");

        resolver.Submissions.ShouldBeEmpty();
        calls.ShouldContain(c => c.Method == "answerCallbackQuery");
    }

    // ── AC3: the bot must actually subscribe to callback_query updates ──────

    [Fact]
    public async Task GetUpdates_RequestsCallbackQueryUpdates()
    {
        var calls = new List<CapturedCall>();
        var client = new TelegramBotApiClient(
            new HttpClient(new CapturingHandler(async (req, ct) =>
            {
                var method = req.RequestUri?.Segments.LastOrDefault()?.Trim('/') ?? string.Empty;
                calls.Add(new CapturedCall(method, req.Content is null ? "{}" : await req.Content.ReadAsStringAsync(ct)));
                return JsonOk(Array.Empty<TelegramUpdate>());
            })),
            "token",
            NullLogger<TelegramBotApiClient>.Instance);

        await client.GetUpdatesAsync(null, 1);

        using var json = JsonDocument.Parse(calls.Single(c => c.Method == "getUpdates").Body);
        json.RootElement.GetProperty("allowed_updates").EnumerateArray()
            .Select(e => e.GetString())
            .ShouldContain("callback_query");
    }

    // ── harness ─────────────────────────────────────────────────────────────

    private static AgentStreamEvent AskEvent(string prompt, params (string Value, string Label)[] choices)
        => new()
        {
            Type = AgentStreamEventType.UserInputRequired,
            UserInputRequest = new AskUserRequest
            {
                RequestId = RequestId,
                ConversationId = ConversationId.From("conv-1"),
                SessionId = SessionId.From("sess-1"),
                AgentId = AgentId.From("agent-1"),
                Prompt = prompt,
                Choices = choices.Length == 0
                    ? null
                    : choices.Select(c => new AskUserChoice { Value = c.Value, Label = c.Label }).ToArray()
            }
        };

    private static Task PressAsync(
        TelegramChannelAdapter adapter,
        int choiceIndex,
        long chatId = AllowedChat,
        long fromId = 7)
    {
        TelegramAskUserCallbackToken.TryEncode(RequestId, choiceIndex, out var data).ShouldBeTrue();
        return adapter.HandleWebhookUpdateAsync("default", new TelegramUpdate
        {
            UpdateId = 1,
            CallbackQuery = new TelegramCallbackQuery
            {
                Id = "cb-" + choiceIndex,
                From = new TelegramUser { Id = fromId },
                Message = new TelegramMessage { MessageId = 1, Chat = new TelegramChat { Id = chatId } },
                Data = data
            }
        }, "secret");
    }

    private static TelegramChannelAdapter CreateAdapter(
        List<CapturedCall> calls,
        IAskUserPromptResolver resolver,
        long? allowedUserId = null)
    {
        var handler = new CapturingHandler(async (request, ct) =>
        {
            var method = request.RequestUri?.Segments.LastOrDefault()?.Trim('/') ?? string.Empty;
            var body = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(ct);
            calls.Add(new CapturedCall(method, body));

            object result = method switch
            {
                "sendMessage" or "editMessageText"
                    => new TelegramMessage { MessageId = 1, Chat = new TelegramChat { Id = AllowedChat } },
                _ => true
            };
            return JsonOk(result);
        });

        var options = new TelegramGatewayOptions
        {
            BotToken = "token",
            // Webhook mode so HandleWebhookUpdateAsync is a legitimate entry point in tests; it runs
            // the exact same HandleUpdateAsync path the polling loop uses.
            WebhookUrl = "https://example.invalid/hook",
            WebhookSecretToken = "secret",
            AllowedChatIds = { AllowedChat },
            // Rich messages off: ask_user prompts use the legacy sendMessage path, which is the only
            // one that carries reply_markup.
            RichMessages = false
        };

        if (allowedUserId is { } uid)
            options.AllowedUserIds.Add(uid);

        var factory = new StubHttpClientFactory(_ => new HttpClient(handler));
        return new TelegramChannelAdapter(
            NullLogger<TelegramChannelAdapter>.Instance,
            Options.Create(options),
            factory,
            configuration: null,
            askUserResolver: resolver);
    }

    /// <summary>
    /// Records every submission so the tests can assert the adapter routes through the single
    /// resolver seam (#2322) rather than inventing its own resolution path.
    /// </summary>
    private sealed class RecordingResolver : IAskUserPromptResolver
    {
        public List<AskUserSubmission> Submissions { get; } = [];

        public AskUserResolutionResult Result { get; set; } = AskUserResolutionResult.Resolved(RequestId);

        public ValueTask<AskUserResolutionResult> ResolveAsync(AskUserSubmission submission, CancellationToken cancellationToken = default)
        {
            Submissions.Add(submission);
            return ValueTask.FromResult(Result);
        }

        public bool TryGetPendingRequestId(ConversationId conversationId, out string requestId)
        {
            requestId = RequestId;
            return true;
        }
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
