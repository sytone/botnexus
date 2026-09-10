using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BotNexus.Gateway.Abstractions.Notifications;
using BotNexus.Gateway.Notifications.Push;

namespace BotNexus.Gateway.Tests.Notifications.Push;

/// <summary>
/// Pins how the sender talks to a push service, and what it does when one pushes back.
/// </summary>
/// <remarks>
/// The failure that matters here is silent: a subscription for a device that no longer exists is
/// retried on every notification forever, and nothing surfaces because the push service answers
/// politely each time. So the status-code handling is the substance of these tests.
/// </remarks>
public sealed class WebPushSenderTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "botnexus-push-send", Guid.NewGuid().ToString("N"));

    private string DbPath => Path.Combine(_dir, "push.sqlite");
    private string VapidPath => Path.Combine(_dir, "vapid.json");

    public WebPushSenderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqlitePoolCleanup.ClearPoolFor(DbPath);
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A leaked temp directory is not worth failing a test over.
        }
    }

    private static readonly Notification Sample = new()
    {
        Id = "n1",
        Kind = NotificationKind.AgentRunFailed,
        Severity = NotificationSeverity.Error,
        Title = "Agent 'assistant' run failed",
        Body = "The provider returned 529.",
        Link = "conversation/c1",
        CreatedAtUtc = DateTimeOffset.UnixEpoch,
    };

    private IPushSubscriptionStore Store() => new SqlitePushSubscriptionStore(DbPath);

    /// <summary>A subscription with real keys, so the encryption runs for real.</summary>
    private static PushSubscription Subscription(string endpoint = "https://push.example/abc") =>
        Subscribe(endpoint).Subscription;

    /// <summary>
    /// A subscription together with the private half, so a test can decrypt what was sent exactly
    /// as the subscribing browser would.
    /// </summary>
    private static (PushSubscription Subscription, ECDiffieHellman Key, byte[] Auth) Subscribe(
        string endpoint = "https://push.example/abc")
    {
        var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var q = key.ExportParameters(false).Q;
        var point = new byte[65];
        point[0] = 0x04;
        q.X!.CopyTo(point, 1 + (32 - q.X!.Length));
        q.Y!.CopyTo(point, 33 + (32 - q.Y!.Length));

        var auth = RandomNumberGenerator.GetBytes(16);

        return (new PushSubscription
        {
            Endpoint = endpoint,
            P256dh = Base64Url.Encode(point),
            Auth = Base64Url.Encode(auth),
        }, key, auth);
    }

    private WebPushSender Sender(StubPushService service, IPushSubscriptionStore store) =>
        new(new HttpClient(service), store, new VapidKeyStore(VapidPath, "mailto:ops@example.com"));

    [Fact]
    public async Task Sends_nothing_when_no_device_has_subscribed()
    {
        var service = new StubPushService();

        var delivered = await Sender(service, Store()).SendAsync(Sample);

        Assert.Equal(0, delivered);
        Assert.Empty(service.Requests);
    }

    [Fact]
    public async Task Sends_an_encrypted_payload_with_a_vapid_header()
    {
        var store = Store();
        await store.SaveAsync(Subscription());
        var service = new StubPushService();

        var delivered = await Sender(service, store).SendAsync(Sample);

        Assert.Equal(1, delivered);
        var request = Assert.Single(service.Requests);

        Assert.StartsWith("vapid t=", request.Authorization);
        Assert.Contains(", k=", request.Authorization);
        Assert.Equal("aes128gcm", request.ContentEncoding);
        Assert.Equal("86400", request.Ttl);

        // The body must be ciphertext, not the notification in the clear: the push service is an
        // untrusted relay and must not be able to read which agent failed.
        Assert.DoesNotContain("assistant", Encoding.UTF8.GetString(request.Body));
        Assert.True(request.Body.Length > 16 + 4 + 1 + 65);
    }

    // The important one. A 410 means the device is gone for good; keeping the row would retry a
    // dead endpoint on every future notification, forever, in silence.
    [Theory]
    [InlineData(HttpStatusCode.Gone)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Forgets_a_subscription_the_push_service_says_is_gone(HttpStatusCode status)
    {
        var store = Store();
        await store.SaveAsync(Subscription());
        var service = new StubPushService { Status = status };

        await Sender(service, store).SendAsync(Sample);

        Assert.Empty(await store.ListAsync());
    }

    // Rate limiting and outages are transient. Dropping the subscription would turn a bad ten
    // minutes into a device that never hears from the gateway again.
    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Keeps_a_subscription_through_a_transient_refusal(HttpStatusCode status)
    {
        var store = Store();
        await store.SaveAsync(Subscription());
        var service = new StubPushService { Status = status };

        var delivered = await Sender(service, store).SendAsync(Sample);

        Assert.Equal(0, delivered);
        Assert.Single(await store.ListAsync());
    }

    // One unreachable push service must not stop the others: subscribers are independent, and a
    // Firefox outage should not cost every Chrome user their notification.
    [Fact]
    public async Task One_failing_subscriber_does_not_stop_the_rest()
    {
        var store = Store();
        await store.SaveAsync(Subscription("https://push.example/first"));
        await store.SaveAsync(Subscription("https://push.example/second"));
        var service = new StubPushService { FailFor = "first" };

        var delivered = await Sender(service, store).SendAsync(Sample);

        Assert.Equal(1, delivered);
        Assert.Equal(2, service.Requests.Count);
    }

    [Fact]
    public async Task Records_delivery_so_a_dormant_device_can_be_told_apart()
    {
        var store = Store();
        await store.SaveAsync(Subscription());

        await Sender(new StubPushService(), store).SendAsync(Sample);

        Assert.NotNull(Assert.Single(await store.ListAsync()).LastSuccessAtUtc);
    }

    // End to end through the real encryption: what the service worker will actually parse. This is
    // the only test that proves the payload the browser receives is the notification, rather than
    // proving the sender produced some bytes.
    [Fact]
    public async Task The_browser_can_decrypt_the_notification_it_is_sent()
    {
        var (subscription, key, auth) = Subscribe();
        using var _ = key;
        var store = Store();
        await store.SaveAsync(subscription);
        var service = new StubPushService();

        await Sender(service, store).SendAsync(Sample);

        var plaintext = Encoding.UTF8.GetString(
            WebPushEncryptor.Decrypt(service.Requests[0].Body, key, auth));

        using var payload = JsonDocument.Parse(plaintext);
        var root = payload.RootElement;

        Assert.Equal("n1", root.GetProperty("id").GetString());
        Assert.Equal("Agent 'assistant' run failed", root.GetProperty("title").GetString());
        Assert.Equal("The provider returned 529.", root.GetProperty("body").GetString());
        Assert.Equal("conversation/c1", root.GetProperty("link").GetString());

        // Names, not numbers - the same contract the REST surface keeps, so one client parses one
        // shape however a notification reached it.
        Assert.Equal("AgentRunFailed", root.GetProperty("kind").GetString());
        Assert.Equal("Error", root.GetProperty("severity").GetString());
    }

    /// <summary>Stands in for a push service and records what it was sent.</summary>
    private sealed class StubPushService : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.Created;

        /// <summary>Fails only requests whose endpoint contains this fragment.</summary>
        public string? FailFor { get; set; }

        public List<Captured> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? []
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);

            Requests.Add(new Captured
            {
                Url = request.RequestUri!.ToString(),
                Authorization = request.Headers.TryGetValues("Authorization", out var auth)
                    ? string.Join("", auth)
                    : string.Empty,
                ContentEncoding = request.Content?.Headers.ContentEncoding.FirstOrDefault() ?? string.Empty,
                Ttl = request.Headers.TryGetValues("TTL", out var ttl) ? string.Join("", ttl) : string.Empty,
                Body = body,
            });

            var failing = FailFor is not null && request.RequestUri!.ToString().Contains(FailFor);

            return new HttpResponseMessage(failing ? HttpStatusCode.InternalServerError : Status);
        }

        internal sealed class Captured
        {
            public required string Url { get; init; }
            public required string Authorization { get; init; }
            public required string ContentEncoding { get; init; }
            public required string Ttl { get; init; }
            public required byte[] Body { get; init; }
        }
    }
}
