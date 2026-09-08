using System.Text;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Dispatching;
using BotNexus.Gateway.Sessions;
using BotNexus.Gateway.Webhooks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace BotNexus.Gateway.Tests.Controllers;

/// <summary>
/// Issue #3807 — the anonymous inbound webhook route buffered the entire request body into a
/// <c>MemoryStream</c> (and then copied it again via <c>ToArray()</c>) BEFORE the HMAC check, with
/// no byte ceiling anywhere in the gateway and no bound on concurrent in-flight reads. That made an
/// unauthenticated caller able to drive arbitrary gateway heap allocation.
/// </summary>
/// <remarks>
/// These tests are non-vacuous against <c>origin/main</c> at <c>f2ab33494</c>: on that tree the
/// controller has no ceiling, so the oversized-body cases read the whole stream and fall through to
/// signature verification instead of returning 413, and there is no semaphore to exhaust for the
/// 429 case.
/// </remarks>
public sealed class WebhookInboundBodyBoundsTests : IAsyncLifetime
{
    private const int Ceiling = 4096;

    private string _dbPath = string.Empty;
    private SqliteWebhookRegistrationStore _registrations = null!;
    private SqliteWebhookRunStore _runs = null!;
    private InMemoryConversationStore _conversations = null!;
    private InMemorySessionStore _sessions = null!;
    private IConversationDispatcher _dispatcher = null!;
    private IInboundMessageOrchestrator _orchestrator = null!;
    private IHttpClientFactory _httpClientFactory = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"webhook-bounds-{Guid.NewGuid():N}.db");
        _registrations = new SqliteWebhookRegistrationStore(_dbPath);
        _runs = new SqliteWebhookRunStore(_dbPath);
        await _registrations.InitializeAsync();
        await _runs.InitializeAsync();

        _conversations = new InMemoryConversationStore();
        _sessions = new InMemorySessionStore();
        var router = new DefaultConversationRouter(
            _conversations, _sessions, NullLogger<DefaultConversationRouter>.Instance);
        _dispatcher = new DefaultConversationDispatcher(router, _conversations);

        _orchestrator = Substitute.For<IInboundMessageOrchestrator>();
        _orchestrator.AcceptAsync(Arg.Any<InboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(InboundDispatchResult.NoRoute());
        _httpClientFactory = Substitute.For<IHttpClientFactory>();
    }

    public Task DisposeAsync()
    {
        SqlitePoolCleanup.ClearPoolFor(_dbPath);
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var path = _dbPath + suffix;
            if (!File.Exists(path)) continue;
            try { File.Delete(path); }
            catch (IOException) { /* parallel suite may briefly retain the handle */ }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Acceptance clause 1 — a body larger than the ceiling is refused with 413, and the full body
    /// is never materialised: the counting stream proves the reader abandoned the copy near the
    /// ceiling rather than draining a payload orders of magnitude larger.
    /// </summary>
    [Fact]
    public async Task OversizedBody_Returns413_AndNeverMaterialisesFullBody()
    {
        var registration = await _registrations.CreateAsync(CreateRegistration());
        var oversized = BuildJsonBody(Ceiling * 64);
        var counting = new CountingStream(oversized);

        var result = await ReceiveAsync(registration, counting, declaredLength: null, sign: oversized);

        StatusOf(result).ShouldBe(StatusCodes.Status413PayloadTooLarge);

        // Never read the whole thing. Allow one chunk of slack past the ceiling — that is the
        // maximum the bounded reader can observe before it knows it must stop.
        counting.BytesRead.ShouldBeLessThan(oversized.Length);
        counting.BytesRead.ShouldBeLessThanOrEqualTo(Ceiling + 81920);
    }

    /// <summary>
    /// Acceptance clause 2 — a truthfully-declared oversized <c>Content-Length</c> is rejected
    /// before any body read at all, so it costs zero bytes.
    /// </summary>
    [Fact]
    public async Task DeclaredContentLengthOverCeiling_IsRejectedBeforeAnyRead()
    {
        var registration = await _registrations.CreateAsync(CreateRegistration());
        var oversized = BuildJsonBody(Ceiling * 8);
        var counting = new CountingStream(oversized);

        var result = await ReceiveAsync(
            registration, counting, declaredLength: oversized.Length, sign: oversized);

        StatusOf(result).ShouldBe(StatusCodes.Status413PayloadTooLarge);
        counting.BytesRead.ShouldBe(0);
    }

    /// <summary>
    /// Acceptance clause 3 — concurrent pre-signature reads are capped. With every in-flight slot
    /// held, a further request receives 429 and never touches its body.
    /// </summary>
    [Fact]
    public async Task WhenInFlightCapIsExhausted_Returns429WithoutReadingBody()
    {
        var registration = await _registrations.CreateAsync(CreateRegistration());
        using var guard = new WebhookInboundBodyGuard(maxBodyBytes: Ceiling, maxInFlightReads: 1);

        // Hold the only slot, exactly as an in-flight pre-auth read would.
        guard.TryAcquireReadSlot().ShouldBeTrue();
        guard.AvailableReadSlots.ShouldBe(0);

        var body = BuildJsonBody(16);
        var counting = new CountingStream(body);

        var result = await ReceiveAsync(registration, counting, declaredLength: body.Length, sign: body, guard: guard);

        StatusOf(result).ShouldBe(StatusCodes.Status429TooManyRequests);
        counting.BytesRead.ShouldBe(0);

        guard.ReleaseReadSlot();
    }

    /// <summary>
    /// Acceptance clause 5 — the 413 and 429 rejections occur strictly before signature
    /// verification. Both rejected requests here carry a VALID signature over their own bytes, so
    /// had control reached <see cref="WebhookSecretHelper.VerifySignature"/> the request would have
    /// been authenticated and continued to a 2xx/404. Returning 413/429 is only possible if the
    /// bound fired first.
    /// </summary>
    [Fact]
    public async Task RejectionsHappenBeforeSignatureVerification()
    {
        var registration = await _registrations.CreateAsync(CreateRegistration());

        var oversized = BuildJsonBody(Ceiling * 8);
        var tooBig = await ReceiveAsync(
            registration, new CountingStream(oversized), declaredLength: null, sign: oversized);
        StatusOf(tooBig).ShouldBe(StatusCodes.Status413PayloadTooLarge);

        using var saturated = new WebhookInboundBodyGuard(maxBodyBytes: Ceiling, maxInFlightReads: 1);
        saturated.TryAcquireReadSlot().ShouldBeTrue();
        var small = BuildJsonBody(16);
        var capped = await ReceiveAsync(
            registration, new CountingStream(small), declaredLength: small.Length, sign: small, guard: saturated);
        StatusOf(capped).ShouldBe(StatusCodes.Status429TooManyRequests);
        saturated.ReleaseReadSlot();

        // Control: the same registration and the same signing helper DO authenticate a small body,
        // which is what makes the two assertions above evidence of ordering rather than of a
        // universally broken route.
        var ok = BuildJsonBody(16);
        var accepted = await ReceiveAsync(
            registration, new CountingStream(ok), declaredLength: ok.Length, sign: ok);
        StatusOf(accepted).ShouldBeLessThan(400);
    }

    /// <summary>
    /// Acceptance clause 4 — a validly-signed request comfortably under both bounds still succeeds
    /// end to end, and the guard returns its slot afterwards so the cap is not leaked.
    /// </summary>
    [Fact]
    public async Task ValidRequestUnderBothLimits_StillSucceeds()
    {
        var registration = await _registrations.CreateAsync(CreateRegistration());
        using var guard = new WebhookInboundBodyGuard(maxBodyBytes: Ceiling, maxInFlightReads: 4);

        var body = BuildJsonBody(64);
        var result = await ReceiveAsync(
            registration, new CountingStream(body), declaredLength: body.Length, sign: body, guard: guard);

        StatusOf(result).ShouldBeLessThan(400);
        guard.AvailableReadSlots.ShouldBe(4);
    }

    /// <summary>
    /// A body of exactly the ceiling is accepted — the bound is inclusive, so the guard does not
    /// silently shrink the documented limit by one byte.
    /// </summary>
    [Fact]
    public async Task BodyExactlyAtCeiling_IsAccepted()
    {
        using var guard = new WebhookInboundBodyGuard(maxBodyBytes: 128, maxInFlightReads: 2);
        var exact = new byte[128];
        Array.Fill(exact, (byte)'a');

        var result = await guard.ReadBoundedAsync(new MemoryStream(exact), CancellationToken.None);

        result.IsTooLarge.ShouldBeFalse();
        result.Body.Length.ShouldBe(128);

        var overByOne = new byte[129];
        Array.Fill(overByOne, (byte)'a');
        var rejected = await guard.ReadBoundedAsync(new MemoryStream(overByOne), CancellationToken.None);
        rejected.IsTooLarge.ShouldBeTrue();
        rejected.Body.ShouldBeEmpty();
    }

    /// <summary>The guard refuses nonsensical bounds rather than degrading to unbounded.</summary>
    [Theory]
    [InlineData(0, 4)]
    [InlineData(-1, 4)]
    [InlineData(4096, 0)]
    [InlineData(4096, -1)]
    public void NonPositiveBounds_AreRejected(int maxBytes, int maxInFlight)
    {
        Should.Throw<ArgumentOutOfRangeException>(
            () => new WebhookInboundBodyGuard(maxBytes, maxInFlight));
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static int StatusOf(IActionResult result)
    {
        var status = result.ShouldBeAssignableTo<IStatusCodeActionResult>();
        status.StatusCode.ShouldNotBeNull();
        return status.StatusCode!.Value;
    }

    /// <summary>
    /// Builds a JSON body whose serialized length is at least <paramref name="approximateBytes"/>,
    /// padding the message field so the payload is structurally valid at any size. A valid payload
    /// matters: it removes "the JSON was malformed" as an alternative explanation for a rejection.
    /// </summary>
    private static byte[] BuildJsonBody(int approximateBytes)
    {
        var padding = new string('x', Math.Max(1, approximateBytes));
        var json = System.Text.Json.JsonSerializer.Serialize(
            new { message = padding, agentAction = false });
        return Encoding.UTF8.GetBytes(json);
    }

    private async Task<IActionResult> ReceiveAsync(
        WebhookRegistration registration,
        Stream body,
        long? declaredLength,
        byte[] sign,
        WebhookInboundBodyGuard? guard = null)
    {
        guard ??= new WebhookInboundBodyGuard(maxBodyBytes: Ceiling, maxInFlightReads: 8);

        var controller = new WebhookInboundController(
            _registrations,
            _runs,
            _orchestrator,
            _dispatcher,
            _conversations,
            _sessions,
            _httpClientFactory,
            NullLogger<WebhookInboundController>.Instance,
            guard)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.Request.Scheme = "https";
        controller.Request.Host = new HostString("gateway.test");
        controller.Request.Body = body;
        controller.Request.ContentLength = declaredLength;
        controller.Request.Headers["X-BotNexus-Signature-256"] =
            WebhookSecretHelper.ComputeSignature(registration.Secret, sign);

        return await controller.Receive(
            registration.AgentId.Value,
            registration.Id.Value,
            CancellationToken.None);
    }

    private static WebhookRegistration CreateRegistration() => new()
    {
        Id = WebhookId.Create(),
        Label = "bounds probe",
        AgentId = AgentId.From("tinker"),
        Secret = WebhookSecretHelper.GenerateSecret(),
        DefaultResponseMode = WebhookResponseMode.Async,
        Enabled = true,
        CreatedAt = DateTimeOffset.UtcNow
    };

    /// <summary>
    /// A read-only stream that records exactly how many bytes the controller pulled. This is the
    /// only way to prove the negative in acceptance clause 1 — that the oversized body was never
    /// materialised — since an assertion on the response code alone cannot distinguish "rejected
    /// after draining 256 KiB" from "rejected without draining it".
    /// </summary>
    private sealed class CountingStream(byte[] content) : Stream
    {
        private readonly MemoryStream _inner = new(content);

        public int BytesRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = _inner.Read(buffer, offset, count);
            BytesRead += n;
            return n;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var n = _inner.Read(buffer.Span);
            BytesRead += n;
            return ValueTask.FromResult(n);
        }

        public override Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Task.FromResult(Read(buffer, offset, count));

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
