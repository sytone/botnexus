using BotNexus.Gateway.Sessions;

namespace BotNexus.Gateway.Sessions.Tests;

public sealed class TranscriptSecretRedactorTests
{
    [Theory]
    [InlineData("here is sk-abcd1234EFGH5678ijkl9012MNOP3456 token")]
    [InlineData("ghp_1234567890abcdefABCDEF1234567890abcdEF")]
    [InlineData("gho_1234567890abcdefABCDEF1234567890abcdEF")]
    [InlineData("ghs_1234567890abcdefABCDEF1234567890abcdEF")]
    [InlineData("ghu_1234567890abcdefABCDEF1234567890abcdEF")]
    [InlineData("ghr_1234567890abcdefABCDEF1234567890abcdEF")]
    [InlineData("key AKIAIOSFODNN7EXAMPLE end")]
    [InlineData("Authorization: Bearer abcDEF123456.tokenBlobHere_9876")]
    [InlineData("password=hunter2secret")]
    [InlineData("api_key=abcdef123456")]
    [InlineData("apikey=abcdef123456")]
    [InlineData("secret=topsecretvalue1")]
    [InlineData("token=abcdef123456xyz")]
    [InlineData("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c")]
    public void Redact_ReplacesKnownSecretShapes(string input)
    {
        var result = TranscriptSecretRedactor.Redact(input);

        Assert.Contains("[redacted-secret]", result);
    }

    [Theory]
    [InlineData("The quick brown fox jumps over the lazy dog.")]
    [InlineData("var x = ComputeTotal(items, taxRate);")]
    [InlineData("Contact me at user@example.com about the meeting.")]
    [InlineData("Order #1618 shipped on 2026-06-12 successfully.")]
    [InlineData("password reset instructions were emailed to you")]
    public void Redact_LeavesOrdinaryTextUntouched(string input)
    {
        var result = TranscriptSecretRedactor.Redact(input);

        Assert.Equal(input, result);
    }

    [Fact]
    public void Redact_HandlesAstralAndEmptyContentSafely()
    {
        Assert.Equal(string.Empty, TranscriptSecretRedactor.Redact(string.Empty));
        Assert.Null(TranscriptSecretRedactor.Redact(null));

        const string astral = "emoji \U0001F600 and text ghp_1234567890abcdefABCDEF1234567890abcdEF done";
        var result = TranscriptSecretRedactor.Redact(astral);
        Assert.Contains("[redacted-secret]", result);
        Assert.Contains("\U0001F600", result);
    }
}
