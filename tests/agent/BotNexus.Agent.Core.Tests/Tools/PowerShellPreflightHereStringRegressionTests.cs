using BotNexus.Agent.Core.Tools;

namespace BotNexus.AgentCore.Tests.Tools;

/// <summary>
/// Regression suite for issue #2905 - the <c>The string is missing the terminator</c> rule scanned
/// raw text without modelling here-string regions, so an apostrophe or a double quote inside a
/// <c>@'</c>&#8230;<c>'@</c> body (where it is ordinary literal text) opened a phantom string and the
/// command was refused. A 7-day corpus replay measured that rule at 11 false / 5 genuine, i.e. it
/// had crossed the same threshold that got its <c>Nested quoting</c> sibling deleted in #2757.
/// </summary>
/// <remarks>
/// The fix models the here-string region rather than deleting the rule, because the rule's five
/// genuine rejections are real parser errors that must keep being refused. Every clause below is
/// paired: a shape that must PASS and a shape that must still be REFUSED, so relaxing the rule into
/// a no-op reddens this file by name.
/// </remarks>
public class PowerShellPreflightHereStringRegressionTests
{
    /// <summary>The exact minimal repro from the issue body.</summary>
    private const string ApostropheInSingleHereString =
        "$s = @'\nit's fine\n'@\n\"len=$($s.Length)\"";

    private const string DoubleQuoteInSingleHereString =
        "$s = @'\nsay \"hi\" now\n'@\n\"len=$($s.Length)\"";

    private const string QuotesInExpandableHereString =
        "$s = @\"\nit's \"quoted\" text\n\"@\n\"len=$($s.Length)\"";

    private const string MarkdownAppendIdiom =
        "$s = @'\n## Notes\n- it's a list\n- with \"quotes\"\n- and a | pipe\n'@\n"
        + "Add-Content -Path 'playbook/log.md' -Value $s";

    // === Clause 1: the repro shapes must not be refused. ===

    [Fact]
    public void Validate_ApostropheInSingleQuotedHereString_IsNotRefused() =>
        PowerShellPreflight.Validate(ApostropheInSingleHereString).ShouldBeNull();

    [Fact]
    public void Validate_DoubleQuoteInSingleQuotedHereString_IsNotRefused() =>
        PowerShellPreflight.Validate(DoubleQuoteInSingleHereString).ShouldBeNull();

    [Fact]
    public void Validate_QuotesInExpandableHereString_IsNotRefused() =>
        PowerShellPreflight.Validate(QuotesInExpandableHereString).ShouldBeNull();

    [Fact]
    public void Validate_MarkdownAppendIdiom_IsNotRefused() =>
        PowerShellPreflight.Validate(MarkdownAppendIdiom).ShouldBeNull();

    [Fact]
    public void ThrowIfInvalid_MarkdownAppendIdiom_DoesNotThrow()
    {
        var args = new[] { "-NoProfile", "-Command", MarkdownAppendIdiom };
        PowerShellPreflight.TryGetInlineScript(args, inlineScript: null, out var extracted).ShouldBeTrue();
        Should.NotThrow(() => PowerShellPreflight.ThrowIfInvalid(extracted));
    }

    /// <summary>
    /// A here-string body is inert for EVERY rule, not just the quote scanner: an unbalanced brace
    /// or a bare pipe inside the body is literal text and must not trip the brace or pipe rules.
    /// </summary>
    [Fact]
    public void Validate_UnbalancedBraceInsideHereStringBody_IsNotRefused() =>
        PowerShellPreflight.Validate("$s = @'\nif (x) { unclosed\n|\n'@\nWrite-Output $s").ShouldBeNull();

    // === Clause 3: the genuine failures must still be refused. ===

    [Fact]
    public void Validate_UnterminatedDoubleQuotedString_IsStillRefused()
    {
        var error = PowerShellPreflight.Validate("$s = \"abc");
        error.ShouldNotBeNull();
        error!.Message.ShouldContain("missing the terminator");
    }

    [Fact]
    public void Validate_UnterminatedSingleQuotedString_IsStillRefused()
    {
        var error = PowerShellPreflight.Validate("Get-Item 'unterminated");
        error.ShouldNotBeNull();
        error!.Message.ShouldContain("missing the terminator");
    }

    [Fact]
    public void Validate_UnterminatedHereString_IsStillRefused()
    {
        var error = PowerShellPreflight.Validate("$s = @'\nbody text\nWrite-Output $s");
        error.ShouldNotBeNull();
        error!.Message.ShouldContain("missing the terminator: '@");
    }

    [Fact]
    public void Validate_UnterminatedExpandableHereString_IsStillRefused()
    {
        var error = PowerShellPreflight.Validate("$s = @\"\nbody text\nWrite-Output $s");
        error.ShouldNotBeNull();
        error!.Message.ShouldContain("missing the terminator: \"@");
    }

    /// <summary>
    /// A terminator must start the line. <c>'@</c> appearing mid-line is body text, so this
    /// here-string is genuinely unterminated and the real parser refuses it too.
    /// </summary>
    [Fact]
    public void Validate_HereStringTerminatorNotAtLineStart_IsStillRefused()
    {
        var error = PowerShellPreflight.Validate("$s = @'\nbody '@ still body\nWrite-Output $s");
        error.ShouldNotBeNull();
        error!.Message.ShouldContain("missing the terminator");
    }

    /// <summary>
    /// <c>@'x'</c> is NOT a here-string opener (no end-of-line after the quote), so the ordinary
    /// single-quote scanner still owns it and an unterminated one is still refused.
    /// </summary>
    [Fact]
    public void Validate_AtQuoteWithoutNewline_IsNotTreatedAsHereString()
    {
        PowerShellPreflight.Validate("Write-Output @'literal'").ShouldBeNull();

        var error = PowerShellPreflight.Validate("Write-Output @'literal");
        error.ShouldNotBeNull();
        error!.Message.ShouldContain("missing the terminator");
    }

    /// <summary>
    /// Rules AFTER a closed here-string must still apply - the region skip must not swallow the
    /// remainder of the script.
    /// </summary>
    [Fact]
    public void Validate_ErrorAfterClosedHereString_IsStillRefused()
    {
        var error = PowerShellPreflight.Validate("$s = @'\nbody\n'@\nGet-Process | | Sort-Object");
        error.ShouldNotBeNull();
        error!.Message.ShouldContain("empty pipe element");
    }

    // === Clause 4: table replay, expectation derived from the real parser. ===

    /// <summary>
    /// Preflight refusal must imply at least one real <c>ParseInput</c> error. The expectation is
    /// derived from the parser at test time rather than a hand-maintained verdict list - a
    /// hand-maintained list is precisely the drift #2757 and #2905 both removed.
    /// </summary>
    [Theory]
    [MemberData(nameof(HereStringCorpus))]
    public void Validate_Corpus_RefusesOnlyWhenRealParserReportsAnError(string script)
    {
        var parserErrors = PowerShellParserProbe.CountParseErrors(script);
        if (parserErrors is null)
        {
            // No PowerShell host on this runner; the deterministic facts above still run.
            return;
        }

        if (PowerShellPreflight.Validate(script) is not null)
        {
            parserErrors.Value.ShouldBeGreaterThan(
                0,
                $"Preflight refused a command the real PowerShell parser accepts: {script}");
        }
    }

    public static TheoryData<string> HereStringCorpus()
    {
        var data = new TheoryData<string>
        {
            ApostropheInSingleHereString,
            DoubleQuoteInSingleHereString,
            QuotesInExpandableHereString,
            MarkdownAppendIdiom,
            "$s = @'\nif (x) { unclosed\n|\n'@\nWrite-Output $s",
            "$note = @\"\n# Log\nit's $x \"quoted\"\n\"@\nAdd-Content -Path 'memory/x.md' -Value $note",
            "Write-Output @'literal'",
            "$s = \"abc",
            "Get-Item 'unterminated",
            "$s = @'\nbody text\nWrite-Output $s",
            "$s = @\"\nbody text\nWrite-Output $s",
            "$s = @'\nbody\n'@\nGet-Process | | Sort-Object",
        };

        return data;
    }
}
