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
        bool debugEnabled = true, HttpMessageHandler? inner = null, Func<string, string>? secretRedactor = null,
        Func<bool>? isEnabled = null)
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

        var handler = new ProviderLoggingHandler(loggerMock.Object, secretRedactor, isEnabled)
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

    // ----- URL query-string credential redaction (issue #2669) -----

    [Fact]
    public async Task RequestUrl_SasSignature_IsRedacted_SchemeHostPathPreserved()
    {
        var (handler, logs) = CreateHandler();
        var invoker = new HttpMessageInvoker(handler);
        var req = MakeRequest(url: "https://acct.blob.core.windows.net/c/blob.txt?sv=2021-08-06&sig=abc123SIGVALUE%3D&se=2030-01-01");

        await invoker.SendAsync(req, CancellationToken.None);

        var requestLog = logs.First(l => l.msg.Contains("request"));
        Assert.DoesNotContain("abc123SIGVALUE", requestLog.msg);
        Assert.Contains("sig=[REDACTED]", requestLog.msg);
        // Scheme, host and path must survive verbatim so the log stays diagnostic.
        Assert.Contains("https://acct.blob.core.windows.net/c/blob.txt?", requestLog.msg);
        // Non-sensitive params keep their values.
        Assert.Contains("sv=2021-08-06", requestLog.msg);
        Assert.Contains("se=2030-01-01", requestLog.msg);
    }

    [Theory]
    [InlineData("sig")]
    [InlineData("key")]
    [InlineData("api_key")]
    [InlineData("apikey")]
    [InlineData("access_token")]
    [InlineData("token")]
    [InlineData("password")]
    [InlineData("secret")]
    [InlineData("SIG")]
    [InlineData("Api_Key")]
    [InlineData("x-goog-api-key")]
    [InlineData("X-Custom-Auth")]
    public async Task RequestUrl_SensitiveQueryParameterNames_AreRedacted(string paramName)
    {
        const string secretValue = "zzTOPSECRETvalue99";
        var (handler, logs) = CreateHandler();
        var invoker = new HttpMessageInvoker(handler);
        var req = MakeRequest(url: $"https://api.example.com/v1/models?{paramName}={secretValue}");

        await invoker.SendAsync(req, CancellationToken.None);

        var requestLog = logs.First(l => l.msg.Contains("request"));
        Assert.DoesNotContain(secretValue, requestLog.msg);
        Assert.Contains($"{paramName}=[REDACTED]", requestLog.msg);
        Assert.Contains("https://api.example.com/v1/models", requestLog.msg);
    }

    [Fact]
    public async Task RequestUrl_NonSensitiveQueryParameter_IsPreserved()
    {
        var (handler, logs) = CreateHandler();
        var invoker = new HttpMessageInvoker(handler);
        var req = MakeRequest(url: "https://api.example.com/v1/models?model=gpt-4o&stream=true");

        await invoker.SendAsync(req, CancellationToken.None);

        var requestLog = logs.First(l => l.msg.Contains("request"));
        Assert.Contains("model=gpt-4o", requestLog.msg);
        Assert.Contains("stream=true", requestLog.msg);
        Assert.DoesNotContain("[REDACTED]", requestLog.msg);
    }

    [Fact]
    public async Task RequestUrl_WithNoQueryString_RoundTripsUnchanged()
    {
        var (handler, logs) = CreateHandler();
        var invoker = new HttpMessageInvoker(handler);
        var req = MakeRequest(url: "https://api.anthropic.com/v1/messages");

        await invoker.SendAsync(req, CancellationToken.None);

        var requestLog = logs.First(l => l.msg.Contains("request"));
        Assert.Contains("https://api.anthropic.com/v1/messages", requestLog.msg);
        Assert.DoesNotContain("[REDACTED]", requestLog.msg);
    }

    [Fact]
    public async Task RequestUrl_Null_DoesNotThrow_AndLogsRequest()
    {
        var (handler, logs) = CreateHandler();
        var invoker = new HttpMessageInvoker(handler);
        var req = new HttpRequestMessage { Method = HttpMethod.Post, RequestUri = null };

        var response = await invoker.SendAsync(req, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(logs, l => l.msg.Contains("request"));
    }

    [Fact]
    public async Task ErrorLog_RedactsUrlCredentials()
    {
        var (handler, logs) = CreateHandler(inner: new ThrowingHandler());
        var invoker = new HttpMessageInvoker(handler);
        var req = MakeRequest(url: "https://api.example.com/v1/models?sig=errorPathSECRET77");

        await Assert.ThrowsAsync<HttpRequestException>(
            () => invoker.SendAsync(req, CancellationToken.None));

        var errorLog = logs.First(l => l.msg.Contains("error"));
        Assert.DoesNotContain("errorPathSECRET77", errorLog.msg);
        Assert.Contains("sig=[REDACTED]", errorLog.msg);
    }

    [Fact]
    public async Task StreamingResponseLog_RedactsUrlCredentials()
    {
        var (handler, logs) = CreateHandler(inner: new SseHandler("data: {}\n\n"));
        var invoker = new HttpMessageInvoker(handler);
        var req = MakeRequest(url: "https://api.example.com/v1/stream?access_token=streamSECRET55");

        await invoker.SendAsync(req, CancellationToken.None);

        var responseLog = logs.First(l => l.msg.Contains("response"));
        Assert.Contains("Streaming", responseLog.msg);
        Assert.DoesNotContain("streamSECRET55", responseLog.msg);
        Assert.Contains("access_token=[REDACTED]", responseLog.msg);
    }

    [Fact]
    public async Task BufferedResponseLog_RedactsUrlCredentials()
    {
        var (handler, logs) = CreateHandler(inner: new BodyHandler("{\"content\":[]}"));
        var invoker = new HttpMessageInvoker(handler);
        var req = MakeRequest(url: "https://api.example.com/v1/models?api_key=bufferedSECRET33");

        await invoker.SendAsync(req, CancellationToken.None);

        var responseLog = logs.First(l => l.msg.Contains("response"));
        Assert.DoesNotContain("bufferedSECRET33", responseLog.msg);
        Assert.Contains("api_key=[REDACTED]", responseLog.msg);
    }

    [Fact]
    public async Task RequestUrl_NonSensitiveParamValue_StillPassesThroughSecretRedactor()
    {
        var (handler, logs) = CreateHandler(secretRedactor: StubRedact);
        var invoker = new HttpMessageInvoker(handler);
        var req = MakeRequest(url: "https://api.example.com/v1/models?note=sk-ant-abcdef");

        await invoker.SendAsync(req, CancellationToken.None);

        var requestLog = logs.First(l => l.msg.Contains("request"));
        Assert.DoesNotContain("sk-ant-abcdef", requestLog.msg);
        Assert.Contains("note=[REDACTED]", requestLog.msg);
    }

    // ----- inner handler stubs -----

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("boom");
    }

    // ----- #3282: runtime toggle, re-evaluated per request -----

    /// <summary>
    /// The core regression for #3282. The SAME handler instance is invoked either side of the flag
    /// flip, which is what distinguishes a per-request decision from a per-construction one: before
    /// the fix the enable/disable branch lived in the DI pipeline factory, so a single instance could
    /// only ever have one behaviour and no config change could alter it without a restart.
    /// </summary>
    [Fact]
    public async Task EnabledPredicate_IsReEvaluatedPerRequest_OnSameHandlerInstance()
    {
        var enabled = false;
        var (handler, logs) = CreateHandler(isEnabled: () => enabled);
        var invoker = new HttpMessageInvoker(handler);

        await invoker.SendAsync(MakeRequest(), CancellationToken.None);
        Assert.Empty(logs);

        // Operator flips the flag on a running gateway - no new handler, no new pipeline.
        enabled = true;
        await invoker.SendAsync(MakeRequest(), CancellationToken.None);
        Assert.Contains(logs, l => l.msg.Contains("Provider HTTP request"));

        // ...and off again, which must silence it without a restart.
        logs.Clear();
        enabled = false;
        await invoker.SendAsync(MakeRequest(), CancellationToken.None);
        Assert.Empty(logs);
    }

    /// <summary>
    /// The disabled predicate must short-circuit before any logging, while leaving the request itself
    /// untouched: a diagnostics toggle that alters provider traffic would be worse than no toggle.
    /// </summary>
    [Fact]
    public async Task DisabledPredicate_SuppressesLogging_ButStillSendsRequest()
    {
        var (handler, logs) = CreateHandler(isEnabled: () => false);
        var invoker = new HttpMessageInvoker(handler);

        var response = await invoker.SendAsync(MakeRequest(), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{}", await response.Content.ReadAsStringAsync(CancellationToken.None));
        Assert.Empty(logs);
    }

    /// <summary>
    /// Both gates must hold. An operator who sets the flag but leaves the level at Information gets
    /// no output, so the fix is incomplete without the runtime level switch that #3282 also adds.
    /// </summary>
    [Fact]
    public async Task EnabledPredicate_StillRequiresDebugLevel()
    {
        var (handler, logs) = CreateHandler(debugEnabled: false, isEnabled: () => true);
        var invoker = new HttpMessageInvoker(handler);

        await invoker.SendAsync(MakeRequest(), CancellationToken.None);

        Assert.Empty(logs);
    }

    /// <summary>
    /// Omitting the predicate must preserve the pre-#3282 contract - Debug alone decides - so every
    /// existing construction site and test keeps its behaviour.
    /// </summary>
    [Fact]
    public async Task NullPredicate_LogsWheneverDebugIsEnabled()
    {
        var (handler, logs) = CreateHandler(isEnabled: null);
        var invoker = new HttpMessageInvoker(handler);

        await invoker.SendAsync(MakeRequest(), CancellationToken.None);

        Assert.Contains(logs, l => l.msg.Contains("Provider HTTP request"));
    }

    /// <summary>
    /// Redaction is a security control, not a diagnostics feature: enabling the toggle at runtime must
    /// not open a window in which secrets are logged raw (guards #2669/#2881/#3276).
    /// </summary>
    [Fact]
    public async Task RedactionStillApplies_WhenEnabledViaPredicate()
    {
        var (handler, logs) = CreateHandler(isEnabled: () => true);
        var invoker = new HttpMessageInvoker(handler);
        var req = MakeRequest();
        req.Headers.TryAddWithoutValidation("x-api-key", "sk-ant-secret");

        await invoker.SendAsync(req, CancellationToken.None);

        var requestLog = logs.First(l => l.msg.Contains("request"));
        Assert.Contains("[REDACTED]", requestLog.msg);
        Assert.DoesNotContain("sk-ant-secret", requestLog.msg);
    }

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
