using System.Net;
using System.Text;
using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.Gateway.Models;
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

    // ── Streaming: Rich Message drafts (DM) + finalize ───────────────────────

    [Fact]
    public async Task Streaming_PrivateChat_SendsDraftsThenFinalizesWithRichMessage()
    {
        var calls = new List<CapturedCall>();
        var adapter = CreateRichAdapter(calls, richSucceeds: true);
        var target = StreamTargets.For("42"); // positive chat id = private chat

        await adapter.SendStreamEventAsync(target, new AgentStreamEvent { Type = AgentStreamEventType.MessageStart });
        // Delta over the 100-char flush threshold forces a draft flush.
        await adapter.SendStreamEventAsync(target, new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "**" + new string('a', 100) + "**" });
        await adapter.SendStreamEventAsync(target, new AgentStreamEvent { Type = AgentStreamEventType.MessageEnd });

        // At least one ephemeral draft preview during streaming...
        var draft = calls.FirstOrDefault(c => c.Method == "sendRichMessageDraft");
        draft.ShouldNotBeNull();
        using var draftJson = JsonDocument.Parse(draft!.Body);
        draftJson.RootElement.GetProperty("draft_id").GetInt64().ShouldNotBe(0);
        draftJson.RootElement.GetProperty("rich_message").GetProperty("markdown").GetString().ShouldNotBeNullOrEmpty();

        // ...and exactly one persistent sendRichMessage at the end to keep the message.
        var finalize = calls.Where(c => c.Method == "sendRichMessage").ToList();
        finalize.Count.ShouldBe(1);
        using var finalJson = JsonDocument.Parse(finalize[0].Body);
        finalJson.RootElement.GetProperty("rich_message").GetProperty("markdown").GetString()
            .ShouldBe("**" + new string('a', 100) + "**");
    }

    [Fact]
    public async Task Streaming_PrivateChat_DraftsReuseSameDraftId()
    {
        var calls = new List<CapturedCall>();
        var adapter = CreateRichAdapter(calls, richSucceeds: true);
        var target = StreamTargets.For("42");

        await adapter.SendStreamEventAsync(target, new AgentStreamEvent { Type = AgentStreamEventType.MessageStart });
        await adapter.SendStreamDeltaAsync(target, new string('a', 150));
        await adapter.SendStreamDeltaAsync(target, new string('b', 150));
        await adapter.SendStreamEventAsync(target, new AgentStreamEvent { Type = AgentStreamEventType.MessageEnd });

        var draftIds = calls
            .Where(c => c.Method == "sendRichMessageDraft")
            .Select(c =>
            {
                using var j = JsonDocument.Parse(c.Body);
                return j.RootElement.GetProperty("draft_id").GetInt64();
            })
            .Distinct()
            .ToList();

        // All draft frames for one stream animate under a single draft id.
        draftIds.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Streaming_Group_NoDrafts_FinalizesOnceWithRichMessage()
    {
        // Drafts are DM-only; a group/supergroup (negative chat id) gets no live preview, just the
        // final rich message at MessageEnd.
        var calls = new List<CapturedCall>();
        var adapter = CreateRichAdapter(calls, richSucceeds: true, allowedChatId: -1001234567890);
        var target = StreamTargets.For("-1001234567890");

        await adapter.SendStreamEventAsync(target, new AgentStreamEvent { Type = AgentStreamEventType.MessageStart });
        await adapter.SendStreamEventAsync(target, new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "**" + new string('a', 100) + "**" });
        await adapter.SendStreamEventAsync(target, new AgentStreamEvent { Type = AgentStreamEventType.MessageEnd });

        calls.ShouldNotContain(c => c.Method == "sendRichMessageDraft");
        calls.Count(c => c.Method == "sendRichMessage").ShouldBe(1);
    }

    [Fact]
    public async Task Streaming_DraftRejected_StillFinalizes()
    {
        // If a draft is rejected mid-stream, drafting stops but the message is still finalized.
        var calls = new List<CapturedCall>();
        var adapter = CreateRichAdapter(calls, richSucceeds: true, draftSucceeds: false);
        var target = StreamTargets.For("42");

        await adapter.SendStreamEventAsync(target, new AgentStreamEvent { Type = AgentStreamEventType.MessageStart });
        await adapter.SendStreamEventAsync(target, new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = new string('a', 150) });
        await adapter.SendStreamEventAsync(target, new AgentStreamEvent { Type = AgentStreamEventType.MessageEnd });

        // A draft was attempted (and rejected), but the persistent rich message still went out.
        calls.ShouldContain(c => c.Method == "sendRichMessageDraft");
        calls.Count(c => c.Method == "sendRichMessage").ShouldBe(1);
    }

    [Fact]
    public async Task Streaming_RichDisabled_UsesLegacyEditPath()
    {
        var calls = new List<CapturedCall>();
        var adapter = CreateRichAdapter(calls, richSucceeds: true, richEnabled: false);
        var target = StreamTargets.For("42");

        await adapter.SendStreamEventAsync(target, new AgentStreamEvent { Type = AgentStreamEventType.MessageStart });
        await adapter.SendStreamEventAsync(target, new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "**" + new string('a', 100) + "**" });
        await adapter.SendStreamEventAsync(target, new AgentStreamEvent { Type = AgentStreamEventType.MessageEnd });

        // Legacy streaming uses sendMessage/editMessageText, never the rich endpoints.
        calls.ShouldNotContain(c => c.Method == "sendRichMessageDraft");
        calls.ShouldNotContain(c => c.Method == "sendRichMessage");
        calls.ShouldContain(c => c.Method == "sendMessage");
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

    // ── Tool activity: must survive message-boundary churn (the "eaten" bug) ──

    [Fact]
    public async Task ToolActivity_SurvivesNextAssistantMessage_DeliveredAsStandaloneMessage()
    {
        // Reproduces the bug in the GROUP path (negative chat id), where streaming content is buffered
        // SILENTLY and only sent at MessageEnd (rich drafts are DM-only). A tool-using turn emits:
        //   [MessageStart preamble][ContentDelta][MessageEnd]   <- assistant 1 finalised, state removed
        //   [ToolStart][ToolEnd]                                <- tool cycle, fresh state
        //   [MessageStart followup][ContentDelta][MessageEnd]   <- assistant 2
        // Old behaviour appended the tool line to the (silent) content buffer of the fresh state; the
        // followup MessageStart then reset it away before any send -> the tool annotation was "eaten"
        // (ZERO outbound messages carried it). New behaviour sends each tool event as its own message,
        // so it survives.
        var calls = new List<CapturedCall>();
        var adapter = CreateRichAdapter(calls, richSucceeds: true, allowedChatId: -1001);
        var target = StreamTargets.For("-1001"); // negative id = group/supergroup, no live drafts

        await adapter.SendStreamEventAsync(target, new AgentStreamEvent { Type = AgentStreamEventType.MessageStart });
        await adapter.SendStreamEventAsync(target, new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "Let me check that." });
        await adapter.SendStreamEventAsync(target, new AgentStreamEvent { Type = AgentStreamEventType.MessageEnd });

        await adapter.SendStreamEventAsync(target, new AgentStreamEvent { Type = AgentStreamEventType.ToolStart, ToolName = "read", ToolCallId = "call-1" });
        await adapter.SendStreamEventAsync(target, new AgentStreamEvent { Type = AgentStreamEventType.ToolEnd, ToolName = "read", ToolCallId = "call-1", ToolIsError = false });

        await adapter.SendStreamEventAsync(target, new AgentStreamEvent { Type = AgentStreamEventType.MessageStart });
        await adapter.SendStreamEventAsync(target, new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "Here's what I found." });
        await adapter.SendStreamEventAsync(target, new AgentStreamEvent { Type = AgentStreamEventType.MessageEnd });

        // Tool activity delivered as standalone sendMessage(s) carrying the canonical glyph.
        var toolMessages = AllSentText(calls).Where(t => t.Contains("read")).ToList();
        toolMessages.ShouldNotBeEmpty(); // the bug: this was empty (tool lines eaten)
        toolMessages.ShouldContain(t => t.Contains(ToolGlyphs.ForTool("read"))); // canonical glyph for read on every channel

        // The followup assistant reply still arrives intact (the fix doesn't disturb content delivery).
        AllSentText(calls).ShouldContain(t => t.Contains("Here's what I found."));
    }

    [Fact]
    public async Task ToolActivity_ErrorResult_UsesWarningGlyphAndFailedStatus()
    {
        var calls = new List<CapturedCall>();
        var adapter = CreateRichAdapter(calls, richSucceeds: true, allowedChatId: -1001);
        var target = StreamTargets.For("-1001");

        await adapter.SendStreamEventAsync(target, new AgentStreamEvent { Type = AgentStreamEventType.ToolStart, ToolName = "shell", ToolCallId = "c2" });
        await adapter.SendStreamEventAsync(target, new AgentStreamEvent { Type = AgentStreamEventType.ToolEnd, ToolName = "shell", ToolCallId = "c2", ToolIsError = true });

        var texts = AllSentText(calls);
        texts.ShouldContain(t => t.Contains(ToolGlyphs.ForTool("shell")) && t.Contains("shell")); // start uses the tool glyph
        texts.ShouldContain(t => t.Contains("\u26A0") && t.Contains("failed")); // failed end uses warning glyph (U+26A0)
    }

    [Fact]
    public async Task ToolActivity_Disabled_SendsNoToolMessages()
    {
        var calls = new List<CapturedCall>();
        var adapter = CreateRichAdapter(calls, richSucceeds: true, allowedChatId: -1001, showToolActivity: false);
        var target = StreamTargets.For("-1001");

        await adapter.SendStreamEventAsync(target, new AgentStreamEvent { Type = AgentStreamEventType.ToolStart, ToolName = "read", ToolCallId = "c3" });
        await adapter.SendStreamEventAsync(target, new AgentStreamEvent { Type = AgentStreamEventType.ToolEnd, ToolName = "read", ToolCallId = "c3", ToolIsError = false });

        AllSentText(calls).Where(t => t.Contains("read")).ShouldBeEmpty();
    }

    // Collects the user-visible text from every outbound message variant (plain sendMessage, edited
    // text, and rich markdown) so assertions don't depend on which delivery path a chat type uses.
    private static List<string> AllSentText(List<CapturedCall> calls)
    {
        var texts = new List<string>();
        foreach (var c in calls)
        {
            using var j = JsonDocument.Parse(c.Body);
            var root = j.RootElement;
            if (root.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                texts.Add(t.GetString() ?? string.Empty);
            else if (root.TryGetProperty("rich_message", out var rm) && rm.TryGetProperty("markdown", out var md) && md.ValueKind == JsonValueKind.String)
                texts.Add(md.GetString() ?? string.Empty);
        }
        return texts;
    }

    // ── harness ──────────────────────────────────────────────────────────────

    private static TelegramChannelAdapter CreateRichAdapter(
        List<CapturedCall> calls,
        bool richSucceeds,
        bool richEnabled = true,
        long allowedChatId = 42,
        bool draftSucceeds = true,
        bool showToolActivity = true)
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

            if (method == "sendRichMessageDraft" && !draftSucceeds)
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(
                        "{\"ok\":false,\"error_code\":400,\"description\":\"Bad Request: DRAFT_REJECTED\"}",
                        Encoding.UTF8, "application/json")
                };
            }

            object result = method switch
            {
                "sendRichMessage" or "sendMessage" or "editMessageText"
                    => new TelegramMessage { MessageId = 1, Chat = new TelegramChat { Id = 42 } },
                _ => true
            };
            return JsonOk(result);
        });

        var options = new TelegramGatewayOptions
        {
            BotToken = "token",
            AllowedChatIds = { allowedChatId },
            RichMessages = richEnabled,
            ShowToolActivity = showToolActivity
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
