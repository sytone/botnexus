using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BotNexus.Extensions.Mcp.Protocol;
using BotNexus.Extensions.Mcp.Transport;
using FluentAssertions;

namespace BotNexus.Extensions.Mcp.Tests.Transport;

public sealed class HttpSseTransportRobustnessTests
{
    [Fact]
    [Trait("Category", "Security")]
    public async Task ConnectionTimeout_ThrowsTimeoutException()
    {
        var handler = new DelayedHandler(TimeSpan.FromSeconds(2));
        using var client = new HttpClient(handler);
        var transport = new HttpSseMcpTransport(new Uri("http://localhost/mcp"), httpClient: client, connectTimeout: TimeSpan.FromMilliseconds(100));

        var act = () => transport.ConnectAsync();
        await act.Should().ThrowAsync<TimeoutException>();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task MalformedSseEvents_AreSkippedGracefully()
    {
        var transport = new HttpSseMcpTransport(new Uri("http://localhost/mcp"));
        var sse = "event: message\ndata: {bad-json\n\n";
        await transport.ParseSseStreamAsync(new StringReader(sse), CancellationToken.None);

        var act = () => transport.ReceiveAsync(new CancellationTokenSource(50).Token);
        await act.Should().ThrowAsync<Exception>();
        await transport.DisposeAsync();
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Category", "SecurityGap")]
    public async Task VeryLargeSseEvent_IsParsedWithoutExplicitSizeLimit_CurrentBehavior()
    {
        var transport = new HttpSseMcpTransport(new Uri("http://localhost/mcp"));
        var payload = new string('x', 1024 * 1024);
        var response = new JsonRpcResponse { Id = 1, Result = JsonSerializer.SerializeToElement(new { value = payload }) };
        var sse = $"event: message\ndata: {JsonSerializer.Serialize(response, JsonContext.Default.JsonRpcResponse)}\n\n";

        await transport.ParseSseStreamAsync(new StringReader(sse), CancellationToken.None);
        var read = await transport.ReceiveAsync(new CancellationTokenSource(TimeSpan.FromSeconds(1)).Token);
        read.Id.Should().NotBeNull();
        await transport.DisposeAsync();
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Category", "SecurityGap")]
    public async Task DroppedConnectionMidStream_DoesNotSurfaceExplicitError_CurrentBehavior()
    {
        var transport = new HttpSseMcpTransport(new Uri("http://localhost/mcp"));
        await transport.ParseSseStreamAsync(new StringReader("event: message\ndata: {"), CancellationToken.None);

        var act = () => transport.ReceiveAsync(new CancellationTokenSource(50).Token);
        await act.Should().ThrowAsync<Exception>();
        await transport.DisposeAsync();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task RapidReconnectAttempts_RespectMaxReconnectAttempts()
    {
        var handler = new CountingFailHandler();
        using var client = new HttpClient(handler);
        var transport = new HttpSseMcpTransport(new Uri("http://localhost/mcp"), httpClient: client, connectTimeout: TimeSpan.FromMilliseconds(200), maxReconnectAttempts: 2);

        var act = () => transport.ConnectAsync();
        await act.Should().ThrowAsync<HttpRequestException>();
        handler.CallCount.Should().Be(1);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task NonSuccessInitialConnect_ReturnsClearError()
    {
        var handler = new FixedResponseHandler(HttpStatusCode.BadGateway);
        using var client = new HttpClient(handler);
        var transport = new HttpSseMcpTransport(new Uri("http://localhost/mcp"), httpClient: client);

        var act = () => transport.ConnectAsync();
        await act.Should().ThrowAsync<HttpRequestException>();
    }

    private sealed class DelayedHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(string.Empty)
            };
        }
    }

    private sealed class CountingFailHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }
    }

    private sealed class FixedResponseHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent("error", Encoding.UTF8, "text/plain")
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
            return Task.FromResult(response);
        }
    }
}
