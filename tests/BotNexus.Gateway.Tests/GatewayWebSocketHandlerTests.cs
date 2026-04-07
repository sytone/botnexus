using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using BotNexus.Channels.WebSocket;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Api.WebSocket;
using BotNexus.Gateway.Sessions;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Gateway.Tests;

public sealed class GatewayWebSocketHandlerTests
{
    [Fact]
    public async Task HandleAsync_WithNonWebSocketRequest_ReturnsBadRequest()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var handler = CreateHandler();

        await handler.HandleAsync(context, CancellationToken.None);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task HandleAsync_WithMissingAgentQuery_ReturnsBadRequest()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.Features.Set<IHttpWebSocketFeature>(new TestWebSocketFeature
        {
            IsWebSocketRequest = true,
            Socket = new TestWebSocket()
        });
        var handler = CreateHandler();

        await handler.HandleAsync(context, CancellationToken.None);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task HandleAsync_OnConnection_SendsConnectedMessageWithConnectionAndSessionIds()
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?agent=agent-a&session=session-123");
        var socket = new TestWebSocket();
        context.Features.Set<IHttpWebSocketFeature>(new TestWebSocketFeature
        {
            IsWebSocketRequest = true,
            Socket = socket
        });
        var handler = CreateHandler();

        await handler.HandleAsync(context, CancellationToken.None);
        var payload = JsonDocument.Parse(Encoding.UTF8.GetString(socket.SentMessages.Single()));

        payload.RootElement.GetProperty("type").GetString().Should().Be("connected");
        payload.RootElement.GetProperty("sessionId").GetString().Should().Be("session-123");
        payload.RootElement.GetProperty("connectionId").GetString().Should().NotBeNullOrEmpty();
        payload.RootElement.GetProperty("sequenceId").GetInt64().Should().Be(1);
    }

    [Fact]
    public async Task HandleAsync_WithReconnect_ReplaysMissedEventsFromSequence()
    {
        var store = new InMemorySessionStore();

        var firstContext = new DefaultHttpContext();
        firstContext.Request.QueryString = new QueryString("?agent=agent-a&session=session-123");
        var firstSocket = new TestWebSocket();
        firstSocket.QueueIncomingText("""{"type":"ping"}""");
        firstContext.Features.Set<IHttpWebSocketFeature>(new TestWebSocketFeature { IsWebSocketRequest = true, Socket = firstSocket });

        var handler = CreateHandler(sessions: store);
        await handler.HandleAsync(firstContext, CancellationToken.None);

        var reconnectContext = new DefaultHttpContext();
        reconnectContext.Request.QueryString = new QueryString("?agent=agent-a&session=session-123");
        var reconnectSocket = new TestWebSocket();
        reconnectSocket.QueueIncomingText("""{"type":"reconnect","sessionKey":"session-123","lastSeqId":1}""");
        reconnectContext.Features.Set<IHttpWebSocketFeature>(new TestWebSocketFeature { IsWebSocketRequest = true, Socket = reconnectSocket });

        await handler.HandleAsync(reconnectContext, CancellationToken.None);

        reconnectSocket.SentMessages.Should().NotBeEmpty();
        var reconnectPayloads = reconnectSocket.SentMessages
            .Select(bytes => JsonDocument.Parse(Encoding.UTF8.GetString(bytes)).RootElement.Clone())
            .ToList();

        var hasReconnectAck = reconnectPayloads.Any(payload =>
            payload.TryGetProperty("type", out var type) &&
            type.GetString() == "reconnect_ack" &&
            payload.TryGetProperty("replayed", out var replayed) &&
            replayed.GetInt32() >= 1);
        hasReconnectAck.Should().BeTrue();
    }

    [Fact]
    public async Task HandleAsync_WithReconnectOnUnknownSession_SendsSessionNotFoundError()
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?agent=agent-a&session=session-123");
        var socket = new TestWebSocket();
        socket.QueueIncomingText("""{"type":"reconnect","sessionKey":"missing","lastSeqId":0}""");
        context.Features.Set<IHttpWebSocketFeature>(new TestWebSocketFeature { IsWebSocketRequest = true, Socket = socket });
        var handler = CreateHandler();

        await handler.HandleAsync(context, CancellationToken.None);

        var payloads = ParsePayloads(socket.SentMessages);
        payloads.Any(payload => HasStringProperty(payload, "type", "error") && HasStringProperty(payload, "code", "SESSION_NOT_FOUND"))
            .Should().BeTrue();
    }

    [Fact]
    public async Task HandleAsync_WithReconnectAndNoMissedEvents_AcknowledgesZeroReplay()
    {
        var store = new InMemorySessionStore();

        var firstContext = new DefaultHttpContext();
        firstContext.Request.QueryString = new QueryString("?agent=agent-a&session=session-123");
        var firstSocket = new TestWebSocket();
        firstSocket.QueueIncomingText("""{"type":"ping"}""");
        firstContext.Features.Set<IHttpWebSocketFeature>(new TestWebSocketFeature { IsWebSocketRequest = true, Socket = firstSocket });

        var handler = CreateHandler(sessions: store);
        await handler.HandleAsync(firstContext, CancellationToken.None);

        var reconnectContext = new DefaultHttpContext();
        reconnectContext.Request.QueryString = new QueryString("?agent=agent-a&session=session-123");
        var reconnectSocket = new TestWebSocket();
        reconnectSocket.QueueIncomingText("""{"type":"reconnect","sessionKey":"session-123","lastSeqId":999}""");
        reconnectContext.Features.Set<IHttpWebSocketFeature>(new TestWebSocketFeature { IsWebSocketRequest = true, Socket = reconnectSocket });

        await handler.HandleAsync(reconnectContext, CancellationToken.None);

        var reconnectPayloads = ParsePayloads(reconnectSocket.SentMessages);
        reconnectPayloads.Any(payload => HasStringProperty(payload, "type", "reconnect_ack") && HasIntProperty(payload, "replayed", 0))
            .Should().BeTrue();
    }

    [Fact]
    public async Task HandleAsync_WithSmallReplayWindow_ReplaysOnlyBoundedEvents()
    {
        var store = new InMemorySessionStore();

        var firstContext = new DefaultHttpContext();
        firstContext.Request.QueryString = new QueryString("?agent=agent-a&session=session-123");
        var firstSocket = new TestWebSocket();
        firstSocket.QueueIncomingText("""{"type":"ping"}""");
        firstSocket.QueueIncomingText("""{"type":"ping"}""");
        firstContext.Features.Set<IHttpWebSocketFeature>(new TestWebSocketFeature { IsWebSocketRequest = true, Socket = firstSocket });

        var handler = CreateHandler(
            sessions: store,
            options: Options.Create(new GatewayWebSocketOptions { ReplayWindowSize = 2 }));
        await handler.HandleAsync(firstContext, CancellationToken.None);

        var reconnectContext = new DefaultHttpContext();
        reconnectContext.Request.QueryString = new QueryString("?agent=agent-a&session=session-123");
        var reconnectSocket = new TestWebSocket();
        reconnectSocket.QueueIncomingText("""{"type":"reconnect","sessionKey":"session-123","lastSeqId":0}""");
        reconnectContext.Features.Set<IHttpWebSocketFeature>(new TestWebSocketFeature { IsWebSocketRequest = true, Socket = reconnectSocket });

        await handler.HandleAsync(reconnectContext, CancellationToken.None);

        var reconnectPayloads = ParsePayloads(reconnectSocket.SentMessages);
        reconnectPayloads.Any(payload => HasStringProperty(payload, "type", "reconnect_ack") && HasIntProperty(payload, "replayed", 2))
            .Should().BeTrue();
    }

    [Fact]
    public async Task HandleAsync_WithSteerMessage_QueuesSteeringOnHandle()
    {
        var handle = new Mock<IAgentHandle>();
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetInstance("agent-a", "session-123"))
            .Returns(new AgentInstance
            {
                InstanceId = "agent-a::session-123",
                AgentId = "agent-a",
                SessionId = "session-123",
                IsolationStrategy = "in-process"
            });
        supervisor.Setup(s => s.GetOrCreateAsync("agent-a", "session-123", It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?agent=agent-a&session=session-123");
        var socket = new TestWebSocket();
        socket.QueueIncomingText("""{"type":"steer","content":"adjust"}""");
        context.Features.Set<IHttpWebSocketFeature>(new TestWebSocketFeature { IsWebSocketRequest = true, Socket = socket });
        var handler = CreateHandler(supervisor.Object);

        await handler.HandleAsync(context, CancellationToken.None);

        handle.Verify(h => h.SteerAsync("adjust", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_WithFollowUpMessage_QueuesFollowUpOnHandle()
    {
        var handle = new Mock<IAgentHandle>();
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetInstance("agent-a", "session-123"))
            .Returns(new AgentInstance
            {
                InstanceId = "agent-a::session-123",
                AgentId = "agent-a",
                SessionId = "session-123",
                IsolationStrategy = "in-process"
            });
        supervisor.Setup(s => s.GetOrCreateAsync("agent-a", "session-123", It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?agent=agent-a&session=session-123");
        var socket = new TestWebSocket();
        socket.QueueIncomingText("""{"type":"follow_up","content":"next"}""");
        context.Features.Set<IHttpWebSocketFeature>(new TestWebSocketFeature { IsWebSocketRequest = true, Socket = socket });
        var handler = CreateHandler(supervisor.Object);

        await handler.HandleAsync(context, CancellationToken.None);

        handle.Verify(h => h.FollowUpAsync("next", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_WithAbortMessage_AbortsActiveHandle()
    {
        var handle = new Mock<IAgentHandle>();
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetInstance("agent-a", "session-123"))
            .Returns(new AgentInstance
            {
                InstanceId = "agent-a::session-123",
                AgentId = "agent-a",
                SessionId = "session-123",
                IsolationStrategy = "in-process"
            });
        supervisor.Setup(s => s.GetOrCreateAsync("agent-a", "session-123", It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?agent=agent-a&session=session-123");
        var socket = new TestWebSocket();
        socket.QueueIncomingText("""{"type":"abort"}""");
        context.Features.Set<IHttpWebSocketFeature>(new TestWebSocketFeature { IsWebSocketRequest = true, Socket = socket });
        var handler = CreateHandler(supervisor.Object);

        await handler.HandleAsync(context, CancellationToken.None);

        handle.Verify(h => h.AbortAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_WithResetMessage_StopsAgentAndDeletesSession()
    {
        var store = new InMemorySessionStore();
        await store.GetOrCreateAsync("session-123", "agent-a", CancellationToken.None);

        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetInstance("agent-a", "session-123"))
            .Returns(new AgentInstance
            {
                InstanceId = "agent-a::session-123",
                AgentId = "agent-a",
                SessionId = "session-123",
                IsolationStrategy = "in-process"
            });
        supervisor.Setup(s => s.StopAsync("agent-a", "session-123", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?agent=agent-a&session=session-123");
        var socket = new TestWebSocket();
        socket.QueueIncomingText("""{"type":"reset"}""");
        context.Features.Set<IHttpWebSocketFeature>(new TestWebSocketFeature { IsWebSocketRequest = true, Socket = socket });
        var handler = CreateHandler(supervisor.Object, sessions: store);

        await handler.HandleAsync(context, CancellationToken.None);

        supervisor.Verify(s => s.StopAsync("agent-a", "session-123", It.IsAny<CancellationToken>()), Times.Once);
        (await store.GetAsync("session-123", CancellationToken.None)).Should().BeNull();
        var payloads = ParsePayloads(socket.SentMessages);
        payloads.Any(payload => HasStringProperty(payload, "type", "session_reset")).Should().BeTrue();
        socket.LastCloseDescription.Should().Be("Session reset");
    }

    [Fact]
    public async Task HandleAsync_WithNewMessage_ResetsSession()
    {
        var store = new InMemorySessionStore();
        await store.GetOrCreateAsync("session-123", "agent-a", CancellationToken.None);

        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?agent=agent-a&session=session-123");
        var socket = new TestWebSocket();
        socket.QueueIncomingText("""{"type":"new"}""");
        context.Features.Set<IHttpWebSocketFeature>(new TestWebSocketFeature { IsWebSocketRequest = true, Socket = socket });
        var handler = CreateHandler(sessions: store);

        await handler.HandleAsync(context, CancellationToken.None);

        (await store.GetAsync("session-123", CancellationToken.None)).Should().BeNull();
        var payloads = ParsePayloads(socket.SentMessages);
        payloads.Any(payload => HasStringProperty(payload, "type", "session_reset")).Should().BeTrue();
    }

    [Fact]
    public async Task HandleAsync_WithPingMessage_SendsPongPayload()
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?agent=agent-a&session=session-123");
        var socket = new TestWebSocket();
        socket.QueueIncomingText("""{"type":"ping"}""");
        context.Features.Set<IHttpWebSocketFeature>(new TestWebSocketFeature { IsWebSocketRequest = true, Socket = socket });
        var handler = CreateHandler();

        await handler.HandleAsync(context, CancellationToken.None);

        var payloads = ParsePayloads(socket.SentMessages);
        payloads.Any(payload => HasStringProperty(payload, "type", "pong")).Should().BeTrue();
    }

    [Fact]
    public async Task HandleAsync_WithSteerMessageWithoutSession_SendsSessionNotFoundError()
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?agent=agent-a&session=session-123");
        var socket = new TestWebSocket();
        socket.QueueIncomingText("""{"type":"steer","content":"adjust"}""");
        context.Features.Set<IHttpWebSocketFeature>(new TestWebSocketFeature { IsWebSocketRequest = true, Socket = socket });
        var handler = CreateHandler();

        await handler.HandleAsync(context, CancellationToken.None);

        var payloads = ParsePayloads(socket.SentMessages);
        payloads.Any(payload => HasStringProperty(payload, "type", "error") && HasStringProperty(payload, "code", "SESSION_NOT_FOUND"))
            .Should().BeTrue();
    }

    [Fact]
    public async Task HandleAsync_WithReconnectForDifferentAgent_SendsSessionNotFoundError()
    {
        var store = new InMemorySessionStore();
        await store.GetOrCreateAsync("session-123", "agent-b", CancellationToken.None);

        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?agent=agent-a&session=session-123");
        var socket = new TestWebSocket();
        socket.QueueIncomingText("""{"type":"reconnect","sessionKey":"session-123","lastSeqId":0}""");
        context.Features.Set<IHttpWebSocketFeature>(new TestWebSocketFeature { IsWebSocketRequest = true, Socket = socket });
        var handler = CreateHandler(sessions: store);

        await handler.HandleAsync(context, CancellationToken.None);

        var payloads = ParsePayloads(socket.SentMessages);
        payloads.Any(payload => HasStringProperty(payload, "type", "error") && HasStringProperty(payload, "code", "SESSION_NOT_FOUND"))
            .Should().BeTrue();
    }

    [Fact]
    public async Task HandleAsync_WithMessage_DispatchesInboundViaChannelPipeline()
    {
        var dispatcher = new Mock<IChannelDispatcher>();
        var channelAdapter = new WebSocketChannelAdapter(NullLogger<WebSocketChannelAdapter>.Instance);
        await channelAdapter.StartAsync(dispatcher.Object, CancellationToken.None);

        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?agent=agent-a&session=session-123");
        var socket = new TestWebSocket();
        socket.QueueIncomingText("""{"type":"message","content":"hello"}""");
        context.Features.Set<IHttpWebSocketFeature>(new TestWebSocketFeature { IsWebSocketRequest = true, Socket = socket });
        var handler = CreateHandler(channelAdapter: channelAdapter);

        await handler.HandleAsync(context, CancellationToken.None);

        dispatcher.Verify(d => d.DispatchAsync(
            It.Is<BotNexus.Gateway.Abstractions.Models.InboundMessage>(m =>
                m.ChannelType == "websocket" &&
                m.TargetAgentId == "agent-a" &&
                m.SessionId == "session-123" &&
                m.Content == "hello"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_WithSlashResetMessage_ResetsSession()
    {
        var store = new InMemorySessionStore();
        await store.GetOrCreateAsync("session-123", "agent-a", CancellationToken.None);

        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?agent=agent-a&session=session-123");
        var socket = new TestWebSocket();
        socket.QueueIncomingText("""{"type":"message","content":"/reset"}""");
        context.Features.Set<IHttpWebSocketFeature>(new TestWebSocketFeature { IsWebSocketRequest = true, Socket = socket });
        var handler = CreateHandler(sessions: store);

        await handler.HandleAsync(context, CancellationToken.None);

        (await store.GetAsync("session-123", CancellationToken.None)).Should().BeNull();
        var payloads = ParsePayloads(socket.SentMessages);
        payloads.Any(payload => HasStringProperty(payload, "type", "session_reset")).Should().BeTrue();
    }

    [Fact]
    public async Task HandleAsync_WithDuplicateSessionConnection_ClosesSecondSocket()
    {
        var handler = CreateHandler();

        var firstContext = new DefaultHttpContext();
        firstContext.Request.QueryString = new QueryString("?agent=agent-a&session=session-123");
        var firstSocket = new BlockingWebSocket();
        firstContext.Features.Set<IHttpWebSocketFeature>(new TestWebSocketFeature
        {
            IsWebSocketRequest = true,
            Socket = firstSocket
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var firstConnectionTask = handler.HandleAsync(firstContext, cts.Token);
        await firstSocket.ReceiveStarted.Task.WaitAsync(cts.Token);

        var secondContext = new DefaultHttpContext();
        secondContext.Request.QueryString = new QueryString("?agent=agent-a&session=session-123");
        var secondSocket = new TestWebSocket();
        secondContext.Features.Set<IHttpWebSocketFeature>(new TestWebSocketFeature
        {
            IsWebSocketRequest = true,
            Socket = secondSocket
        });

        await handler.HandleAsync(secondContext, cts.Token);

        secondSocket.LastCloseStatus.Should().Be((WebSocketCloseStatus)4409);
        secondSocket.LastCloseDescription.Should().Be("Session already has an active connection");

        firstSocket.AllowClose();
        await firstConnectionTask;
    }

    private static List<JsonElement> ParsePayloads(IEnumerable<byte[]> sentMessages)
        => sentMessages
            .Select(bytes => JsonDocument.Parse(Encoding.UTF8.GetString(bytes)).RootElement.Clone())
            .ToList();

    private static bool HasStringProperty(JsonElement payload, string name, string expectedValue)
        => payload.TryGetProperty(name, out var value) &&
           string.Equals(value.GetString(), expectedValue, StringComparison.Ordinal);

    private static bool HasIntProperty(JsonElement payload, string name, int expectedValue)
        => payload.TryGetProperty(name, out var value) &&
           value.ValueKind == JsonValueKind.Number &&
           value.GetInt32() == expectedValue;

    private static GatewayWebSocketHandler CreateHandler(
        IAgentSupervisor? supervisor = null,
        WebSocketChannelAdapter? channelAdapter = null,
        InMemorySessionStore? sessions = null,
        IOptions<GatewayWebSocketOptions>? options = null)
    {
        var resolvedOptions = options ?? Options.Create(new GatewayWebSocketOptions());
        var resolvedChannelAdapter = channelAdapter ?? new WebSocketChannelAdapter(NullLogger<WebSocketChannelAdapter>.Instance);
        var resolvedSessions = sessions ?? new InMemorySessionStore();
        var connectionManager = new WebSocketConnectionManager(resolvedOptions, NullLogger<WebSocketConnectionManager>.Instance);
        var dispatcher = new WebSocketMessageDispatcher(
            supervisor ?? Mock.Of<IAgentSupervisor>(),
            resolvedChannelAdapter,
            resolvedSessions,
            resolvedOptions,
            connectionManager,
            NullLogger<WebSocketMessageDispatcher>.Instance);

        return new GatewayWebSocketHandler(
            resolvedChannelAdapter,
            resolvedSessions,
            resolvedOptions,
            connectionManager,
            dispatcher,
            NullLogger<GatewayWebSocketHandler>.Instance);
    }

    private sealed class TestWebSocketFeature : IHttpWebSocketFeature
    {
        public required WebSocket Socket { get; init; }

        public bool IsWebSocketRequest { get; init; }

        public Task<WebSocket> AcceptAsync(WebSocketAcceptContext context)
            => Task.FromResult(Socket);
    }

    private sealed class TestWebSocket : WebSocket
    {
        private WebSocketState _state = WebSocketState.Open;
        private readonly Queue<byte[]> _incomingMessages = new();

        public List<byte[]> SentMessages { get; } = [];
        public WebSocketCloseStatus? LastCloseStatus { get; private set; }
        public string? LastCloseDescription { get; private set; }

        public void QueueIncomingText(string text)
            => _incomingMessages.Enqueue(Encoding.UTF8.GetBytes(text));

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public override void Abort()
            => _state = WebSocketState.Aborted;

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            LastCloseStatus = closeStatus;
            LastCloseDescription = statusDescription;
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _state = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            if (_incomingMessages.Count == 0)
            {
                _state = WebSocketState.CloseReceived;
                return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
            }

            var payload = _incomingMessages.Dequeue();
            payload.CopyTo(buffer.Array!, buffer.Offset);
            return Task.FromResult(new WebSocketReceiveResult(payload.Length, WebSocketMessageType.Text, true));
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            SentMessages.Add(buffer.ToArray());
            return Task.CompletedTask;
        }

        public override void Dispose()
            => _state = WebSocketState.Closed;
    }

    private sealed class BlockingWebSocket : WebSocket
    {
        private WebSocketState _state = WebSocketState.Open;
        private readonly TaskCompletionSource _allowClose = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> ReceiveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void AllowClose() => _allowClose.TrySetResult();

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public override void Abort()
            => _state = WebSocketState.Aborted;

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _state = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            ReceiveStarted.TrySetResult(true);
            await _allowClose.Task.WaitAsync(cancellationToken);
            _state = WebSocketState.CloseReceived;
            return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public override void Dispose()
            => _state = WebSocketState.Closed;
    }
}
