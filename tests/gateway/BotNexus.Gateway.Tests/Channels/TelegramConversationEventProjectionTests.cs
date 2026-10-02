using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using BotNexus.Domain.Gateway.Models;
using BotNexus.Domain.Primitives;
using BotNexus.Extensions.Channels.Telegram;
using BotNexus.Gateway.Abstractions.Events;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Tests.Channels;

public sealed class TelegramConversationEventProjectionTests
{
    [Fact]
    public async Task Publisher_MatchingNonMutedBinding_SendsFinalResponseOnce()
    {
        var telegram = new RecordingTelegramHandler();
        var adapter = CreateAdapter(telegram);
        await using var publisher = new ConversationEventPublisher([adapter]);
        var conversationId = ConversationId.Create();
        var sessionId = SessionId.Create();
        var binding = Binding("telegram", ChannelAddress.From("42"), BindingMode.Interactive);

        (await publisher.PublishAsync(AgentEvent(conversationId, sessionId, AgentStreamEventType.MessageStart, binding))).ShouldBeTrue();
        (await publisher.PublishAsync(AgentEvent(conversationId, sessionId, AgentStreamEventType.ContentDelta, binding, contentDelta: "**hello** "))).ShouldBeTrue();
        (await publisher.PublishAsync(AgentEvent(conversationId, sessionId, AgentStreamEventType.ContentDelta, binding, contentDelta: "world"))).ShouldBeTrue();
        (await publisher.PublishAsync(AgentEvent(conversationId, sessionId, AgentStreamEventType.MessageEnd, binding, finalContent: "**hello** world"))).ShouldBeTrue();

        await publisher.WaitForDrainAsync(TestTimeout());

        var sent = telegram.Sent.ShouldHaveSingleItem();
        sent.Method.ShouldBe("sendRichMessage");
        using var payload = JsonDocument.Parse(sent.Body);
        payload.RootElement.GetProperty("chat_id").GetInt64().ShouldBe(42);
        payload.RootElement.GetProperty("rich_message").GetProperty("markdown").GetString().ShouldBe("**hello** world");
    }

    [Fact]
    public async Task Publisher_UnrelatedAndMutedBindings_SendNothing()
    {
        var telegram = new RecordingTelegramHandler();
        var adapter = CreateAdapter(telegram);
        await using var publisher = new ConversationEventPublisher([adapter]);
        var conversationId = ConversationId.Create();
        var sessionId = SessionId.Create();

        (await publisher.PublishAsync(AgentEvent(
            conversationId, sessionId, AgentStreamEventType.MessageEnd,
            Binding("matrix", ChannelAddress.From("42"), BindingMode.Interactive), finalContent: "unrelated"))).ShouldBeTrue();
        (await publisher.PublishAsync(AgentEvent(
            conversationId, sessionId, AgentStreamEventType.MessageEnd,
            Binding("telegram", ChannelAddress.From("43"), BindingMode.Muted), finalContent: "muted"))).ShouldBeTrue();

        await publisher.WaitForDrainAsync(TestTimeout());
        telegram.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task Publisher_UnsupportedLifecycleEvent_SendsNothing()
    {
        var telegram = new RecordingTelegramHandler();
        var adapter = CreateAdapter(telegram);
        await using var publisher = new ConversationEventPublisher([adapter]);

        (await publisher.PublishAsync(new ConversationCreatedEvent
        {
            AgentId = AgentId.From("farnsworth"),
            ConversationId = ConversationId.Create(),
            Bindings = [Binding("telegram", ChannelAddress.From("42"), BindingMode.Interactive)],
            Title = "new conversation",
        })).ShouldBeTrue();

        await publisher.WaitForDrainAsync(TestTimeout());
        telegram.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task Publisher_ToolThinkingAndFinalEvents_PreservesDeliveryOrderAndRenderingPolicy()
    {
        var telegram = new RecordingTelegramHandler();
        var adapter = CreateAdapter(telegram);
        await using var publisher = new ConversationEventPublisher([adapter]);
        var conversationId = ConversationId.Create();
        var sessionId = SessionId.Create();
        var binding = Binding("telegram", ChannelAddress.From("42"), BindingMode.Interactive);

        (await publisher.PublishAsync(AgentEvent(conversationId, sessionId, AgentStreamEventType.MessageStart, binding))).ShouldBeTrue();
        (await publisher.PublishAsync(AgentEvent(conversationId, sessionId, AgentStreamEventType.ContentDelta, binding, contentDelta: "Checking."))).ShouldBeTrue();
        (await publisher.PublishAsync(AgentEvent(conversationId, sessionId, AgentStreamEventType.MessageEnd, binding, finalContent: "Checking."))).ShouldBeTrue();
        (await publisher.PublishAsync(AgentEvent(conversationId, sessionId, AgentStreamEventType.ToolStart, binding, toolName: "read"))).ShouldBeTrue();
        (await publisher.PublishAsync(AgentEvent(conversationId, sessionId, AgentStreamEventType.ToolEnd, binding, toolName: "read", toolIsError: false))).ShouldBeTrue();
        (await publisher.PublishAsync(AgentEvent(conversationId, sessionId, AgentStreamEventType.MessageStart, binding))).ShouldBeTrue();
        (await publisher.PublishAsync(AgentEvent(conversationId, sessionId, AgentStreamEventType.ThinkingDelta, binding, thinkingContent: "short plan"))).ShouldBeTrue();
        (await publisher.PublishAsync(AgentEvent(conversationId, sessionId, AgentStreamEventType.ContentDelta, binding, contentDelta: "Final answer."))).ShouldBeTrue();
        (await publisher.PublishAsync(AgentEvent(conversationId, sessionId, AgentStreamEventType.MessageEnd, binding, finalContent: "Thinking: short plan\nFinal answer."))).ShouldBeTrue();

        await publisher.WaitForDrainAsync(TestTimeout());

        var sent = telegram.Sent;
        sent.Select(call => call.Method).ShouldBe(new[] { "sendRichMessage", "sendMessage", "sendMessage", "sendRichMessage" });
        var text = sent.Select(VisibleText).ToArray();
        text[0].ShouldBe("Checking.");
        text[1].ShouldContain("read");
        text[2].ShouldContain("read");
        text[3].ShouldBe("Thinking: short plan\nFinal answer.");
    }

    private static TelegramChannelAdapter CreateAdapter(RecordingTelegramHandler handler)
    {
        var options = new TelegramGatewayOptions
        {
            BotToken = "token",
            AllowedChatIds = { 42 },
        };
        return new TelegramChannelAdapter(
            NullLogger<TelegramChannelAdapter>.Instance,
            Options.Create(options),
            new StubHttpClientFactory(handler));
    }

    private static ConversationBindingSnapshot Binding(string channel, ChannelAddress address, BindingMode mode)
        => new(BindingId.Create(), ChannelKey.From(channel), AdapterId: null, address, mode, ThreadingMode.Single);

    private static ConversationAgentEvent AgentEvent(
        ConversationId conversationId,
        SessionId sessionId,
        AgentStreamEventType type,
        ConversationBindingSnapshot binding,
        string? contentDelta = null,
        string? thinkingContent = null,
        string? finalContent = null,
        string? toolName = null,
        bool? toolIsError = null)
        => new()
        {
            AgentId = AgentId.From("farnsworth"),
            ConversationId = conversationId,
            SessionId = sessionId,
            Bindings = ImmutableArray.Create(binding),
            StreamEvent = new AgentStreamEvent
            {
                Type = type,
                ContentDelta = contentDelta,
                ThinkingContent = thinkingContent,
                FinalContent = finalContent,
                ToolName = toolName,
                ToolIsError = toolIsError,
                AgentId = AgentId.From("farnsworth"),
                ConversationId = conversationId,
                SessionId = sessionId,
            },
        };

    private static string VisibleText(RecordedCall call)
    {
        using var payload = JsonDocument.Parse(call.Body);
        var root = payload.RootElement;
        if (root.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            return text.GetString() ?? string.Empty;
        if (root.TryGetProperty("rich_message", out var rich) && rich.TryGetProperty("markdown", out var markdown))
            return markdown.GetString() ?? string.Empty;
        return string.Empty;
    }

    private static CancellationToken TestTimeout()
        => new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;

    private sealed record RecordedCall(string Method, string Body);

    private sealed class RecordingTelegramHandler : HttpMessageHandler
    {
        private readonly ConcurrentQueue<RecordedCall> _sent = new();
        public IReadOnlyList<RecordedCall> Sent => _sent.ToArray();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var method = request.RequestUri?.Segments.LastOrDefault()?.Trim('/') ?? string.Empty;
            var body = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);
            _sent.Enqueue(new RecordedCall(method, body));

            object result = method switch
            {
                "sendRichMessage" or "sendMessage" or "editMessageText"
                    => new TelegramMessage { MessageId = _sent.Count, Chat = new TelegramChat { Id = 42 } },
                _ => true
            };
            var response = JsonSerializer.Serialize(new TelegramApiResponse<object> { Ok = true, Result = result });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class StubHttpClientFactory(RecordingTelegramHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
