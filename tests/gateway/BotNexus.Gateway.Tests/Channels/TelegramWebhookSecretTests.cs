using BotNexus.Extensions.Channels.Telegram;

namespace BotNexus.Gateway.Tests.Channels;

/// <summary>
/// Unit tests for <see cref="TelegramWebhookSecret"/> — the generation and constant-time
/// validation of the Telegram webhook secret token that authenticates inbound webhook traffic.
/// </summary>
public sealed class TelegramWebhookSecretTests
{
    [Fact]
    public void Generate_ProducesTokenWithinBotApiCharsetAndLength()
    {
        for (var i = 0; i < 50; i++)
        {
            var token = TelegramWebhookSecret.Generate();

            token.Length.ShouldBeInRange(1, 256);
            token.ShouldAllBe(c =>
                (c >= 'A' && c <= 'Z') ||
                (c >= 'a' && c <= 'z') ||
                (c >= '0' && c <= '9') ||
                c == '_' ||
                c == '-');
        }
    }

    [Fact]
    public void Generate_ProducesDistinctTokens()
    {
        var tokens = Enumerable.Range(0, 100).Select(_ => TelegramWebhookSecret.Generate()).ToHashSet();

        // 32 bytes of entropy per token — collisions across 100 draws are astronomically unlikely.
        tokens.Count.ShouldBe(100);
    }

    [Theory]
    [InlineData("abcDEF123_-", true)]
    [InlineData("a", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("has spaces", false)]
    [InlineData("has.dot", false)]
    [InlineData("has/slash", false)]
    [InlineData("has+plus", false)]
    public void IsValid_EnforcesBotApiCharset(string? value, bool expected)
        => TelegramWebhookSecret.IsValid(value).ShouldBe(expected);

    [Fact]
    public void IsValid_RejectsTokenLongerThan256()
    {
        var tooLong = new string('a', 257);
        TelegramWebhookSecret.IsValid(tooLong).ShouldBeFalse();
    }

    [Fact]
    public void IsValid_AcceptsTokenOfExactly256()
    {
        var maxLength = new string('a', 256);
        TelegramWebhookSecret.IsValid(maxLength).ShouldBeTrue();
    }

    [Fact]
    public void Matches_ReturnsTrue_ForIdenticalSecrets()
    {
        var secret = TelegramWebhookSecret.Generate();
        TelegramWebhookSecret.Matches(secret, secret).ShouldBeTrue();
    }

    [Theory]
    [InlineData("expected-secret", "different-secret")]
    [InlineData("expected-secret", "expected-secre")]   // one char short
    [InlineData("expected-secret", "expected-secretX")] // one char long
    [InlineData("expected-secret", "EXPECTED-SECRET")]  // case differs
    public void Matches_ReturnsFalse_ForMismatchedSecrets(string expected, string provided)
        => TelegramWebhookSecret.Matches(expected, provided).ShouldBeFalse();

    [Theory]
    [InlineData(null, "anything")]
    [InlineData("", "anything")]
    [InlineData("expected", null)]
    [InlineData("expected", "")]
    public void Matches_ReturnsFalse_WhenEitherSideMissing(string? expected, string? provided)
        => TelegramWebhookSecret.Matches(expected, provided).ShouldBeFalse();
}
