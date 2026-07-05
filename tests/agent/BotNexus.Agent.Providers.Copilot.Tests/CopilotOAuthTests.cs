using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BotNexus.Agent.Providers.Copilot;
using BotNexus.Agent.Providers.Core.Utilities;

namespace BotNexus.Agent.Providers.Copilot.Tests;

public class CopilotOAuthTests
{
    [Fact]
    public void OAuthCredentials_CanBeCreatedWithValidProperties()
    {
        var creds = new OAuthCredentials(
            AccessToken: "ghu_abc123",
            RefreshToken: "gho_refresh456",
            ExpiresAt: DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()
        );

        creds.AccessToken.ShouldBe("ghu_abc123");
        creds.RefreshToken.ShouldBe("gho_refresh456");
        creds.ExpiresAt.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void OAuthCredentials_DefaultApiEndpoint_IsNull()
    {
        var creds = new OAuthCredentials("token", "refresh", 0);
        creds.ApiEndpoint.ShouldBeNull();
    }

    [Fact]
    public void OAuthCredentials_WithApiEndpoint_PreservesValue()
    {
        var creds = new OAuthCredentials("token", "refresh", 0, "https://enterprise.copilot.example.com");
        creds.ApiEndpoint.ShouldBe("https://enterprise.copilot.example.com");
    }

    [Fact]
    public void OAuthCredentials_RecordEquality_MatchesOnAllFields()
    {
        var a = new OAuthCredentials("tok", "ref", 100, "https://api.example.com");
        var b = new OAuthCredentials("tok", "ref", 100, "https://api.example.com");
        a.ShouldBe(b);
    }

    [Fact]
    public void OAuthCredentials_RecordEquality_DiffersOnAccessToken()
    {
        var a = new OAuthCredentials("tok1", "ref", 100);
        var b = new OAuthCredentials("tok2", "ref", 100);
        a.ShouldNotBe(b);
    }

    [Fact]
    public void OAuthCredentials_RecordEquality_DiffersOnExpiresAt()
    {
        var a = new OAuthCredentials("tok", "ref", 100);
        var b = new OAuthCredentials("tok", "ref", 200);
        a.ShouldNotBe(b);
    }

    // --- Expiry detection tests ---

    [Fact]
    public void TokenExpiryDetection_WorksCorrectly_WhenExpired()
    {
        var pastExpiry = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds();
        var creds = new OAuthCredentials("token", "refresh", pastExpiry);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var isExpired = now >= creds.ExpiresAt - 60;

        isExpired.ShouldBeTrue("token expired 5 minutes ago");
    }

    [Fact]
    public void TokenExpiryDetection_WorksCorrectly_WhenValid()
    {
        var futureExpiry = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var creds = new OAuthCredentials("token", "refresh", futureExpiry);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var isExpired = now >= creds.ExpiresAt - 60;

        isExpired.ShouldBeFalse("token is still valid for ~1 hour");
    }

    [Fact]
    public void TokenExpiryDetection_WorksCorrectly_WhenWithin60Seconds()
    {
        var almostExpired = DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeSeconds();
        var creds = new OAuthCredentials("token", "refresh", almostExpired);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var isExpired = now >= creds.ExpiresAt - 60;

        isExpired.ShouldBeTrue("token expires in 30s, which is within the 60s refresh window");
    }

    [Fact]
    public void TokenExpiryDetection_ExactlyAt60Seconds_ShouldTriggerRefresh()
    {
        var exactBoundary = DateTimeOffset.UtcNow.AddSeconds(60).ToUnixTimeSeconds();
        var creds = new OAuthCredentials("token", "refresh", exactBoundary);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var isExpired = now >= creds.ExpiresAt - 60;

        isExpired.ShouldBeTrue("token at exactly 60s boundary should trigger refresh");
    }

    [Fact]
    public void TokenExpiryDetection_ExpiresAtZero_ShouldAlwaysNeedRefresh()
    {
        var creds = new OAuthCredentials("token", "refresh", 0);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var isExpired = now >= creds.ExpiresAt - 60;

        isExpired.ShouldBeTrue("ExpiresAt=0 forces refresh on first use (login flow)");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-100000)]
    public void TokenExpiryDetection_NegativeExpiresAt_ShouldNeedRefresh(long expiresAt)
    {
        var creds = new OAuthCredentials("token", "refresh", expiresAt);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var isExpired = now >= creds.ExpiresAt - 60;

        isExpired.ShouldBeTrue("negative ExpiresAt is always expired");
    }

    // --- GetApiKeyAsync tests ---

    [Fact]
    public async Task GetApiKeyAsync_WhenProviderNotInMap_ReturnsNull()
    {
        var map = new Dictionary<string, OAuthCredentials>();

        var result = await CopilotOAuth.GetApiKeyAsync("unknown-provider", map);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task GetApiKeyAsync_WhenProviderExistsButEmptyMap_ReturnsNull()
    {
        var map = new Dictionary<string, OAuthCredentials>
        {
            ["other-provider"] = new("token", "refresh", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds())
        };

        var result = await CopilotOAuth.GetApiKeyAsync("missing-provider", map);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task GetApiKeyAsync_WhenMultipleProviders_ReturnsCorrectOne()
    {
        var futureExpiry = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var map = new Dictionary<string, OAuthCredentials>
        {
            ["provider-a"] = new("token-a", "refresh-a", futureExpiry),
            ["provider-b"] = new("token-b", "refresh-b", futureExpiry)
        };

        var result = await CopilotOAuth.GetApiKeyAsync("provider-a", map);

        result.ShouldNotBeNull();
        result!.Value.ApiKey.ShouldBe("token-a");
    }

    // --- ExpiresAt bounds validation tests (#648) ---

    [Fact]
    public void IsExpiresAtInRange_ValidFutureTimestamp_ReturnsTrue()
    {
        var future = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        CopilotOAuth.IsExpiresAtInRange(future).ShouldBeTrue();
    }

    [Fact]
    public void IsExpiresAtInRange_Zero_ReturnsFalse()
    {
        CopilotOAuth.IsExpiresAtInRange(0).ShouldBeFalse("ExpiresAt=0 forces refresh, not treated as in-range");
    }

    [Fact]
    public void IsExpiresAtInRange_Negative_ReturnsFalse()
    {
        CopilotOAuth.IsExpiresAtInRange(-1).ShouldBeFalse();
    }

    [Fact]
    public void IsExpiresAtInRange_MaxValidValue_ReturnsTrue()
    {
        CopilotOAuth.IsExpiresAtInRange(CopilotOAuth.MaxValidExpiresAt).ShouldBeTrue();
    }

    [Fact]
    public void IsExpiresAtInRange_BeyondMaxValid_ReturnsFalse()
    {
        // A crafted JWT with exp beyond DateTimeOffset.MaxValue must be treated as invalid.
        // DateTimeOffset.FromUnixTimeSeconds would throw ArgumentOutOfRangeException for such values.
        var outOfRange = CopilotOAuth.MaxValidExpiresAt + 1;
        CopilotOAuth.IsExpiresAtInRange(outOfRange).ShouldBeFalse(
            "exp beyond DateTimeOffset.MaxValue would crash FromUnixTimeSeconds");
    }

    [Theory]
    [InlineData(long.MaxValue)]
    [InlineData(9_999_999_999_999L)]
    public void IsExpiresAtInRange_ExtremelyLargeValues_ReturnsFalse(long extreme)
    {
        CopilotOAuth.IsExpiresAtInRange(extreme).ShouldBeFalse(
            "extremely large exp values must be rejected to prevent perpetual-valid bypass");
    }

    // --- OAuthCredentials with-expressions (record mutation) ---

    [Fact]
    public void OAuthCredentials_WithExpression_CanUpdateAccessToken()
    {
        var original = new OAuthCredentials("old-token", "refresh", 100);
        var updated = original with { AccessToken = "new-token" };

        updated.AccessToken.ShouldBe("new-token");
        updated.RefreshToken.ShouldBe("refresh");
        updated.ExpiresAt.ShouldBe(100);
    }

    [Fact]
    public void OAuthCredentials_WithExpression_CanSetApiEndpoint()
    {
        var original = new OAuthCredentials("token", "refresh", 100);
        var updated = original with { ApiEndpoint = "https://enterprise.example.com" };

        updated.ApiEndpoint.ShouldBe("https://enterprise.example.com");
        original.ApiEndpoint.ShouldBeNull("original should not be mutated");
    }

    // --- Bounded OAuth token-exchange response reads (#1772) ---
    //
    // ReadJsonAsync serves all three peer-controlled OAuth reads (device-code, access-token,
    // copilot-token). It routes through ReadBoundedJsonAsync, which caps the body via
    // BoundedHttpContent so a hostile / malfunctioning GitHub OAuth endpoint cannot force the
    // runtime to buffer an unbounded body before JsonDocument.Parse (OOM-DoS hardening). These
    // tests exercise the internal seam directly: happy small body parses, an over-cap body throws,
    // and an over-cap declared Content-Length is rejected cheaply without reading the body.

    [Fact]
    public async Task ReadBoundedJsonAsync_SmallValidBody_ParsesAndReturnsElement()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"access_token":"ghu_abc123","token_type":"bearer"}""",
                Encoding.UTF8,
                "application/json")
        };

        var element = await CopilotOAuth.ReadBoundedJsonAsync(response, maxBytes: 1024, CancellationToken.None);

        element.GetProperty("access_token").GetString().ShouldBe("ghu_abc123");
        element.GetProperty("token_type").GetString().ShouldBe("bearer");
    }

    [Fact]
    public async Task ReadBoundedJsonAsync_BodyLargerThanCap_ThrowsResponseContentTooLarge()
    {
        // A well-formed JSON body whose length exceeds the tiny test cap. The bounded reader must
        // abort before the whole body is buffered rather than parsing it.
        var bigJson = "{\"token\":\"" + new string('a', 4096) + "\"}";
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(bigJson, Encoding.UTF8, "application/json")
        };

        var act = async () => await CopilotOAuth.ReadBoundedJsonAsync(response, maxBytes: 1024, CancellationToken.None);

        var ex = await act.ShouldThrowAsync<ResponseContentTooLargeException>();
        ex.MaxBytes.ShouldBe(1024);
    }

    [Fact]
    public async Task ReadBoundedJsonAsync_OverCapDeclaredContentLength_RejectsWithoutReadingBody()
    {
        // A declared Content-Length larger than the cap must be rejected up front, before a single
        // body byte is pulled. The stream never ends if read, so reaching the assertion proves the
        // cheap declared-length rejection fired.
        using var stream = new NeverEndingStream();
        var content = new StreamContent(stream);
        content.Headers.ContentLength = long.MaxValue;
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };

        var act = async () => await CopilotOAuth.ReadBoundedJsonAsync(response, maxBytes: 1024, CancellationToken.None);

        var ex = await act.ShouldThrowAsync<ResponseContentTooLargeException>();
        ex.ObservedBytes.ShouldBe(long.MaxValue);
        stream.BytesRead.ShouldBe(0, "an over-cap declared Content-Length must reject before reading the body");
    }

    [Fact]
    public async Task ReadBoundedJsonAsync_UnboundedNoLengthBody_AbortsMidFlight()
    {
        // No Content-Length (chunked / lying endpoint). The streaming read itself must abort once it
        // has read past the cap, proving the full (infinite) body is never buffered before parsing.
        using var stream = new NeverEndingStream();
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };

        var act = async () => await CopilotOAuth.ReadBoundedJsonAsync(response, maxBytes: 1024, CancellationToken.None);

        await act.ShouldThrowAsync<ResponseContentTooLargeException>();
        stream.BytesRead.ShouldBeLessThan(10L * 1024 * 1024, "the reader must abort a chunk past the cap, not drain the infinite body");
        stream.BytesRead.ShouldBeGreaterThan(0);
    }

    /// <summary>
    /// A read stream that returns bytes forever - stands in for a hostile endpoint streaming an
    /// unbounded body. Records how many bytes were actually pulled so a test can prove the bounded
    /// reader aborted instead of draining it.
    /// </summary>
    private sealed class NeverEndingStream : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            // Fill the requested span with a constant byte and count it - never signals end-of-stream.
            Array.Fill(buffer, (byte)'a', offset, count);
            BytesRead += count;
            return count;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            buffer.Span.Fill((byte)'a');
            BytesRead += buffer.Length;
            return ValueTask.FromResult(buffer.Length);
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
