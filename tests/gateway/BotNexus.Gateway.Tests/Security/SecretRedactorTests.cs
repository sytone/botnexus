using BotNexus.Gateway.Security;

namespace BotNexus.Gateway.Tests.Security;

/// <summary>
/// Verifies that <see cref="SecretRedactor"/> replaces common secret-shaped values
/// with <c>[REDACTED]</c> and leaves safe text untouched.
/// </summary>
public sealed class SecretRedactorTests
{
    private readonly SecretRedactor _sut = new();

    [Fact]
    public void Redact_NullOrEmpty_ReturnsInput()
    {
        _sut.Redact(string.Empty).ShouldBe(string.Empty);
    }

    [Theory]
    [InlineData("sk-abc123XYZdef456UVWghi789JKLmno0123456789PQRSTUVX")]        // 48-char OpenAI legacy key
    [InlineData("sk-proj-aBcDeFgHiJkLmNoPqRsTuVwXyZ0123456789abcdefghijklmnopqrstuvwxyz0123456789ABCDE")]
    public void Redact_OpenAiKey_IsRedacted(string key)
    {
        var input = $"Authorization: Bearer {key}";
        _sut.Redact(input).ShouldNotContain(key);
        _sut.Redact(input).ShouldContain("[REDACTED]");
    }

    [Theory]
    [InlineData("sk-ant-api03-AAABBBCCC111222333444555666777888999000aaabbbccc")]
    [InlineData("sk-ant-xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx")]
    public void Redact_AnthropicKey_IsRedacted(string key)
    {
        var input = $"key={key}";
        _sut.Redact(input).ShouldNotContain(key);
        _sut.Redact(input).ShouldContain("[REDACTED]");
    }

    [Theory]
    [InlineData("ghp_aBcDeFgHiJkLmNoPqRsTuVwXyZ0123456789")]   // 36 chars after prefix
    [InlineData("ghs_aBcDeFgHiJkLmNoPqRsTuVwXyZ0123456789")]
    [InlineData("gho_aBcDeFgHiJkLmNoPqRsTuVwXyZ0123456789")]
    [InlineData("github_pat_aBcDeFgHiJkLmNoPqRsTuVwXyZ0123456789_ABCDEF123456789012345678")]
    public void Redact_GitHubToken_IsRedacted(string token)
    {
        var input = $"token: {token}";
        _sut.Redact(input).ShouldNotContain(token);
        _sut.Redact(input).ShouldContain("[REDACTED]");
    }

    [Theory]
    [InlineData("AKIAIOSFODNN7EXAMPLE")]     // 20 chars: AKIA + 16 uppercase alphanumeric
    [InlineData("AKIAJ1234567890ABCDE")]
    public void Redact_AwsAccessKey_IsRedacted(string key)
    {
        var input = $"aws_access_key_id={key}";
        _sut.Redact(input).ShouldNotContain(key);
        _sut.Redact(input).ShouldContain("[REDACTED]");
    }

    [Theory]
    [InlineData("AIzaSyDummyGoogleApiKey1234567890ABCDE")]   // AIza + 35 chars
    public void Redact_GoogleApiKey_IsRedacted(string key)
    {
        var input = $"apiKey: {key}";
        _sut.Redact(input).ShouldNotContain(key);
        _sut.Redact(input).ShouldContain("[REDACTED]");
    }

    [Theory]
    [InlineData("xoxb-TESTONLY-TESTONLY-AbCdEfGhIjKlMnOpQrStUv")]  // Slack bot token (test-only, not a real token)
    [InlineData("xoxp-TESTONLY-TESTONLY-TESTONLY-abcdef1234567890")]  // Slack user token (test-only, not a real token)
    public void Redact_SlackToken_IsRedacted(string token)
    {
        var input = $"slack_token={token}";
        _sut.Redact(input).ShouldNotContain(token);
        _sut.Redact(input).ShouldContain("[REDACTED]");
    }

    [Theory]
    [InlineData("sk_live_AbCdEfGhIjKlMnOpQrSt")]   // Stripe live secret key
    [InlineData("sk_test_AbCdEfGhIjKlMnOpQrSt")]   // Stripe test secret key
    public void Redact_StripeKey_IsRedacted(string key)
    {
        var input = $"stripe_key={key}";
        _sut.Redact(input).ShouldNotContain(key);
        _sut.Redact(input).ShouldContain("[REDACTED]");
    }

    [Theory]
    [InlineData("123456789:AAExxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx")]   // full bot token (id + 35-char secret)
    [InlineData("987654:AbCdEfGhIjKlMnOpQrStUvWx-_012345678")]     // 35-char base64url secret incl - and _
    public void Redact_TelegramBotToken_IsRedacted(string token)
    {
        // Mirrors the token embedded in TelegramBotApiClient endpoint/file URLs
        // (https://api.telegram.org/bot{token}/...), the credential #1929 guards.
        var input = $"https://api.telegram.org/bot{token}/getUpdates";
        _sut.Redact(input).ShouldNotContain(token);
        _sut.Redact(input).ShouldContain("[REDACTED]");
    }

    [Fact]
    public void Redact_BareTelegramBotToken_IsRedacted()
    {
        const string token = "123456789:AAExxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx";
        _sut.Redact(token).ShouldNotContain(token);
        _sut.Redact(token).ShouldContain("[REDACTED]");
    }

    [Fact]
    public void Redact_AuthorizationBearerHeader_IsRedacted()
    {
        const string token = "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.SflKxwR";
        var input = $"Authorization: Bearer {token}";
        _sut.Redact(input).ShouldNotContain(token);
        _sut.Redact(input).ShouldContain("[REDACTED]");
    }

    [Fact]
    public void Redact_AuthorizationBasicHeader_IsRedacted()
    {
        // Basic auth base64-encodes user:password; the full credential must not survive.
        const string cred = "dXNlcm5hbWU6c3VwZXJzZWNyZXRwYXNzd29yZA==";
        var input = $"Authorization: Basic {cred}";
        var result = _sut.Redact(input);
        result.ShouldNotContain(cred);
        result.ShouldContain("[REDACTED]");
    }

    [Fact]
    public void Redact_AuthorizationBotHeader_IsRedacted()
    {
        // Discord-style bot token.
        const string token = "MTIzNDU2Nzg5MDEyMzQ1Njc4.AbCdEf.GhIjKlMnOpQrStUvWxYz012345";
        var input = $"Authorization: Bot {token}";
        var result = _sut.Redact(input);
        result.ShouldNotContain(token);
        result.ShouldContain("[REDACTED]");
    }

    [Fact]
    public void Redact_ProxyAuthorizationHeader_IsRedacted()
    {
        const string cred = "aGVsbG86d29ybGRzZWNyZXR2YWx1ZQ==";
        var input = $"Proxy-Authorization: Basic {cred}";
        var result = _sut.Redact(input);
        result.ShouldNotContain(cred);
        result.ShouldContain("[REDACTED]");
    }

    [Theory]
    [InlineData("X-Api-Key")]
    [InlineData("X-Auth-Token")]
    [InlineData("X-OpenClaw-Token")]
    public void Redact_ApiKeyStyleHeader_IsRedacted(string header)
    {
        const string secret = "aBcDeFgHiJkLmNoPqRsT1234";
        var input = $"{header}: {secret}";
        var result = _sut.Redact(input);
        result.ShouldNotContain(secret);
        result.ShouldContain("[REDACTED]");
    }

    [Fact]
    public void Redact_StandaloneBearerToken_IsRedacted()
    {
        // A raw diagnostic / exception line that prints "Bearer <token>" with no
        // Authorization: header name in front still leaks the token.
        const string token = "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.SflKxwR";
        var input = $"HttpRequestException: sent header Bearer {token}";
        var result = _sut.Redact(input);
        result.ShouldNotContain(token);
        result.ShouldContain("[REDACTED]");
    }

    [Fact]
    public void Redact_QuotedAuthorizationHeader_IsRedacted()
    {
        // JSON-embedded / serialized header form: "Authorization": "Bearer <token>".
        const string token = "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.SflKxwR";
        var input = $"{{\"Authorization\": \"Bearer {token}\"}}";
        var result = _sut.Redact(input);
        result.ShouldNotContain(token);
        result.ShouldContain("[REDACTED]");
    }

    [Fact]
    public void Redact_WordBearerInProse_IsNotOverRedacted()
    {
        // The word "Bearer" in ordinary prose (no long token following) must survive.
        const string safe = "The bearer of this message is a friend.";
        _sut.Redact(safe).ShouldBe(safe);
    }

    [Fact]
    public void Redact_ShortApiKeyHeaderValue_IsNotRedacted()
    {
        // Values shorter than the minimum length must pass through.
        const string safe = "X-Api-Key: abc";
        _sut.Redact(safe).ShouldBe(safe);
    }

    [Fact]
    public void Redact_ApiKeyInQueryString_IsRedacted()
    {
        const string key = "AbCdEfGhIjKlMnOpQrStUvWxYz12345678901234";
        var input = $"https://api.example.com/data?api_key={key}";
        _sut.Redact(input).ShouldNotContain(key);
        _sut.Redact(input).ShouldContain("[REDACTED]");
    }

    [Fact]
    public void Redact_NormalText_IsUnchanged()
    {
        const string safe = "The quick brown fox jumps over the lazy dog.";
        _sut.Redact(safe).ShouldBe(safe);
    }

    [Fact]
    public void Redact_ShortToken_IsNotRedacted()
    {
        // Tokens shorter than the minimum length for each pattern should pass through
        const string safe = "token=abc123";
        _sut.Redact(safe).ShouldBe(safe);
    }

    [Fact]
    public void Redact_MultipleSecretsInOneLine_AllRedacted()
    {
        const string openAiKey = "sk-abc123XYZdef456UVWghi789JKLmno0123456789PQRSTUVX";
        const string awsKey = "AKIAIOSFODNN7EXAMPLE";
        var input = $"openai={openAiKey} aws={awsKey}";

        var result = _sut.Redact(input);
        result.ShouldNotContain(openAiKey);
        result.ShouldNotContain(awsKey);
        result.ShouldContain("[REDACTED]");
    }

    [Fact]
    public void Redact_IsIdempotent()
    {
        const string key = "sk-abc123XYZdef456UVWghi789JKLmno0123456789PQRSTUVX";
        var firstPass = _sut.Redact(key);
        var secondPass = _sut.Redact(firstPass);
        secondPass.ShouldBe(firstPass);
    }
}
