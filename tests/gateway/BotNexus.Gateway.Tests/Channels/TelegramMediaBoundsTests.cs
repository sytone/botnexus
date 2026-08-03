using System.Net;
using System.Text;
using System.Text.Json;
using BotNexus.Extensions.Channels.Telegram;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Gateway.Tests.Channels;

/// <summary>
/// Bounds the Telegram inbound media path (issue #2724). Authorization ordering is already correct;
/// what was missing is a size cap and a wall-clock timeout on the download itself. These tests pin
/// both bounds and the fail-safe direction: an oversize or slow attachment is SKIPPED, but the
/// message (caption text) is still delivered - media loss is acceptable, message loss is not.
/// </summary>
public sealed class TelegramMediaBoundsTests
{
    private const string BotName = "default";

    [Fact]
    public void TelegramBotConfig_DefaultsBoundMediaDownloads()
    {
        var config = new TelegramBotConfig();

        // 20 MB matches the Telegram Bot API's own documented download ceiling for getFile.
        config.MaxMediaBytes.ShouldBe(20L * 1024 * 1024);
        config.MediaDownloadTimeoutSeconds.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task PhotoWithAdvertisedSizeAboveCap_IsNotDownloaded_AndStillDispatchesCaption()
    {
        var stub = new MediaStub();
        var (adapter, secret, dispatcher) = await StartAsync(
            stub,
            options =>
            {
                options.AllowedChatIds.Add(42);
                options.AllowedUserIds.Add(7);
                options.MaxMediaBytes = 1_000_000;
            });

        var result = await adapter.HandleWebhookUpdateAsync(
            BotName,
            PhotoUpdate(chatId: 42, userId: 7, caption: "huge picture", advertisedFileSize: 9_000_000),
            secret(),
            CancellationToken.None);

        await adapter.StopAsync(CancellationToken.None);

        result.ShouldBe(TelegramChannelAdapter.WebhookHandleResult.Accepted);

        // The cap must be applied BEFORE any network work: getFile is never called.
        stub.GetFileCalls.ShouldBe(0);
        stub.DownloadCalls.ShouldBe(0);

        var dispatched = Dispatched(dispatcher);
        dispatched.Count.ShouldBe(1);
        dispatched[0].Content.ShouldBe("huge picture");
        dispatched[0].ContentParts.ShouldBeNull();
    }

    [Fact]
    public async Task PhotoWhoseActualStreamExceedsCap_IsAborted_AndStillDispatchesCaption()
    {
        // The advertised FileSize is server-supplied and therefore not authoritative. A hostile or
        // buggy server can advertise 1 KB and then stream gigabytes, so the cap must be re-enforced
        // while reading rather than trusted once up front.
        const long cap = 256 * 1024;
        var stub = new MediaStub { DownloadTotalBytes = 16 * 1024 * 1024 };

        var (adapter, secret, dispatcher) = await StartAsync(
            stub,
            options =>
            {
                options.AllowedChatIds.Add(42);
                options.AllowedUserIds.Add(7);
                options.MaxMediaBytes = cap;
            });

        var result = await adapter.HandleWebhookUpdateAsync(
            BotName,
            PhotoUpdate(chatId: 42, userId: 7, caption: "lying about its size", advertisedFileSize: 1_024),
            secret(),
            CancellationToken.None);

        await adapter.StopAsync(CancellationToken.None);

        result.ShouldBe(TelegramChannelAdapter.WebhookHandleResult.Accepted);
        stub.GetFileCalls.ShouldBe(1);

        // Aborted, not buffered: the adapter must stop reading shortly after crossing the cap and
        // must never have pulled the whole 16 MB body into memory.
        stub.BytesServed.ShouldBeLessThan(stub.DownloadTotalBytes);
        stub.BytesServed.ShouldBeLessThanOrEqualTo(cap * 2);

        var dispatched = Dispatched(dispatcher);
        dispatched.Count.ShouldBe(1);
        dispatched[0].Content.ShouldBe("lying about its size");
        dispatched[0].ContentParts.ShouldBeNull();
    }

    [Fact]
    public async Task PhotoDownloadExceedingTimeout_IsCancelled_AndStillDispatchesCaption()
    {
        var stub = new MediaStub { DownloadStallForever = true };
        var (adapter, secret, dispatcher) = await StartAsync(
            stub,
            options =>
            {
                options.AllowedChatIds.Add(42);
                options.AllowedUserIds.Add(7);
                options.MediaDownloadTimeoutSeconds = 1;
            });

        var result = await adapter.HandleWebhookUpdateAsync(
            BotName,
            PhotoUpdate(chatId: 42, userId: 7, caption: "slowloris", advertisedFileSize: 1_024),
            secret(),
            CancellationToken.None);

        await adapter.StopAsync(CancellationToken.None);

        result.ShouldBe(TelegramChannelAdapter.WebhookHandleResult.Accepted);
        stub.DownloadCalls.ShouldBe(1);
        stub.DownloadCancelled.ShouldBeTrue();

        var dispatched = Dispatched(dispatcher);
        dispatched.Count.ShouldBe(1);
        dispatched[0].Content.ShouldBe("slowloris");
        dispatched[0].ContentParts.ShouldBeNull();
    }

    [Fact]
    public async Task UnauthorizedChat_CausesZeroGetFileCalls()
    {
        var stub = new MediaStub();
        var (adapter, secret, dispatcher) = await StartAsync(
            stub,
            options =>
            {
                options.AllowedChatIds.Add(42);
                options.AllowedUserIds.Add(7);
            });

        var result = await adapter.HandleWebhookUpdateAsync(
            BotName,
            PhotoUpdate(chatId: 9999, userId: 7, caption: "intruder", advertisedFileSize: 1_024),
            secret(),
            CancellationToken.None);

        await adapter.StopAsync(CancellationToken.None);

        result.ShouldBe(TelegramChannelAdapter.WebhookHandleResult.Accepted);
        stub.GetFileCalls.ShouldBe(0);
        stub.DownloadCalls.ShouldBe(0);
        Dispatched(dispatcher).ShouldBeEmpty();
    }

    [Fact]
    public async Task UnauthorizedUser_CausesZeroGetFileCalls()
    {
        var stub = new MediaStub();
        var (adapter, secret, dispatcher) = await StartAsync(
            stub,
            options =>
            {
                options.AllowedChatIds.Add(42);
                options.AllowedUserIds.Add(7);
            });

        var result = await adapter.HandleWebhookUpdateAsync(
            BotName,
            PhotoUpdate(chatId: 42, userId: 8888, caption: "intruder", advertisedFileSize: 1_024),
            secret(),
            CancellationToken.None);

        await adapter.StopAsync(CancellationToken.None);

        result.ShouldBe(TelegramChannelAdapter.WebhookHandleResult.Accepted);
        stub.GetFileCalls.ShouldBe(0);
        stub.DownloadCalls.ShouldBe(0);
        Dispatched(dispatcher).ShouldBeEmpty();
    }

    [Fact]
    public async Task PhotoWithinBounds_IsStillDownloadedAndAttached()
    {
        // Non-vacuity in the other direction: the bounds must not break the happy path.
        var stub = new MediaStub { DownloadTotalBytes = 4 };
        var (adapter, secret, dispatcher) = await StartAsync(
            stub,
            options =>
            {
                options.AllowedChatIds.Add(42);
                options.AllowedUserIds.Add(7);
            });

        var result = await adapter.HandleWebhookUpdateAsync(
            BotName,
            PhotoUpdate(chatId: 42, userId: 7, caption: "small picture", advertisedFileSize: 4),
            secret(),
            CancellationToken.None);

        await adapter.StopAsync(CancellationToken.None);

        result.ShouldBe(TelegramChannelAdapter.WebhookHandleResult.Accepted);
        var dispatched = Dispatched(dispatcher);
        dispatched.Count.ShouldBe(1);
        dispatched[0].ContentParts.ShouldNotBeNull();
        dispatched[0].ContentParts!.Count.ShouldBe(1);
        dispatched[0].ContentParts![0].ShouldBeOfType<BinaryContentPart>().Data.Length.ShouldBe(4);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static IReadOnlyList<InboundMessage> Dispatched(Mock<IChannelDispatcher> dispatcher)
        => dispatcher.Invocations
            .Where(i => i.Method.Name == nameof(IChannelDispatcher.DispatchAsync))
            .Select(i => (InboundMessage)i.Arguments[0])
            .ToList();

    private static TelegramUpdate PhotoUpdate(long chatId, long userId, string caption, int advertisedFileSize)
        => new()
        {
            UpdateId = 1,
            Message = new TelegramMessage
            {
                MessageId = 1,
                Chat = new TelegramChat { Id = chatId },
                From = new TelegramUser { Id = userId },
                Caption = caption,
                Photo =
                [
                    new TelegramPhotoSize
                    {
                        FileId = "file_id",
                        FileUniqueId = "u1",
                        Width = 800,
                        Height = 600,
                        FileSize = advertisedFileSize
                    }
                ]
            }
        };

    private static async Task<(TelegramChannelAdapter Adapter, Func<string?> Secret, Mock<IChannelDispatcher> Dispatcher)> StartAsync(
        MediaStub stub,
        Action<TelegramGatewayOptions> configure)
    {
        var options = new TelegramGatewayOptions
        {
            BotToken = "token",
            WebhookUrl = "https://example.com/telegram/webhook/default"
        };
        configure(options);

        var adapter = new TelegramChannelAdapter(
            NullLogger<TelegramChannelAdapter>.Instance,
            Options.Create(options),
            new SingleClientFactory(new HttpClient(stub)));

        var dispatcher = new Mock<IChannelDispatcher>();
        dispatcher
            .Setup(d => d.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await adapter.StartAsync(dispatcher.Object, CancellationToken.None);
        return (adapter, () => stub.WebhookSecret, dispatcher);
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    /// <summary>
    /// Bot API stub that counts getFile calls and serves a synthetic, instrumented file body so a
    /// test can observe how many bytes the adapter actually pulled off the wire.
    /// </summary>
    private sealed class MediaStub : HttpMessageHandler
    {
        private int _getFileCalls;
        private int _downloadCalls;

        public string? WebhookSecret { get; private set; }

        public long DownloadTotalBytes { get; init; } = 1024;

        public bool DownloadStallForever { get; init; }

        public int GetFileCalls => Volatile.Read(ref _getFileCalls);

        public int DownloadCalls => Volatile.Read(ref _downloadCalls);

        public long BytesServed => CountingStream.TotalServed;

        public bool DownloadCancelled => CountingStream.Cancelled;

        private CountingStream CountingStream { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get &&
                request.RequestUri?.AbsolutePath.Contains("/file/bot", StringComparison.OrdinalIgnoreCase) == true)
            {
                Interlocked.Increment(ref _downloadCalls);
                CountingStream.Configure(DownloadTotalBytes, DownloadStallForever);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(CountingStream) };
            }

            var method = request.RequestUri?.Segments.LastOrDefault()?.Trim('/') ?? string.Empty;
            var body = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);

            if (method == "setWebhook")
            {
                using var json = JsonDocument.Parse(body);
                WebhookSecret = json.RootElement.TryGetProperty("secret_token", out var el) && el.ValueKind == JsonValueKind.String
                    ? el.GetString()
                    : null;
            }

            if (method == "getFile")
            {
                Interlocked.Increment(ref _getFileCalls);
                return Json("{\"ok\":true,\"result\":{\"file_id\":\"file_id\",\"file_unique_id\":\"u1\",\"file_path\":\"photos/x.jpg\"}}");
            }

            return Json(method switch
            {
                "getUpdates" => "{\"ok\":true,\"result\":[]}",
                _ => "{\"ok\":true,\"result\":true}"
            });
        }

        private static HttpResponseMessage Json(string payload)
            => new(HttpStatusCode.OK) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
    }

    /// <summary>
    /// Read-only stream that hands out an unbounded-looking body in small chunks while recording how
    /// much was actually consumed, so a test can prove the reader aborted early instead of buffering.
    /// </summary>
    private sealed class CountingStream : Stream
    {
        private long _total = 1024;
        private long _served;
        private bool _stall;
        private volatile bool _cancelled;

        public long TotalServed => Interlocked.Read(ref _served);

        public bool Cancelled => _cancelled;

        public void Configure(long total, bool stall)
        {
            _total = total;
            _stall = stall;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_stall)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    _cancelled = true;
                    throw;
                }
            }

            var remaining = _total - Interlocked.Read(ref _served);
            if (remaining <= 0)
                return 0;

            var count = (int)Math.Min(Math.Min(buffer.Length, 8192), remaining);
            buffer.Span[..count].Fill(0xAB);
            Interlocked.Add(ref _served, count);
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
