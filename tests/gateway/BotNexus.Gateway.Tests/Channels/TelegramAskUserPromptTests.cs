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
/// Tests for rendering <c>ask_user</c> prompts on Telegram as inline keyboards and resolving button
/// taps through the shared gateway seam (#2323).
/// </summary>
/// <remarks>
/// Before this work a <c>UserInputRequired</c> event fell through the adapter's stream-event switch
/// and was discarded: the user saw nothing, the turn appeared to hang, and the prompt could only
/// resolve by timeout - or be silently satisfied by whatever the user typed next. These tests pin
/// both halves of the fix, the outbound keyboard and the inbound callback query, including the
/// authorization and idempotency behaviour that makes a publicly visible button safe.
/// </remarks>
public sealed class TelegramAskUserPromptTests
{
    // ── Outbound: rendering the prompt ───────────────────────────────────────

    [Fact]
    public async Task UserInputRequired_WithChoices_SendsInlineKeyboard()
    {
        var calls = new List<CapturedCall>();
        var adapter = CreateAdapter(calls, out _);

        await adapter.SendStreamEventAsync(StreamTargets.For("42"), PromptEvent(SingleChoicePrompt()));

        var send = calls.Single(c => c.Method == "sendMessage");
        using var json = JsonDocument.Parse(send.Body);
        json.RootElement.GetProperty("chat_id").GetInt64().ShouldBe(42);
        json.RootElement.GetProperty("text").GetString().ShouldBe("Pick a colour");

        var rows = json.RootElement.GetProperty("reply_markup").GetProperty("inline_keyboard");
        // Two choices + a cancel row. Single-choice prompts get no submit button.
        rows.GetArrayLength().ShouldBe(3);
        rows[0][0].GetProperty("text").GetString().ShouldBe("Red");
        rows[1][0].GetProperty("text").GetString().ShouldBe("Blue");
        rows[2][0].GetProperty("text").GetString()!.ShouldContain("Cancel");
    }

    [Fact]
    public async Task UserInputRequired_FreeForm_SendsPlainPromptWithNoKeyboard()
    {
        var calls = new List<CapturedCall>();
        var adapter = CreateAdapter(calls, out _);

        await adapter.SendStreamEventAsync(StreamTargets.For("42"), PromptEvent(new AskUserRequest
        {
            RequestId = "req-free",
            ConversationId = ConversationId.From("c_1"),
            SessionId = SessionId.From("s_1"),
            AgentId = AgentId.From("farnsworth"),
            Prompt = "What should I name it?",
            InputType = AskUserInputType.FreeForm
        }));

        var send = calls.Single(c => c.Method == "sendMessage");
        using var json = JsonDocument.Parse(send.Body);
        json.RootElement.GetProperty("text").GetString().ShouldBe("What should I name it?");
        // The distinguishing assertion: a free-form prompt must carry NO keyboard at all, otherwise
        // the user is shown buttons for a question that expects typed text.
        json.RootElement.TryGetProperty("reply_markup", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task UserInputRequired_MultiSelect_RendersSubmitButton()
    {
        var calls = new List<CapturedCall>();
        var adapter = CreateAdapter(calls, out _);

        await adapter.SendStreamEventAsync(StreamTargets.For("42"), PromptEvent(MultiChoicePrompt()));

        using var json = JsonDocument.Parse(calls.Single(c => c.Method == "sendMessage").Body);
        var rows = json.RootElement.GetProperty("reply_markup").GetProperty("inline_keyboard");
        // 2 choices + submit + cancel.
        rows.GetArrayLength().ShouldBe(4);
        rows[2][0].GetProperty("text").GetString()!.ShouldContain("Submit");
        rows[3][0].GetProperty("text").GetString()!.ShouldContain("Cancel");
    }

    [Fact]
    public async Task UserInputRequired_LargeChoiceSet_DegradesToNumberedTextInsteadOfFailing()
    {
        var calls = new List<CapturedCall>();
        var adapter = CreateAdapter(calls, out _);

        var choices = Enumerable.Range(1, 60)
            .Select(i => new AskUserChoice { Value = $"v{i}", Label = $"Option {i}" })
            .ToList();

        await adapter.SendStreamEventAsync(StreamTargets.For("42"), PromptEvent(new AskUserRequest
        {
            RequestId = "req-big",
            ConversationId = ConversationId.From("c_1"),
            SessionId = SessionId.From("s_1"),
            AgentId = AgentId.From("farnsworth"),
            Prompt = "Pick one",
            InputType = AskUserInputType.SingleChoice,
            Choices = choices
        }));

        var send = calls.Single(c => c.Method == "sendMessage");
        using var json = JsonDocument.Parse(send.Body);
        // Degrades rather than failing the send: no keyboard, but the choices are still answerable
        // as a numbered list the user can reply to by typing.
        json.RootElement.TryGetProperty("reply_markup", out _).ShouldBeFalse();
        var text = json.RootElement.GetProperty("text").GetString();
        text.ShouldNotBeNull();
        text!.ShouldContain("1. Option 1");
        text.ShouldContain("60. Option 60");
    }

    [Fact]
    public async Task UserInputRequired_CallbackDataStaysWithinBotApiLimit_ForLargeChoiceSet()
    {
        var calls = new List<CapturedCall>();
        var adapter = CreateAdapter(calls, out _);

        // Deliberately verbose values/labels: if callback_data carried the choice value rather than a
        // compact token, this prompt would blow the Bot API's 64-byte ceiling and Telegram would
        // reject the entire send.
        var choices = Enumerable.Range(1, 30)
            .Select(i => new AskUserChoice
            {
                Value = new string('v', 200) + i,
                Label = new string('L', 120) + i
            })
            .ToList();

        await adapter.SendStreamEventAsync(StreamTargets.For("42"), PromptEvent(new AskUserRequest
        {
            RequestId = new string('r', 300),
            ConversationId = ConversationId.From("c_1"),
            SessionId = SessionId.From("s_1"),
            AgentId = AgentId.From("farnsworth"),
            Prompt = "Pick one",
            InputType = AskUserInputType.SingleChoice,
            Choices = choices
        }));

        using var json = JsonDocument.Parse(calls.Single(c => c.Method == "sendMessage").Body);
        var rows = json.RootElement.GetProperty("reply_markup").GetProperty("inline_keyboard");

        var inspected = 0;
        foreach (var row in rows.EnumerateArray())
        {
            foreach (var button in row.EnumerateArray())
            {
                var data = button.GetProperty("callback_data").GetString();
                data.ShouldNotBeNull();
                Encoding.UTF8.GetByteCount(data!).ShouldBeLessThanOrEqualTo(64);
                inspected++;
            }
        }

        // Guards against a vacuous pass: the loop must actually have examined buttons.
        inspected.ShouldBe(31); // 30 choices + cancel
    }

    // ── Inbound: resolving a tap ─────────────────────────────────────────────

    [Fact]
    public async Task CallbackQuery_ResolvesPromptThroughSharedGatewayService()
    {
        var calls = new List<CapturedCall>();
        var adapter = CreateAdapter(calls, out var resolver);

        await adapter.SendStreamEventAsync(StreamTargets.For("42"), PromptEvent(SingleChoicePrompt()));
        await TapAsync(adapter, calls, ChoiceData(calls, index: 1));

        var submission = resolver.Submissions.ShouldHaveSingleItem();
        submission.ConversationId.ShouldBe(ConversationId.From("c_1"));
        submission.RequestId.ShouldBe("req-1");
        submission.SelectedValues.ShouldBe(["blue"]);
        submission.Cancelled.ShouldBeFalse();
        submission.OriginChannel.ShouldBe(ChannelKey.From("telegram"));
    }

    [Fact]
    public async Task CallbackQuery_AcknowledgesPromptlySoTheClientStopsSpinning()
    {
        var calls = new List<CapturedCall>();
        var adapter = CreateAdapter(calls, out _);

        await adapter.SendStreamEventAsync(StreamTargets.For("42"), PromptEvent(SingleChoicePrompt()));
        await TapAsync(adapter, calls, ChoiceData(calls, index: 0));

        var ack = calls.Single(c => c.Method == "answerCallbackQuery");
        using var json = JsonDocument.Parse(ack.Body);
        json.RootElement.GetProperty("callback_query_id").GetString().ShouldBe("cbq-1");
    }

    [Fact]
    public async Task CallbackQuery_AfterResolution_EditsMessageAndRemovesKeyboard()
    {
        var calls = new List<CapturedCall>();
        var adapter = CreateAdapter(calls, out _);

        await adapter.SendStreamEventAsync(StreamTargets.For("42"), PromptEvent(SingleChoicePrompt()));
        await TapAsync(adapter, calls, ChoiceData(calls, index: 0));

        var edit = calls.Single(c => c.Method == "editMessageText");
        using var json = JsonDocument.Parse(edit.Body);
        json.RootElement.GetProperty("message_id").GetInt32().ShouldBe(555);
        json.RootElement.GetProperty("text").GetString()!.ShouldContain("Red");
        // Omitting reply_markup is how Telegram is told to strip the keyboard, so a resolved prompt
        // cannot be tapped a second time.
        json.RootElement.TryGetProperty("reply_markup", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task CallbackQuery_Duplicate_IsNoOpNotSecondResolution()
    {
        var calls = new List<CapturedCall>();
        var adapter = CreateAdapter(calls, out var resolver);

        await adapter.SendStreamEventAsync(StreamTargets.For("42"), PromptEvent(SingleChoicePrompt()));
        var data = ChoiceData(calls, index: 0);

        await TapAsync(adapter, calls, data);
        await TapAsync(adapter, calls, data, callbackId: "cbq-2");

        // The load-bearing assertion: the second tap must not produce a second submission.
        resolver.Submissions.Count.ShouldBe(1);
        adapter.GetPendingPromptCount().ShouldBe(0);
        // Both taps are still acknowledged, so the client never keeps spinning.
        calls.Count(c => c.Method == "answerCallbackQuery").ShouldBe(2);
    }

    [Fact]
    public async Task CallbackQuery_Cancel_ResolvesWithCancellationSemantics()
    {
        var calls = new List<CapturedCall>();
        var adapter = CreateAdapter(calls, out var resolver);

        await adapter.SendStreamEventAsync(StreamTargets.For("42"), PromptEvent(SingleChoicePrompt()));
        await TapAsync(adapter, calls, CancelData(calls));

        var submission = resolver.Submissions.ShouldHaveSingleItem();
        submission.Cancelled.ShouldBeTrue();
        submission.SelectedValues.ShouldBeNull();

        using var json = JsonDocument.Parse(calls.Single(c => c.Method == "editMessageText").Body);
        json.RootElement.GetProperty("text").GetString()!.ShouldContain("Cancelled");
    }

    [Fact]
    public async Task CallbackQuery_MultiSelect_AccumulatesSelectionsAndSubmitsOnce()
    {
        var calls = new List<CapturedCall>();
        var adapter = CreateAdapter(calls, out var resolver);

        await adapter.SendStreamEventAsync(StreamTargets.For("42"), PromptEvent(MultiChoicePrompt()));

        await TapAsync(adapter, calls, ChoiceData(calls, index: 0));
        await TapAsync(adapter, calls, ChoiceData(calls, index: 1), callbackId: "cbq-2");

        // Selecting does NOT resolve: the user must explicitly submit.
        resolver.Submissions.ShouldBeEmpty();

        await TapAsync(adapter, calls, SubmitData(calls), callbackId: "cbq-3");

        var submission = resolver.Submissions.ShouldHaveSingleItem();
        submission.SelectedValues.ShouldBe(["red", "blue"]);
    }

    [Fact]
    public async Task CallbackQuery_MultiSelect_TogglingTwiceDeselects()
    {
        var calls = new List<CapturedCall>();
        var adapter = CreateAdapter(calls, out var resolver);

        await adapter.SendStreamEventAsync(StreamTargets.For("42"), PromptEvent(MultiChoicePrompt()));

        await TapAsync(adapter, calls, ChoiceData(calls, index: 0));
        await TapAsync(adapter, calls, ChoiceData(calls, index: 0), callbackId: "cbq-2");
        await TapAsync(adapter, calls, SubmitData(calls), callbackId: "cbq-3");

        resolver.Submissions.ShouldHaveSingleItem().SelectedValues.ShouldBeEmpty();
    }

    // ── Authorization: a button is visible to everyone in a group ────────────

    [Fact]
    public async Task CallbackQuery_FromUnauthorizedChat_IsRejectedWithoutResolving()
    {
        var calls = new List<CapturedCall>();
        var adapter = CreateAdapter(calls, out var resolver);

        await adapter.SendStreamEventAsync(StreamTargets.For("42"), PromptEvent(SingleChoicePrompt()));
        var data = ChoiceData(calls, index: 0);

        await adapter.HandleWebhookUpdateAsync(
            "default",
            CallbackUpdate(data, chatId: 999, userId: 7, callbackId: "cbq-bad"),
            providedSecret: null,
            CancellationToken.None);

        resolver.Submissions.ShouldBeEmpty();
        // Still pending: an unauthorized tap must not consume the prompt either.
        adapter.GetPendingPromptCount().ShouldBe(1);
    }

    [Fact]
    public async Task CallbackQuery_FromUnauthorizedUser_IsRejectedWithoutResolving()
    {
        var calls = new List<CapturedCall>();
        var adapter = CreateAdapter(calls, out var resolver, allowedUserId: 7);

        await adapter.SendStreamEventAsync(StreamTargets.For("42"), PromptEvent(SingleChoicePrompt()));
        var data = ChoiceData(calls, index: 0);

        await adapter.HandleWebhookUpdateAsync(
            "default",
            CallbackUpdate(data, chatId: 42, userId: 8888, callbackId: "cbq-bad"),
            providedSecret: null,
            CancellationToken.None);

        resolver.Submissions.ShouldBeEmpty();
        adapter.GetPendingPromptCount().ShouldBe(1);
    }

    [Fact]
    public async Task CallbackQuery_UnknownToken_IsAcknowledgedButResolvesNothing()
    {
        var calls = new List<CapturedCall>();
        var adapter = CreateAdapter(calls, out var resolver);

        // A tap on a keyboard this process never issued - e.g. a prompt from before a restart.
        await TapAsync(adapter, calls, "bnq:9999:0");

        resolver.Submissions.ShouldBeEmpty();
        calls.ShouldContain(c => c.Method == "answerCallbackQuery");
    }

    [Fact]
    public async Task CallbackQuery_ForeignCallbackData_IsIgnoredNotMisparsed()
    {
        var calls = new List<CapturedCall>();
        var adapter = CreateAdapter(calls, out var resolver);

        await TapAsync(adapter, calls, "some-other-feature:payload");

        resolver.Submissions.ShouldBeEmpty();
        calls.ShouldNotContain(c => c.Method == "editMessageText");
    }

    [Fact]
    public async Task UserInputRequired_ResolverMissing_StillRendersThePrompt()
    {
        // A misconfigured gateway must not mean an invisible prompt: rendering is independent of
        // whether the resolution seam happens to be registered.
        var calls = new List<CapturedCall>();
        var adapter = CreateAdapter(calls, out _, useResolver: false);

        await adapter.SendStreamEventAsync(StreamTargets.For("42"), PromptEvent(SingleChoicePrompt()));

        calls.ShouldContain(c => c.Method == "sendMessage");
    }

    // ── Pure keyboard/token logic ────────────────────────────────────────────

    [Fact]
    public void TryParseCallbackData_RoundTripsChoiceCancelAndSubmit()
    {
        TelegramPromptKeyboard.TryParseCallbackData(
            TelegramPromptKeyboard.ChoiceCallbackData("7", 3), out var token, out var kind, out var index).ShouldBeTrue();
        token.ShouldBe("7");
        kind.ShouldBe(TelegramPromptCallbackKind.Choice);
        index.ShouldBe(3);

        TelegramPromptKeyboard.TryParseCallbackData(
            TelegramPromptKeyboard.CancelCallbackData("7"), out _, out kind, out _).ShouldBeTrue();
        kind.ShouldBe(TelegramPromptCallbackKind.Cancel);

        TelegramPromptKeyboard.TryParseCallbackData(
            TelegramPromptKeyboard.SubmitCallbackData("7"), out _, out kind, out _).ShouldBeTrue();
        kind.ShouldBe(TelegramPromptCallbackKind.Submit);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bnq")]
    [InlineData("bnq:7")]
    [InlineData("bnq::0")]
    [InlineData("other:7:0")]
    [InlineData("bnq:7:notanindex")]
    [InlineData("bnq:7:-1")]
    public void TryParseCallbackData_RejectsMalformedInput(string? data)
        => TelegramPromptKeyboard.TryParseCallbackData(data, out _, out _, out _).ShouldBeFalse();

    // ── harness ──────────────────────────────────────────────────────────────

    private static AskUserRequest SingleChoicePrompt() => new()
    {
        RequestId = "req-1",
        ConversationId = ConversationId.From("c_1"),
        SessionId = SessionId.From("s_1"),
        AgentId = AgentId.From("farnsworth"),
        Prompt = "Pick a colour",
        InputType = AskUserInputType.SingleChoice,
        Choices =
        [
            new AskUserChoice { Value = "red", Label = "Red" },
            new AskUserChoice { Value = "blue", Label = "Blue" }
        ]
    };

    private static AskUserRequest MultiChoicePrompt() => new()
    {
        RequestId = "req-multi",
        ConversationId = ConversationId.From("c_1"),
        SessionId = SessionId.From("s_1"),
        AgentId = AgentId.From("farnsworth"),
        Prompt = "Pick colours",
        InputType = AskUserInputType.MultipleChoice,
        AllowMultiple = true,
        Choices =
        [
            new AskUserChoice { Value = "red", Label = "Red" },
            new AskUserChoice { Value = "blue", Label = "Blue" }
        ]
    };

    private static AgentStreamEvent PromptEvent(AskUserRequest request) => new()
    {
        Type = AgentStreamEventType.UserInputRequired,
        UserInputRequest = request,
        ConversationId = request.ConversationId,
        SessionId = request.SessionId,
        AgentId = request.AgentId
    };

    /// <summary>Reads the callback data off the most recently sent keyboard.</summary>
    private static string ButtonData(List<CapturedCall> calls, Func<JsonElement, bool> match)
    {
        var send = calls.Last(c => c.Method == "sendMessage");
        using var json = JsonDocument.Parse(send.Body);
        foreach (var row in json.RootElement.GetProperty("reply_markup").GetProperty("inline_keyboard").EnumerateArray())
        {
            foreach (var button in row.EnumerateArray())
            {
                if (match(button))
                    return button.GetProperty("callback_data").GetString()!;
            }
        }

        throw new InvalidOperationException("No matching button was rendered.");
    }

    private static string ChoiceData(List<CapturedCall> calls, int index)
    {
        var seen = -1;
        return ButtonData(calls, button =>
        {
            var text = button.GetProperty("text").GetString() ?? string.Empty;
            if (text.Contains("Cancel") || text.Contains("Submit"))
                return false;
            return ++seen == index;
        });
    }

    private static string CancelData(List<CapturedCall> calls)
        => ButtonData(calls, b => (b.GetProperty("text").GetString() ?? string.Empty).Contains("Cancel"));

    private static string SubmitData(List<CapturedCall> calls)
        => ButtonData(calls, b => (b.GetProperty("text").GetString() ?? string.Empty).Contains("Submit"));

    private static Task TapAsync(
        TelegramChannelAdapter adapter,
        List<CapturedCall> calls,
        string callbackData,
        string callbackId = "cbq-1")
        => adapter.HandleWebhookUpdateAsync(
            "default",
            CallbackUpdate(callbackData, chatId: 42, userId: 7, callbackId),
            providedSecret: TestWebhookSecret,
            CancellationToken.None);

    private static TelegramUpdate CallbackUpdate(string data, long chatId, long userId, string callbackId) => new()
    {
        UpdateId = 1,
        CallbackQuery = new TelegramCallbackQuery
        {
            Id = callbackId,
            Data = data,
            From = new TelegramUser { Id = userId },
            Message = new TelegramMessage { MessageId = 555, Chat = new TelegramChat { Id = chatId } }
        }
    };

    private const string TestWebhookSecret = "test-secret-2323";

    private static TelegramChannelAdapter CreateAdapter(
        List<CapturedCall> calls,
        out RecordingPromptResolver resolver,
        long allowedChatId = 42,
        long? allowedUserId = null,
        bool useResolver = true)
    {
        resolver = new RecordingPromptResolver();
        return CreateAdapter(calls, useResolver ? resolver : null, allowedChatId, allowedUserId);
    }

    private static TelegramChannelAdapter CreateAdapter(
        List<CapturedCall> calls,
        IAskUserPromptResolver? promptResolver,
        long allowedChatId,
        long? allowedUserId)
    {
        var handler = new CapturingHandler(async (request, ct) =>
        {
            var method = request.RequestUri?.Segments.LastOrDefault()?.Trim('/') ?? string.Empty;
            var body = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(ct);
            calls.Add(new CapturedCall(method, body));

            object result = method switch
            {
                "sendMessage" or "editMessageText" or "sendRichMessage"
                    => new TelegramMessage { MessageId = 555, Chat = new TelegramChat { Id = 42 } },
                _ => true
            };

            var payload = JsonSerializer.Serialize(new TelegramApiResponse<object> { Ok = true, Result = result });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
        });

        var options = new TelegramGatewayOptions
        {
            BotToken = "token",
            // Webhook mode so tests can inject updates through the real authorization path rather
            // than reaching into a private method.
            WebhookUrl = "https://example.invalid/hook",
            WebhookSecretToken = TestWebhookSecret,
            AllowedChatIds = { allowedChatId },
            RichMessages = false
        };

        if (allowedUserId is { } userId)
            options.AllowedUserIds.Add(userId);

        var factory = new StubHttpClientFactory(_ => new HttpClient(handler));
        return new TelegramChannelAdapter(
            NullLogger<TelegramChannelAdapter>.Instance,
            Options.Create(options),
            factory,
            configuration: null,
            promptResolver: promptResolver);
    }

    private sealed class RecordingPromptResolver : IAskUserPromptResolver
    {
        public List<AskUserSubmission> Submissions { get; } = [];

        public ValueTask<AskUserResolutionResult> ResolveAsync(
            AskUserSubmission submission,
            CancellationToken cancellationToken = default)
        {
            Submissions.Add(submission);
            return ValueTask.FromResult(AskUserResolutionResult.Resolved(submission.RequestId ?? "unknown"));
        }

        public bool TryGetPendingRequestId(ConversationId conversationId, out string requestId)
        {
            requestId = string.Empty;
            return false;
        }
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
