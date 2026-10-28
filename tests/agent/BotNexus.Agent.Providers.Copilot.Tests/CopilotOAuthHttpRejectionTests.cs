using System.Net;
using System.Text;
using System.Text.Json;

namespace BotNexus.Agent.Providers.Copilot.Tests;

public class CopilotOAuthHttpRejectionTests
{
    private const string DeviceUrl = "https://github.com/login/device/code";
    private const string TokenUrl = "https://github.com/login/oauth/access_token";
    private const int MaxBytes = 256 * 1024;
    private const string Secret = "gho_NEVER_ECHO_device_refresh_secret";
    private static readonly IReadOnlyDictionary<string, string> Fields = new Dictionary<string, string>
    {
        ["client_id"] = "client-value",
        ["scope"] = "read:user",
        ["device_code"] = Secret
    };

    [Theory]
    [InlineData(DeviceUrl)]
    [InlineData(TokenUrl)]
    public async Task PostFormAsync_Success_PreservesRequestAndResponse(string url)
    {
        using var content = new TrackingContent("{\"device_code\":\"unchanged\"}");
        using var expected = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        using var handler = new RecordingHandler(async (request, ct) =>
        {
            request.Method.ShouldBe(HttpMethod.Post);
            request.RequestUri?.AbsoluteUri.ShouldBe(url);
            request.Headers.Accept.Single().MediaType.ShouldBe("application/json");
            request.Headers.UserAgent.ShouldBeEmpty();
            request.Headers.Authorization.ShouldBeNull();
            var form = request.Content.ShouldNotBeNull();
            form.Headers.ContentType?.MediaType.ShouldBe("application/x-www-form-urlencoded");
            (await form.ReadAsStringAsync(ct)).ShouldBe(
                $"client_id=client-value&scope=read%3Auser&device_code={Secret}");
            return expected;
        });
        using var client = new HttpClient(handler);

        using var actual = await CopilotOAuth.PostFormAsync(client, url, Fields, CancellationToken.None);

        ReferenceEquals(actual, expected).ShouldBeTrue();
        content.Disposed.ShouldBeFalse();
        (await actual.Content.ReadAsStringAsync()).ShouldBe("{\"device_code\":\"unchanged\"}");
        handler.Sends.ShouldBe(1);
    }

    [Theory]
    [InlineData("authorization_pending")]
    [InlineData("slow_down")]
    [InlineData("expired_token")]
    [InlineData("unsupported_grant_type")]
    [InlineData("incorrect_client_credentials")]
    [InlineData("incorrect_device_code")]
    [InlineData("access_denied")]
    [InlineData("device_flow_disabled")]
    public async Task PostFormAsync_KnownError_UsesOnlyFixedLiteral(string code)
    {
        var body = JsonSerializer.Serialize(new { error = code, error_description = Secret, access_token = Secret });
        await AssertRejectionAsync(body, code);
    }

    [Theory]
    [InlineData("gho_NEVER_ECHO_device_refresh_secret")]
    [InlineData("access_denied gho_NEVER_ECHO_device_refresh_secret")]
    [InlineData("ACCESS_DENIED")]
    [InlineData(" access_denied")]
    [InlineData("access_denied\r\nAuthorization: Bearer secret")]
    public async Task PostFormAsync_UntrustedError_FallsBackWithoutEcho(string code)
    {
        await AssertRejectionAsync(JsonSerializer.Serialize(new { error = code, error_description = Secret }), "unknown_error");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not JSON gho_NEVER_ECHO_device_refresh_secret")]
    [InlineData("<html>gho_NEVER_ECHO_device_refresh_secret</html>")]
    [InlineData("{\"error\":")]
    [InlineData("{}")]
    [InlineData("{\"error_description\":\"gho_NEVER_ECHO_device_refresh_secret\"}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"gho_NEVER_ECHO_device_refresh_secret\"")]
    [InlineData("123")]
    [InlineData("{\"error\":null}")]
    [InlineData("{\"error\":123}")]
    [InlineData("{\"error\":true}")]
    [InlineData("{\"error\":[]}")]
    [InlineData("{\"error\":{}}")]
    public async Task PostFormAsync_InvalidEnvelope_PreservesStatusAndSafeFallback(string body)
    {
        await AssertRejectionAsync(body, "unknown_error");
    }

    [Theory]
    [InlineData(DeviceUrl, HttpStatusCode.BadRequest)]
    [InlineData(TokenUrl, HttpStatusCode.Forbidden)]
    [InlineData(TokenUrl, HttpStatusCode.TooManyRequests)]
    [InlineData(DeviceUrl, HttpStatusCode.InternalServerError)]
    public async Task PostFormAsync_Rejection_PreservesOriginalStatusAndKnownEndpoint(string url, HttpStatusCode status)
    {
        await AssertRejectionAsync("{\"error\":\"access_denied\"}", "access_denied", url, status);
    }

    [Fact]
    public async Task PostFormAsync_DeclaredOversize_DoesNotReadOrPrebuffer()
    {
        using var content = new TrackingContent("{\"error\":\"access_denied\"}");
        content.Headers.ContentLength = MaxBytes + 1;
        await AssertRejectionAsync(content, "unknown_error");
        content.StreamReads.ShouldBe(0);
        content.Serialized.ShouldBeFalse();
    }

    [Fact]
    public async Task PostFormAsync_StreamingOversize_StopsAtBoundWithoutPrebuffering()
    {
        using var content = new TrackingContent("{\"error\":\"access_denied\",\"padding\":\"" + new string('x', MaxBytes * 4) + "\"}");
        await AssertRejectionAsync(content, "unknown_error");
        content.StreamReads.ShouldBeGreaterThan(0);
        content.StreamReads.ShouldBeLessThanOrEqualTo(MaxBytes + 81920);
        content.Serialized.ShouldBeFalse();
    }

    [Fact]
    public async Task PostFormAsync_ExactCap_StillRecognizesError()
    {
        const string json = "{\"error\":\"access_denied\"}";
        await AssertRejectionAsync(json + new string(' ', MaxBytes - json.Length), "access_denied");
    }

    [Fact]
    public async Task PostFormAsync_CancellationDuringBody_PropagatesAndDisposesWithoutRetry()
    {
        using var cts = new CancellationTokenSource();
        using var content = new TrackingContent("{}", cts);
        using var response = new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = content };
        using var handler = new RecordingHandler((_, _) => Task.FromResult(response));
        using var client = new HttpClient(handler);

        var error = await Should.ThrowAsync<OperationCanceledException>(async () =>
            await CopilotOAuth.PostFormAsync(client, DeviceUrl, Fields, cts.Token));

        error.CancellationToken.ShouldBe(cts.Token);
        content.Disposed.ShouldBeTrue();
        handler.Sends.ShouldBe(1);
    }

    [Fact]
    public async Task PostFormAsync_TransportFailure_PropagatesOriginalWithoutRetry()
    {
        var expected = new HttpRequestException("transport failure");
        using var handler = new RecordingHandler((_, _) => Task.FromException<HttpResponseMessage>(expected));
        using var client = new HttpClient(handler);
        var actual = await Should.ThrowAsync<HttpRequestException>(async () =>
            await CopilotOAuth.PostFormAsync(client, DeviceUrl, Fields, CancellationToken.None));
        ReferenceEquals(actual, expected).ShouldBeTrue();
        handler.Sends.ShouldBe(1);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task PostFormAsync_ProgressingBody_SharesDeadlineWithSend(HttpStatusCode status)
    {
        var clock = new DeadlineClock();
        using var content = new ProgressingContent(clock);
        using var response = new HttpResponseMessage(status) { Content = content };
        using var handler = new RecordingHandler((_, _) =>
        {
            clock.Advance(TimeSpan.FromSeconds(7));
            return Task.FromResult(response);
        });
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

        // Shouldly creates a new TaskCanceledException for canceled tasks, losing the
        // original inner exception. Capture the awaited exception to verify its chain.
        OperationCanceledException? error = null;
        try
        {
            using var actual = await CopilotOAuth.PostFormAsync(client, DeviceUrl, Fields,
                CancellationToken.None, clock);
        }
        catch (OperationCanceledException caught)
        {
            error = caught;
        }
        error.ShouldNotBeNull();

        // Every chunk progresses within the 30s idle limit; 7s sending + 3 x 8s
        // reading crosses the ORIGINAL request deadline, not a fresh body deadline.
        content.Reads.ShouldBe(3);
        content.Disposed.ShouldBeTrue();
        error.InnerException.ShouldBeOfType<TimeoutException>();
        clock.DueTimes.ShouldBe([client.Timeout]);
        clock.ActiveTimers.ShouldBe(0);
        handler.Sends.ShouldBe(1);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task PostFormAsync_CallerCancellationDuringProgress_UsesCallerToken(HttpStatusCode status)
    {
        var clock = new DeadlineClock();
        using var caller = new CancellationTokenSource();
        using var content = new ProgressingContent(clock, caller);
        using var response = new HttpResponseMessage(status) { Content = content };
        using var handler = new RecordingHandler((_, _) => Task.FromResult(response));
        using var client = new HttpClient(handler);

        var error = await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            using var actual = await CopilotOAuth.PostFormAsync(client, DeviceUrl, Fields, caller.Token, clock);
        });

        error.CancellationToken.ShouldBe(caller.Token);
        content.Disposed.ShouldBeTrue();
        handler.Sends.ShouldBe(1);
        clock.ActiveTimers.ShouldBe(0);
    }

    [Fact]
    public async Task PostFormAsync_Success_BuffersOriginalContentAndHeadersWithinCap()
    {
        var bytes = Encoding.Unicode.GetBytes("{\"device_code\":\"unchanged\"}");
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new("application/json") { CharSet = "utf-16" };
        content.Headers.TryAddWithoutValidation("X-Content", "preserved");
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        using var handler = new RecordingHandler((_, _) => Task.FromResult(response));
        using var client = new HttpClient(handler);
        var clock = new DeadlineClock();

        using var actual = await CopilotOAuth.PostFormAsync(client, DeviceUrl, Fields, CancellationToken.None, clock);

        ReferenceEquals(actual.Content, content).ShouldBeTrue();
        clock.Advance(client.Timeout + TimeSpan.FromSeconds(1));
        (await actual.Content.ReadAsByteArrayAsync()).ShouldBe(bytes);
        (await actual.Content.ReadAsStringAsync()).ShouldBe("{\"device_code\":\"unchanged\"}");
        actual.Content.Headers.ContentType?.CharSet.ShouldBe("utf-16");
        actual.Content.Headers.GetValues("X-Content").Single().ShouldBe("preserved");
        clock.ActiveTimers.ShouldBe(0);
    }

    // Finite progressing work: without the request deadline these reads finish (RED does
    // not wait for a real timeout). The same stream drives success serialization and errors.
    private sealed class ProgressingContent(DeadlineClock clock, CancellationTokenSource? caller = null) : HttpContent
    {
        public int Reads { get; private set; }
        public bool Disposed { get; private set; }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new ProgressingStream(this, clock, caller));
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
        {
            using var source = new ProgressingStream(this, clock, caller);
            var buffer = new byte[16];
            int count;
            while ((count = await source.ReadAsync(buffer.AsMemory(), ct)) != 0)
                await stream.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) Disposed = true;
            base.Dispose(disposing);
        }
        private sealed class ProgressingStream(ProgressingContent owner, DeadlineClock clock, CancellationTokenSource? caller)
            : MemoryStream("{}  "u8.ToArray(), writable: false)
        {
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            {
                owner.Reads++;
                clock.Advance(TimeSpan.FromSeconds(8));
                caller?.Cancel();
                ct.ThrowIfCancellationRequested();
                return base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], ct);
            }
        }
    }

    private sealed class DeadlineClock : TimeProvider
    {
        private TimeSpan _elapsed;
        private readonly List<DeadlineTimer> _timers = [];
        public List<TimeSpan> DueTimes { get; } = [];
        public int ActiveTimers => _timers.Count(timer => timer.Active);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            DueTimes.Add(dueTime);
            var timer = new DeadlineTimer(this, callback, state);
            _timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }
        public void Advance(TimeSpan elapsed)
        {
            _elapsed += elapsed;
            foreach (var timer in _timers.ToArray()) timer.FireIfDue();
        }
        private sealed class DeadlineTimer(DeadlineClock clock, TimerCallback callback, object? state) : ITimer
        {
            private TimeSpan? _due;
            public bool Active => _due is not null;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (period != Timeout.InfiniteTimeSpan) throw new NotSupportedException("One-shot deadlines only.");
                _due = dueTime == Timeout.InfiniteTimeSpan ? null : clock._elapsed + dueTime;
                return true;
            }
            public void FireIfDue()
            {
                if (_due is { } due && due <= clock._elapsed)
                {
                    _due = null;
                    callback(state);
                }
            }
            public void Dispose() => _due = null;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private static async Task AssertRejectionAsync(string body, string code, string url = DeviceUrl,
        HttpStatusCode status = HttpStatusCode.BadRequest)
    {
        using var content = new TrackingContent(body);
        await AssertRejectionAsync(content, code, url, status);
    }

    private static async Task AssertRejectionAsync(TrackingContent content, string code, string url = DeviceUrl,
        HttpStatusCode status = HttpStatusCode.BadRequest)
    {
        using var response = new HttpResponseMessage(status)
        {
            Content = content,
            ReasonPhrase = Secret,
            RequestMessage = new HttpRequestMessage(HttpMethod.Post, "https://attacker.invalid/" + Secret)
        };
        response.Headers.TryAddWithoutValidation("X-Secret", Secret);
        using var handler = new RecordingHandler((_, _) => Task.FromResult(response));
        using var client = new HttpClient(handler);

        var error = await Should.ThrowAsync<HttpRequestException>(async () =>
            await CopilotOAuth.PostFormAsync(client, url, Fields, CancellationToken.None));

        error.StatusCode.ShouldBe(status);
        error.Message.ShouldBe($"GitHub OAuth request to {url} failed with HTTP {(int)status}: {code}.");
        error.ToString().ShouldNotContain(Secret);
        error.InnerException.ShouldBeNull();
        content.Disposed.ShouldBeTrue();
        handler.Sends.ShouldBe(1);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        public int Sends { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sends++;
            return send(request, cancellationToken);
        }
    }

    // A content type that detects default SendAsync prebuffering separately from bounded stream reads.
    private sealed class TrackingContent(string body, CancellationTokenSource? cancelOnRead = null) : HttpContent
    {
        public bool Disposed { get; private set; }
        public bool Serialized { get; private set; }
        public int StreamReads { get; private set; }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            Serialized = true;
            await stream.WriteAsync(Encoding.UTF8.GetBytes(body));
        }

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(
            new TrackingStream(Encoding.UTF8.GetBytes(body), count => StreamReads += count, cancelOnRead));

        protected override void Dispose(bool disposing)
        {
            if (disposing) Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class TrackingStream(byte[] bytes, Action<int> recordRead, CancellationTokenSource? cancelOnRead)
        : MemoryStream(bytes, writable: false)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancelOnRead?.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            var count = Read(buffer.Span);
            recordRead(count);
            return ValueTask.FromResult(count);
        }
    }
}
