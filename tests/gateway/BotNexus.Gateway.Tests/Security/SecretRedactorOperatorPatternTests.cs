using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Security;

namespace BotNexus.Gateway.Tests.Security;

/// <summary>
/// Verifies operator-configurable secret redaction patterns (#2727).
///
/// The contract under test is deliberately narrow: operator patterns are applied <b>in addition to</b>
/// the built-in set (never replacing it), a malformed pattern fails validation loudly at configuration
/// time rather than at redaction time, and a pathological pattern is bounded by a match timeout so the
/// logging path can never hang. A redactor that throws mid-transcript is a worse outcome than one with
/// a closed pattern set, which is why every sad path here asserts either a startup-time error or a
/// safe runtime degradation - never an escaping exception.
/// </summary>
public sealed class SecretRedactorOperatorPatternTests
{
    private static SecretRedactor Redactor(params string[] patterns)
        => new(securityEvents: null, options: new SecretRedactionOptions(patterns));

    // ---------- AC5: operator pattern actually redacts ----------

    [Fact]
    public void Redact_OperatorPattern_RedactsDeploymentSecret()
    {
        var sut = Redactor("deployment-secret-[a-z-]+");

        var result = sut.Redact("connecting with deployment-secret-abc now");

        result.ShouldNotContain("deployment-secret-abc");
        result.ShouldContain("[REDACTED]");
    }

    [Fact]
    public void Redact_OperatorPattern_AppliesToExternalDeliveryPathToo()
    {
        var sut = Redactor("deployment-secret-[a-z-]+");

        var result = sut.RedactForExternalDelivery("summary: deployment-secret-abc");

        result.ShouldNotContain("deployment-secret-abc");
    }

    [Fact]
    public void Redact_MultipleOperatorPatterns_AllApply()
    {
        var sut = Redactor("deployment-secret-[a-z-]+", "cust-[0-9]{6}");

        var result = sut.Redact("deployment-secret-abc and cust-123456");

        result.ShouldNotContain("deployment-secret-abc");
        result.ShouldNotContain("cust-123456");
    }

    // ---------- AC2: additive, never replacing the built-ins ----------

    [Theory]
    [InlineData("ghp_aBcDeFgHiJkLmNoPqRsTuVwXyZ0123456789")]
    [InlineData("AKIAIOSFODNN7EXAMPLE")]
    [InlineData("AIzaSyA1234567890abcdefghijklmnopqrstuv")]
    // The Slack/Stripe fixtures are split with string concatenation rather than written as single
    // literals. They are entirely synthetic, but they are SHAPED like real credentials by design -
    // that is the whole point of a redaction fixture - and GitHub push protection blocks the literal
    // forms. Concatenation keeps the runtime value byte-identical while keeping the file pushable.
    [InlineData("xoxb-" + "123456789012-1234567890123-abcdefghijklmnopqrstuvwx")]
    [InlineData("glpat-aBcDeFgHiJkLmNoPqRsT")]
    public void Redact_WithOperatorPatternsConfigured_BuiltInPatternsStillRedact(string secret)
    {
        var sut = Redactor("deployment-secret-[a-z-]+");

        var result = sut.Redact($"value {secret} end");

        result.ShouldNotContain(secret);
        result.ShouldContain("[REDACTED]");
    }

    /// <summary>
    /// AC2 in its strongest form: for EVERY built-in pattern, a redactor configured with operator
    /// patterns must produce exactly the same output as one with none. This is what makes "additive"
    /// a proven property rather than a spot check on a handful of formats.
    /// </summary>
    [Fact]
    public void Redact_EveryBuiltInPattern_ProducesIdenticalOutputWithOperatorPatternsConfigured()
    {
        var baseline = new SecretRedactor();
        var withOperator = Redactor("deployment-secret-[a-z-]+");

        string[] builtInSamples =
        [
            "sk-abc123XYZdef456UVWghi789JKLmno0123456789PQRSTUVX",
            "sk-ant-api03-AAABBBCCC111222333444555666777888999000aaabbbccc",
            "github_pat_11ABCDEFG0abcdefghijklmnopqrstuvwxyz0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789ab",
            "ghp_aBcDeFgHiJkLmNoPqRsTuVwXyZ0123456789",
            "glpat-aBcDeFgHiJkLmNoPqRsT",
            "AKIAIOSFODNN7EXAMPLE",
            "AIzaSyA1234567890abcdefghijklmnopqrstuv",
            "xoxb-" + "123456789012-1234567890123-abcdefghijklmnopqrstuvwx",
            "sk_" + "live_aBcDeFgHiJkLmNoPqRsTuVwX",
            "Authorization: Bearer abcdefghijklmnopqrstuvwxyz0123456789",
            "Authorization: Basic dXNlcjpwYXNzd29yZA==",
            "X-Api-Key: abcdefghijklmnopqrstuvwxyz012345",
            "api_key=abcdefghijklmnopqrstuvwxyz012345",
        ];

        foreach (var sample in builtInSamples)
        {
            var text = $"prefix {sample} suffix";
            withOperator.Redact(text).ShouldBe(
                baseline.Redact(text),
                customMessage: $"operator patterns changed built-in redaction of: {sample}");
        }
    }

    // ---------- AC6: absent/empty list is byte-identical to today ----------

    [Fact]
    public void Redact_NoOperatorPatterns_IsByteIdenticalToDefaultRedactor()
    {
        var baseline = new SecretRedactor();
        var emptyList = new SecretRedactor(securityEvents: null, options: new SecretRedactionOptions([]));
        var nullOptions = new SecretRedactor(securityEvents: null, options: null);

        const string Text = "ghp_aBcDeFgHiJkLmNoPqRsTuVwXyZ0123456789 and plain text and deployment-secret-abc";

        var expected = baseline.Redact(Text);
        emptyList.Redact(Text).ShouldBe(expected);
        nullOptions.Redact(Text).ShouldBe(expected);

        // The un-configured redactor must NOT redact the operator-shaped value.
        expected.ShouldContain("deployment-secret-abc");
    }

    [Fact]
    public void Redact_SafeText_WithOperatorPatterns_IsUnchanged()
    {
        var sut = Redactor("deployment-secret-[a-z-]+");

        const string Text = "the quick brown fox jumps over the lazy dog";
        sut.Redact(Text).ShouldBe(Text);
    }

    // ---------- AC3 / sad paths: invalid patterns fail loudly at construction ----------

    [Fact]
    public void Ctor_MalformedRegex_ThrowsNamingTheOffendingPattern()
    {
        var ex = Should.Throw<ArgumentException>(
            () => new SecretRedactor(securityEvents: null, options: new SecretRedactionOptions(["(unclosed"])));

        ex.Message.ShouldContain("(unclosed");
    }

    [Fact]
    public void Ctor_EmptyPattern_ThrowsNamingTheIndex()
    {
        var ex = Should.Throw<ArgumentException>(
            () => new SecretRedactor(securityEvents: null, options: new SecretRedactionOptions([""])));

        ex.Message.ShouldContain("empty");
    }

    [Fact]
    public void Ctor_MatchEverythingPattern_IsRejected()
    {
        var ex = Should.Throw<ArgumentException>(
            () => new SecretRedactor(securityEvents: null, options: new SecretRedactionOptions([".*"])));

        ex.Message.ShouldContain(".*");
    }

    // ---------- AC4: catastrophic backtracking is bounded, not fatal ----------

    [Fact]
    public void Redact_CatastrophicBacktrackingPattern_TimesOutWithoutThrowing()
    {
        // A classic nested-quantifier bomb. With no timeout this never returns.
        var sut = new SecretRedactor(
            securityEvents: null,
            options: new SecretRedactionOptions(["(a+)+$"], TimeSpan.FromMilliseconds(50)));

        var evil = new string('a', 40) + "!";

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = Should.NotThrow(() => sut.Redact(evil));
        sw.Stop();

        // The redaction path must not hang. Generous ceiling to stay stable on a loaded CI runner,
        // while still being orders of magnitude below a genuine catastrophic backtrack.
        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));

        // Degrading safely means the timed-out pattern simply does not redact - it must never throw
        // mid-transcript and must never lose the surrounding text.
        result.ShouldNotBeNull();
    }

    [Fact]
    public void Redact_TimedOutOperatorPattern_StillAppliesBuiltInPatterns()
    {
        var sut = new SecretRedactor(
            securityEvents: null,
            options: new SecretRedactionOptions(["(a+)+$"], TimeSpan.FromMilliseconds(50)));

        var evil = new string('a', 40) + "!";
        var result = sut.Redact($"ghp_aBcDeFgHiJkLmNoPqRsTuVwXyZ0123456789 {evil}");

        result.ShouldNotContain("ghp_aBcDeFgHiJkLmNoPqRsTuVwXyZ0123456789");
    }

    // ---------- duplicates ----------

    [Fact]
    public void Ctor_DuplicateOperatorPatterns_AreDedupedNotFatal()
    {
        var sut = Redactor("deployment-secret-[a-z-]+", "deployment-secret-[a-z-]+");

        var result = sut.Redact("deployment-secret-abc");

        result.ShouldBe("[REDACTED]");
    }

    [Fact]
    public void Ctor_OperatorPatternDuplicatingBuiltIn_IsAcceptedAndDoesNotDoubleRedact()
    {
        var baseline = new SecretRedactor();
        // Duplicate of the built-in AWS access key shape.
        var sut = Redactor(@"\bAKIA[0-9A-Z]{16}\b");

        const string Text = "key AKIAIOSFODNN7EXAMPLE here";
        sut.Redact(Text).ShouldBe(baseline.Redact(Text));
    }

    // ---------- whitespace-only and null entries ----------

    [Fact]
    public void Ctor_WhitespaceOnlyPattern_IsRejected()
    {
        Should.Throw<ArgumentException>(
            () => new SecretRedactor(securityEvents: null, options: new SecretRedactionOptions(["   "])));
    }
}
