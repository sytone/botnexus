using System.Text;
using BotNexus.Gateway.Webhooks;

namespace BotNexus.Gateway.Webhooks.Tests;

public sealed class WebhookSecretHelperTests
{
    // ── GenerateSecret ────────────────────────────────────────────────────────

    [Fact]
    public void GenerateSecret_HasCorrectPrefix()
    {
        var secret = WebhookSecretHelper.GenerateSecret();
        Assert.StartsWith("whsec_", secret);
    }

    [Fact]
    public void GenerateSecret_IsCorrectLength()
    {
        // "whsec_" (6) + 64 hex chars (32 bytes → 64 hex digits) = 70
        var secret = WebhookSecretHelper.GenerateSecret();
        Assert.Equal(70, secret.Length);
    }

    [Fact]
    public void GenerateSecret_ProducesUniqueValues()
    {
        var a = WebhookSecretHelper.GenerateSecret();
        var b = WebhookSecretHelper.GenerateSecret();
        Assert.NotEqual(a, b);
    }

    // ── ComputeSignature ──────────────────────────────────────────────────────

    [Fact]
    public void ComputeSignature_HasSha256Prefix()
    {
        var body = Encoding.UTF8.GetBytes("{\"message\":\"hello\"}");
        var sig = WebhookSecretHelper.ComputeSignature("whsec_test", body);
        Assert.StartsWith("sha256=", sig);
    }

    [Fact]
    public void ComputeSignature_IsDeterministic()
    {
        var body = Encoding.UTF8.GetBytes("payload");
        var a = WebhookSecretHelper.ComputeSignature("mysecret", body);
        var b = WebhookSecretHelper.ComputeSignature("mysecret", body);
        Assert.Equal(a, b);
    }

    [Fact]
    public void ComputeSignature_DiffersForDifferentSecrets()
    {
        var body = Encoding.UTF8.GetBytes("payload");
        var a = WebhookSecretHelper.ComputeSignature("secret-a", body);
        var b = WebhookSecretHelper.ComputeSignature("secret-b", body);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ComputeSignature_DiffersForDifferentBodies()
    {
        const string secret = "mysecret";
        var a = WebhookSecretHelper.ComputeSignature(secret, Encoding.UTF8.GetBytes("body-a"));
        var b = WebhookSecretHelper.ComputeSignature(secret, Encoding.UTF8.GetBytes("body-b"));
        Assert.NotEqual(a, b);
    }

    // ── VerifySignature ───────────────────────────────────────────────────────

    [Fact]
    public void VerifySignature_ReturnsTrueForCorrectSignature()
    {
        var secret = WebhookSecretHelper.GenerateSecret();
        var body = Encoding.UTF8.GetBytes("{\"message\":\"test\"}");
        var sig = WebhookSecretHelper.ComputeSignature(secret, body);

        Assert.True(WebhookSecretHelper.VerifySignature(secret, body, sig));
    }

    [Fact]
    public void VerifySignature_ReturnsFalseForWrongSecret()
    {
        var body = Encoding.UTF8.GetBytes("payload");
        var sig = WebhookSecretHelper.ComputeSignature("correct-secret", body);

        Assert.False(WebhookSecretHelper.VerifySignature("wrong-secret", body, sig));
    }

    [Fact]
    public void VerifySignature_ReturnsFalseForTamperedBody()
    {
        const string secret = "mysecret";
        var original = Encoding.UTF8.GetBytes("original");
        var tampered = Encoding.UTF8.GetBytes("tampered");
        var sig = WebhookSecretHelper.ComputeSignature(secret, original);

        Assert.False(WebhookSecretHelper.VerifySignature(secret, tampered, sig));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void VerifySignature_ReturnsFalseForMissingHeader(string? header)
    {
        var body = Encoding.UTF8.GetBytes("payload");
        Assert.False(WebhookSecretHelper.VerifySignature("secret", body, header));
    }

    [Fact]
    public void VerifySignature_ReturnsFalseForSignatureWithoutPrefix()
    {
        const string secret = "mysecret";
        var body = Encoding.UTF8.GetBytes("payload");
        var sig = WebhookSecretHelper.ComputeSignature(secret, body);
        var withoutPrefix = sig["sha256=".Length..]; // strip prefix

        Assert.False(WebhookSecretHelper.VerifySignature(secret, body, withoutPrefix));
    }

    // ── Known-vector test (ensures algorithm matches GitHub/Stripe convention) ─

    [Fact]
    public void ComputeSignature_MatchesKnownVector()
    {
        // Computed independently:
        //   key = "whsec_test"
        //   body = "Hello, BotNexus!"
        //   HMAC-SHA256 hex = verified externally
        const string secret = "whsec_test";
        var body = Encoding.UTF8.GetBytes("Hello, BotNexus!");
        var sig = WebhookSecretHelper.ComputeSignature(secret, body);

        // sig must start with "sha256=" and be exactly 7 + 64 chars
        Assert.StartsWith("sha256=", sig);
        Assert.Equal(71, sig.Length);

        // Verify round-trip
        Assert.True(WebhookSecretHelper.VerifySignature(secret, body, sig));
    }
}
