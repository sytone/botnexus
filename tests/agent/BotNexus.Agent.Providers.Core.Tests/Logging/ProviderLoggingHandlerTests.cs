using System.Net;
using System.Text;
using BotNexus.Agent.Providers.Core.Logging;
using Microsoft.Extensions.Logging;
using Moq;

namespace BotNexus.Agent.Providers.Core.Tests.Logging;

public class ProviderLoggingHandlerTests
{
    // ----- helpers -----

    private static (ProviderLoggingHandler handler, List<(LogLevel level, string msg)> logs) CreateHandler(
        bool debugEnabled = true, HttpMessageHandler? inner = null, Func<string, string>? secretRedactor = null)
    {
        var captured = new List<(LogLevel level, string msg)>();
        var loggerMock = new Mock<ILogger<ProviderLoggingHandler>>();
        loggerMock.Setup(l => l.IsEnabled(It.IsAny<LogLevel>()))
            .Returns<LogLevel>(l => l == LogLevel.Debug && debugEnabled);
        loggerMock
            .Setup(l => l.Log(
                It.IsAny<LogLevel>(),
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Callback<LogLevel, EventId, object, Exception?, Delegate>((lvl, _, state, _, formatter) =>
            {
                captured.Add((lvl, formatter.DynamicInvoke(state, null) as string ?? ""));
            });

        var handler = new ProviderLoggingHandler(loggerMock.Object, secretRedactor)
        {
            InnerHandler = inner ?? new OkHandler()
        };
        return (handler, captured);
    }

    private static HttpRequestMessage MakeRequest(string body = "{}", string url = "https://api.anthropic.com/v1/messages")
        => new(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    // ----- auth header redaction -----

    [Fact]
    public async Task AuthHeaders_AreAlwaysRedacted_XApiKey()
    {
        var (handler, logs) = CreateHandler();
        var invoker = new HttpMessageInvoker(handler);
        var req = MakeRequest();
        req.Headers.TryAddWithoutValidation("x-api-key", "sk-ant-secret");

        await invoker.SendAsync(req, CancellationToken.None);

        var requestLog = logs.First(l => l.msg.Contains("request"));
        Assert.Contains("[REDACTED]", requestLog.msg);
        Assert.DoesNotContain("sk-ant-secret", requestLog.msg);
    }

    [Fact]
    public async Task AuthHeaders_AreAlwaysRedacted_Authorization()
    {
        var (handler, logs) = CreateHandler();
        var invoker = new HttpMessageInvoker(handler);
        var req = MakeRequest();
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer sk-secret-token");

        await invoker.SendAsync(req, CancellationToken.None);

        var requestLog = logs.First(l => l.msg.Contains("request"));
        Assert.Contains("[REDACTED]", requestLog.msg);
        Assert.DoesNotContain("sk-secret-token", requestLog.msg);
    }

    [Fact]
    public async Task NonAuthHeaders_AreNotRedacted()
    {
        var (handler, logs) = CreateHandler();
        var invoker = new HttpMessageInvoker(handler);
        var req = MakeRequest();
        req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");

        await invoker.SendAsync(req, CancellationToken.None);

        var requestLog = logs.First(l => l.msg.Contains("request"));
        Assert.Contains("2023-06-01", requestLog.msg);
    }

    // ----- opt-in flag (debug disabled) -----

    [Fact]
    public async Task WhenDebugDisabled_NoLogsEmitted()
    {
        var (handler, logs) = CreateHandler(debugEnabled: false);
        var invoker = new HttpMessageInvoker(handler);
        var req = MakeRequest();

        await invoker.SendAsync(req, CancellationToken.None);

        Assert.Empty(logs);
    }

    [Fact]
    public async Task WhenDebugEnabled_RequestAndResponseLogged()
    {
        var (handler, logs) = CreateHandler(debugEnabled: true);
        var invoker = new HttpMessageInvoker(handler);
        var req = MakeRequest();

        await invoker.SendAsync(req, CancellationToken.None);

        Assert.Contains(logs, l => l.msg.Contains("request"));
        Assert.Contains(logs, l => l.msg.Contains("response"));
    }

    // ----- structured fields -----

    [Fact]
    public async Task RequestLog_ContainsMethodAndUrl()
    {
        var (handler, logs) = CreateHandler();
        var invoker = new HttpMessageInvoker(handler);
        var req = MakeRequest(url: "https://api.openai.com/v1/chat/completions");

        await invoker.SendAsync(req, CancellationToken.None);

        var requestLog = logs.First(l => l.msg.Contains("request"));
        Assert.Contains("POST", requestLog.msg);
        Assert.Contains("openai.com", requestLog.msg);
    }

    [Fact]
    public async Task ResponseLog_ContainsStatusCode()
    {
        var (handler, logs) = CreateHandler(inner: new StatusCodeHandler(HttpStatusCode.OK));
        var invoker = new HttpMessageInvoker(handler);

        await invoker.SendAsync(MakeRequest(), CancellationToken.None);

        var responseLog = logs.First(l => l.msg.Contains("response"));
        Assert.Contains("200", responseLog.msg);
    }

    // ----- streaming path -----

    [Fact]
    public async Task StreamingResponse_LogsStreamingMarker_NotBody()
    {
        var sseBody = "event: message_start\ndata: {}\n\n";
        var (handler, logs) = CreateHandler(inner: new SseHandler(sseBody));
        var invoker = new HttpMessageInvoker(handler);

        await invoker.SendAsync(MakeRequest(), CancellationToken.None);

        var responseLog = logs.First(l => l.msg.Contains("response"));
        Assert.Contains("Streaming", responseLog.msg);
        // SSE body must NOT be buffered/logged
        Assert.DoesNotContain("message_start", responseLog.msg);
    }

    // ----- body secret redaction (issue #453) -----

    // A stand-in for the gateway SecretRedactor: replaces any Anthropic-shaped key with [REDACTED].
    private static string StubRedact(string input)
        => System.Text.RegularExpressions.Regex.Replace(input, @"sk-ant-[A-Za-z0-9_\-]{6}", "[REDACTED]");

    [Fact]
    public async Task RequestBody_SecretsAreRedacted_ViaInjectedRedactor()
    {
        var (handler, logs) = CreateHandler(secretRedactor: StubRedact);
        var invoker = new HttpMessageInvoker(handler);
        var req = MakeRequest(body: "{\"api_key\":\"sk-ant-abcdef123456\"}");

        await invoker.SendAsync(req, CancellationToken.None);

        var requestLog = logs.First(l => l.msg.Contains("request"));
        Assert.Contains("[REDACTED]", requestLog.msg);
        Assert.DoesNotContain("sk-ant-abcdef123456", requestLog.msg);
    }

    [Fact]
    public async Task ResponseBody_SecretsAreRedacted_ViaInjectedRedactor()
    {
        var inner = new BodyHandler("{\"leaked\":\"sk-ant-zzzzzz999999\"}");
        var (handler, logs) = CreateHandler(inner: inner, secretRedactor: StubRedact);
        var invoker = new HttpMessageInvoker(handler);

        await invoker.SendAsync(MakeRequest(), CancellationToken.None);

        var responseLog = logs.First(l => l.msg.Contains("response"));
        Assert.Contains("[REDACTED]", responseLog.msg);
        Assert.DoesNotContain("sk-ant-zzzzzz999999", responseLog.msg);
    }

    // ----- token usage extraction (issue #453) -----

    [Fact]
    public async Task ResponseLog_IncludesUsage_WhenPresentInBody()
    {
        var inner = new BodyHandler("{\"usage\":{\"input_tokens\":12,\"output_tokens\":34}}");
        var (handler, logs) = CreateHandler(inner: inner);
        var invoker = new HttpMessageInvoker(handler);

        await invoker.SendAsync(MakeRequest(), CancellationToken.None);

        var responseLog = logs.First(l => l.msg.Contains("response"));
        Assert.Contains("input_tokens", responseLog.msg);
        Assert.Contains("34", responseLog.msg);
    }

    [Fact]
    public async Task ResponseLog_UsageIsNa_WhenAbsent()
    {
        var inner = new BodyHandler("{\"content\":[]}");
        var (handler, logs) = CreateHandler(inner: inner);
        var invoker = new HttpMessageInvoker(handler);

        await invoker.SendAsync(MakeRequest(), CancellationToken.None);

        var responseLog = logs.First(l => l.msg.Contains("response"));
        Assert.Contains("n/a", responseLog.msg);
    }

    // ----- streaming stays non-destructive (issue #453) -----

    [Fact]
    public async Task StreamingResponse_BodyStreamRemainsReadable_AfterLogging()
    {
        var sseBody = "event: message_start\ndata: {\"usage\":{\"output_tokens\":7}}\n\n";
        var (handler, _) = CreateHandler(inner: new SseHandler(sseBody));
        var invoker = new HttpMessageInvoker(handler);

        var response = await invoker.SendAsync(MakeRequest(), CancellationToken.None);

        // The caller must still be able to read the full, unconsumed stream body.
        var readBack = await response.Content.ReadAsStringAsync(CancellationToken.None);
        Assert.Equal(sseBody, readBack);
    }

    // ----- truncation -----

    [Fact]
    public async Task LargeBody_IsTruncatedInLog()
    {
        var bigBody = new string('x', 10_000);
        var (handler, logs) = CreateHandler();
        var invoker = new HttpMessageInvoker(handler);
        var req = MakeRequest(body: bigBody);

        await invoker.SendAsync(req, CancellationToken.None);

        var requestLog = logs.First(l => l.msg.Contains("request"));
        Assert.Contains("truncated", requestLog.msg);
    }

    // ----- inner handler stubs -----

    private sealed class OkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
    }

    private sealed class StatusCodeHandler(HttpStatusCode code) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(code)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
    }

    private sealed class BodyHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }

    private sealed class SseHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/event-stream")
            });
    }
}
