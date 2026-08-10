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

    // --- OAuth envelope-shape validation (#2894) ---
    //
    // ReadBoundedJsonAsync is the single choke-point for all three peer-controlled OAuth reads
    // (device-code :67, access-token :94, copilot-token :187). Before this fix it returned the
    // parsed root element without checking its kind, so a `null`, array or scalar body reached
    // GetProperty and produced a raw System.Text.Json InvalidOperationException instead of a
    // controlled authentication failure. The message must name the endpoint and the observed
    // JsonValueKind and must never carry any response-body text.

    [Theory]
    [InlineData("null", JsonValueKind.Null)]
    [InlineData("[]", JsonValueKind.Array)]
    [InlineData("""["device_code","leaked_secret_value"]""", JsonValueKind.Array)]
    [InlineData("\"leaked_secret_value\"", JsonValueKind.String)]
    [InlineData("42", JsonValueKind.Number)]
    [InlineData("true", JsonValueKind.True)]
    [InlineData("false", JsonValueKind.False)]
    public async Task ReadBoundedJsonAsync_NonObjectRoot_ThrowsControlledEnvelopeFailure(
        string body, JsonValueKind expectedKind)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/device/code");
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
            RequestMessage = request
        };

        var act = async () => await CopilotOAuth.ReadBoundedJsonAsync(response, maxBytes: 1024, CancellationToken.None);

        var ex = await act.ShouldThrowAsync<InvalidOperationException>();
        ex.Message.ShouldContain("https://github.com/login/device/code");
        ex.Message.ShouldContain(expectedKind.ToString());
        // The body must never be echoed - it can carry a reflected credential.
        ex.Message.ShouldNotContain("leaked_secret_value");
        // Bodies that are bare JSON keywords (null/true/false) share their spelling with the
        // JsonValueKind name the diagnostic is required to report, so a literal substring check
        // would collide with the very kind token asserted above. Only assert non-echo for bodies
        // whose text is distinguishable from the kind name.
        if (!string.Equals(body, expectedKind.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            ex.Message.ShouldNotContain(body);
        }
    }

    // All three production call sites funnel through the same seam, so each endpoint URL must
    // surface in its own controlled failure rather than a System.Text.Json type exception.
    [Theory]
    [InlineData("https://github.com/login/device/code")]
    [InlineData("https://github.com/login/oauth/access_token")]
    [InlineData("https://api.github.com/copilot_internal/v2/token")]
    public async Task ReadBoundedJsonAsync_NonObjectRoot_NamesTheCallingEndpoint(string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[]", Encoding.UTF8, "application/json"),
            RequestMessage = request
        };

        var act = async () => await CopilotOAuth.ReadBoundedJsonAsync(response, maxBytes: 1024, CancellationToken.None);

        var ex = await act.ShouldThrowAsync<InvalidOperationException>();
        ex.Message.ShouldContain(url);
        ex.Message.ShouldContain("Array");
    }

    [Fact]
    public async Task ReadBoundedJsonAsync_NonObjectRootWithNoRequestMessage_StillThrowsWithoutBody()
    {
        // A response constructed without a RequestMessage must still fail controlled rather than
        // NullReferenceException while building the diagnostic.
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""["leaked_secret_value"]""", Encoding.UTF8, "application/json")
        };

        var act = async () => await CopilotOAuth.ReadBoundedJsonAsync(response, maxBytes: 1024, CancellationToken.None);

        var ex = await act.ShouldThrowAsync<InvalidOperationException>();
        ex.Message.ShouldContain("Array");
        ex.Message.ShouldNotContain("leaked_secret_value");
    }

    [Fact]
    public async Task ReadBoundedJsonAsync_ObjectRoot_StillReturnsTheElement()
    {
        // The shape check must not disturb the happy path for any of the three call sites.
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"device_code":"dc","user_code":"UC-1","verification_uri":"https://github.com/login/device"}""",
                Encoding.UTF8,
                "application/json")
        };

        var element = await CopilotOAuth.ReadBoundedJsonAsync(response, maxBytes: 1024, CancellationToken.None);

        element.ValueKind.ShouldBe(JsonValueKind.Object);
        element.GetProperty("device_code").GetString().ShouldBe("dc");
    }

    // The device-code envelope reads three required string fields. A well-formed object that is
    // missing one - or carries a non-string value - must fail by name, not via the null-forgiving
    // `!` operator downstream.
    [Theory]
    [InlineData("device_code")]
    [InlineData("user_code")]
    [InlineData("verification_uri")]
    public void RequireStringProperty_MissingProperty_ThrowsNamedFailure(string missing)
    {
        var fields = new Dictionary<string, string>
        {
            ["device_code"] = "dc",
            ["user_code"] = "UC-1",
            ["verification_uri"] = "https://github.com/login/device"
        };
        fields.Remove(missing);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(fields));
        var root = doc.RootElement;

        var ex = Should.Throw<InvalidOperationException>(
            () => CopilotOAuth.RequireStringProperty(root, missing, "https://github.com/login/device/code"));

        ex.Message.ShouldContain(missing);
        ex.Message.ShouldContain("https://github.com/login/device/code");
    }

    [Theory]
    [InlineData("""{"device_code":null}""")]
    [InlineData("""{"device_code":123}""")]
    [InlineData("""{"device_code":[]}""")]
    [InlineData("""{"device_code":{}}""")]
    public void RequireStringProperty_NonStringProperty_ThrowsNamedFailure(string body)
    {
        using var doc = JsonDocument.Parse(body);

        var ex = Should.Throw<InvalidOperationException>(
            () => CopilotOAuth.RequireStringProperty(doc.RootElement, "device_code", "https://github.com/login/device/code"));

        ex.Message.ShouldContain("device_code");
        ex.Message.ShouldNotContain("123");
    }

    [Fact]
    public void RequireStringProperty_PresentStringProperty_ReturnsValue()
    {
        using var doc = JsonDocument.Parse("""{"device_code":"dc-1234"}""");

        CopilotOAuth.RequireStringProperty(doc.RootElement, "device_code", "https://github.com/login/device/code")
            .ShouldBe("dc-1234");
    }

    [Fact]
    public void RequireStringProperty_NeverEchoesTheValueOfOtherFields()
    {
        // The failure diagnostic names the missing field only; sibling values may be credentials.
        using var doc = JsonDocument.Parse("""{"access_token":"gho_supersecrettoken1234567890"}""");

        var ex = Should.Throw<InvalidOperationException>(
            () => CopilotOAuth.RequireStringProperty(doc.RootElement, "device_code", "https://github.com/login/device/code"));

        ex.Message.ShouldNotContain("gho_supersecrettoken1234567890");
    }


    // BuildOAuthErrorMessage must never echo the peer-supplied error_description (#1884): GitHub
    // OAuth error bodies can reflect the just-submitted refresh_token / device_code back inside
    // error_description, so only the machine-readable error code may appear in the exception text.
    [Fact]
    public void BuildOAuthErrorMessage_IncludesOnlyTheErrorCode()
    {
        var msg = CopilotOAuth.BuildOAuthErrorMessage("invalid_grant");
        msg.ShouldBe("GitHub OAuth error: invalid_grant");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildOAuthErrorMessage_NullOrBlankError_FallsBackToUnknown(string? error)
    {
        var msg = CopilotOAuth.BuildOAuthErrorMessage(error);
        msg.ShouldBe("GitHub OAuth error: unknown_error");
    }

    [Fact]
    public void BuildOAuthErrorMessage_NeverContainsReflectedSecret()
    {
        // Even if a caller somehow passed a reflected credential as the error *code* (it cannot in
        // practice), the message shape is fixed to the code we pass and carries no description field.
        // This test locks the contract that the description is never interpolated.
        var secret = "gho_supersecretrefreshtoken1234567890";
        var msg = CopilotOAuth.BuildOAuthErrorMessage("invalid_grant");
        msg.ShouldNotContain(secret);
        msg.ShouldNotContain("error_description");
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
