using System.Text;
using BotNexus.Domain.Security;

namespace BotNexus.Domain.Tests.Security;

/// <summary>
/// Tests for <see cref="WebhookSecret"/> (#2927) — the strong type that replaces bare
/// <see cref="string"/> webhook secrets at the channel authentication boundary.
/// </summary>
public sealed class WebhookSecretTests
{
    // --- AC1: cannot be constructed without validation, no implicit conversion from string ---

    [Fact]
    public void Type_ExposesNoPublicConstructorAndNoImplicitConversionFromString()
    {
        var type = typeof(WebhookSecret);

        // A public parameterised constructor would let any string become a secret.
        type.GetConstructors()
            .Where(c => c.GetParameters().Length > 0)
            .ShouldBeEmpty();

        // An implicit (or explicit) conversion operator would reintroduce the same hole.
        type.GetMethods()
            .Where(m => m.Name is "op_Implicit" or "op_Explicit")
            .Where(m => m.GetParameters().Any(p => p.ParameterType == typeof(string)))
            .ShouldBeEmpty();
    }

    [Fact]
    public void Default_HasNoValue_AndRevealThrows()
    {
        var uninitialised = default(WebhookSecret);

        uninitialised.HasValue.ShouldBeFalse();
        uninitialised.Length.ShouldBe(0);
        Should.Throw<InvalidOperationException>(() => uninitialised.Reveal());
    }

    [Theory]
    [InlineData("abcDEF123_-")]
    [InlineData("a")]
    public void TryCreate_AcceptsValidTokens(string value)
    {
        WebhookSecret.TryCreate(value, out var secret).ShouldBeTrue();
        secret.HasValue.ShouldBeTrue();
        secret.Reveal().ShouldBe(value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("has spaces")]
    [InlineData("has.dot")]
    [InlineData("has/slash")]
    [InlineData("has+plus")]
    public void TryCreate_RejectsInvalidTokens_AndYieldsNoInstance(string? value)
    {
        WebhookSecret.TryCreate(value, out var secret).ShouldBeFalse();
        secret.HasValue.ShouldBeFalse();
    }

    [Fact]
    public void TryCreate_RejectsTokenLongerThanMaxLength_AndAcceptsExactlyMaxLength()
    {
        WebhookSecret.TryCreate(new string('a', WebhookSecret.MaxLength + 1), out _).ShouldBeFalse();
        WebhookSecret.TryCreate(new string('a', WebhookSecret.MaxLength), out _).ShouldBeTrue();
    }

    [Fact]
    public void Create_ThrowsForInvalidInput()
        => Should.Throw<ArgumentException>(() => WebhookSecret.Create("has spaces"));

    // --- AC3: ToString() does not return the secret and it cannot reach a log sink ---

    [Fact]
    public void ToString_DoesNotReturnTheSecretValue()
    {
        const string raw = "super-secret-token-value";
        var secret = WebhookSecret.Create(raw);

        secret.ToString().ShouldBe(WebhookSecret.RedactedMarker);
        secret.ToString().ShouldNotContain(raw);
    }

    [Fact]
    public void RawValue_CannotReachALogSinkViaInterpolationOrFormatting()
    {
        const string raw = "leak-me-if-you-can";
        var secret = WebhookSecret.Create(raw);

        // Every accidental-disclosure route a caller could plausibly take. Each must be inert.
        var sink = new StringBuilder();
        sink.AppendLine($"interpolated: {secret}");
        sink.AppendLine("concatenated: " + secret);
        sink.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, "formatted: {0}", secret));
        sink.AppendLine($"boxed: {(object)secret}");
        sink.Append(secret);

        sink.ToString().ShouldNotContain(raw);

        // And the explicit unwrap is the ONLY thing that yields the value — proving the assertion
        // above is not vacuously true because the secret was never stored.
        secret.Reveal().ShouldBe(raw);
    }

    [Fact]
    public void RecordSynthesisedFormatting_DoesNotLeakTheBackingField()
    {
        const string raw = "record-printmembers-leak";
        var secret = WebhookSecret.Create(raw);

        // Record structs synthesise a PrintMembers that prints backing fields; it is overridden so
        // that even a caller reaching ToString through the record machinery gets nothing.
        $"{secret}".ShouldNotContain(raw);
        secret.ToString().ShouldNotContain(raw);
    }

    // --- Constant-time equality (the timing oracle a plain string comparison would reopen) ---

    [Fact]
    public void Equality_MatchesIdenticalSecrets()
    {
        var a = WebhookSecret.Create("identical-secret");
        var b = WebhookSecret.Create("identical-secret");

        a.Equals(b).ShouldBeTrue();
        (a == b).ShouldBeTrue();
        a.GetHashCode().ShouldBe(b.GetHashCode());
    }

    [Theory]
    [InlineData("expected-secret", "different-secret")]
    [InlineData("expected-secret", "expected-secre")]
    [InlineData("expected-secret", "expected-secretX")]
    [InlineData("expected-secret", "EXPECTED-SECRET")]
    public void Equality_RejectsMismatchedSecrets(string left, string right)
        => WebhookSecret.Create(left).Equals(WebhookSecret.Create(right)).ShouldBeFalse();

    [Fact]
    public void Equality_DefaultInstanceNeverMatches_NotEvenItself()
    {
        var uninitialised = default(WebhookSecret);

        // A polling-mode bot's absent secret must not authenticate an equally-absent header.
        uninitialised.Equals(default(WebhookSecret)).ShouldBeFalse();
        uninitialised.Equals(WebhookSecret.Create("anything")).ShouldBeFalse();
        WebhookSecret.Create("anything").Equals(uninitialised).ShouldBeFalse();
    }

    [Fact]
    public void GetHashCode_DoesNotExposeThePlaintext()
    {
        var secret = WebhookSecret.Create("hash-code-secret");

        // The digest-derived hash must not be the string's own hash code.
        secret.GetHashCode().ShouldNotBe("hash-code-secret".GetHashCode(StringComparison.Ordinal));
    }

    // --- Generate ---

    [Fact]
    public void Generate_ProducesValidDistinctSecrets()
    {
        var generated = Enumerable.Range(0, 100).Select(_ => WebhookSecret.Generate()).ToList();

        generated.ShouldAllBe(s => s.HasValue);
        generated.Select(s => s.Reveal()).ToHashSet().Count.ShouldBe(100);

        foreach (var secret in generated)
        {
            WebhookSecret.TryCreate(secret.Reveal(), out var roundTripped).ShouldBeTrue();
            roundTripped.Equals(secret).ShouldBeTrue();
        }
    }
}
