using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Api.WebSocket;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging.Abstractions;
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
    }

    private static GatewayWebSocketHandler CreateHandler()
        => new(
            Mock.Of<IAgentSupervisor>(),
            Mock.Of<ISessionStore>(),
            Mock.Of<IActivityBroadcaster>(),
            NullLogger<GatewayWebSocketHandler>.Instance);

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

        public List<byte[]> SentMessages { get; } = [];

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

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
            => Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            SentMessages.Add(buffer.ToArray());
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override void Dispose()
            => _state = WebSocketState.Closed;
    }
}
