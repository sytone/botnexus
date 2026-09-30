using System.Net;
using System.Text;
using BotNexus.Agent.Providers.Core.Logging;
using Microsoft.Extensions.Logging;
using Moq;

namespace BotNexus.Agent.Providers.Core.Tests.Logging;

public sealed class ProviderExceptionalDiagnosticsHandlerTests
{
    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task ServerFailure_EmitsOnePayloadFreeTerminalEvent(HttpStatusCode statusCode)
    {
        const string secret = "synthetic-provider-secret";
        var (handler, events) = CreateHandler(statusCode, $"{{\"error\":\"{secret}\"}}");
        using var invoker = new HttpMessageInvoker(handler);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://api.example.test/v1/messages?api_key={secret}")
        {
            Content = new StringContent($"{{\"prompt\":\"{secret}\"}}", Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {secret}");

        using var response = await invoker.SendAsync(request, CancellationToken.None);

        var terminalEvent = Assert.Single(events);
        Assert.Equal(LogLevel.Warning, terminalEvent.Level);
        Assert.Equal(ProviderExceptionalDiagnosticsHandler.TerminalFailureEventId, terminalEvent.EventId);
        Assert.Contains(((int)statusCode).ToString(), terminalEvent.Message);
        Assert.Contains("api.example.test", terminalEvent.Message);
        Assert.DoesNotContain(secret, terminalEvent.Message);
        Assert.DoesNotContain("api_key", terminalEvent.Message);
        Assert.DoesNotContain("prompt", terminalEvent.Message);
        Assert.Equal($"{{\"error\":\"{secret}\"}}", await response.Content.ReadAsStringAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task NonServerFailure_EmitsNoExceptionalEvent(HttpStatusCode statusCode)
    {
        var (handler, events) = CreateHandler(statusCode, "{}");
        using var invoker = new HttpMessageInvoker(handler);

        using var response = await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "https://api.example.test/v1/messages"),
            CancellationToken.None);

        Assert.Equal(statusCode, response.StatusCode);
        Assert.Empty(events);
    }

    private static (ProviderExceptionalDiagnosticsHandler Handler, List<CapturedEvent> Events) CreateHandler(
        HttpStatusCode statusCode,
        string body)
    {
        var events = new List<CapturedEvent>();
        var logger = new Mock<ILogger<ProviderExceptionalDiagnosticsHandler>>();
        logger.Setup(value => value.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        logger
            .Setup(value => value.Log(
                It.IsAny<LogLevel>(),
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Callback<LogLevel, EventId, object, Exception?, Delegate>((level, eventId, state, _, formatter) =>
            {
                events.Add(new CapturedEvent(
                    level,
                    eventId,
                    formatter.DynamicInvoke(state, null) as string ?? string.Empty));
            });

        var handler = new ProviderExceptionalDiagnosticsHandler(logger.Object)
        {
            InnerHandler = new ResponseHandler(statusCode, body)
        };
        return (handler, events);
    }

    private sealed record CapturedEvent(LogLevel Level, EventId EventId, string Message);

    private sealed class ResponseHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }
}
