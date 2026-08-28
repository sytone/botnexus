using BotNexus.Agent.Core.Tools;

namespace BotNexus.AgentCore.Tests.Tools;

/// <summary>
/// Regression tests for issue #3576 - the preflight <b>admitting</b> genuinely un-parseable inline
/// PowerShell. A 7-day forensic replay of every command that produced a runtime <c>ParserError</c>
/// found 102 commands, of which <b>85 fail to parse</b> under the real
/// <c>System.Management.Automation.Language.Parser.ParseInput</c> - i.e. all 85 were deterministically
/// catchable before a process was ever launched, and the hand-rolled scanner let every one through.
/// </summary>
/// <remarks>
/// <para>
/// This is the complementary half of #3566: that issue is the preflight refusing <i>valid</i> input,
/// this one is the preflight admitting <i>invalid</i> input. Both had the same cause - string
/// heuristics standing in for a real parse - so the fix is to run the authoritative parser and refuse
/// only on a genuine <c>ParseError</c>.
/// </para>
/// <para>
/// The two non-vacuity guards in this file are deliberate and must never be weakened:
/// <see cref="ScriptFileSyntaxError_IsNotRefused"/> (clause 5 - the error belongs to an invoked
/// <c>-File</c> script, not the inline text) and <see cref="CleanParsingCommand_IsNeverRefused"/>
/// (clause 6 - the anti-false-positive fence that #2757/#2905/#3566 exist to protect). Without them a
/// "refuse everything" implementation would pass.
/// </para>
/// </remarks>
public class PowerShellPreflightParserGateTests
{
    // ---------------------------------------------------------------------------------------------
    // Clause 1 - the 47-occurrence idiom: a statement cannot be a pipeline source, and the rejection
    // must NAME the $(...) correction rather than leaving the agent with the bare parser message.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ForeachPipedFrom_IsRefused_AndNamesTheSubexpressionCorrection()
    {
        var error = PowerShellPreflight.Validate("foreach($i in 1,2){ $i } | Sort-Object");

        error.ShouldNotBeNull();
        error!.Message.ShouldContain("An empty pipe element is not allowed.");
        error.Remediation.ShouldNotBeNull();
        error.Remediation!.ShouldContain("$(");
        error.Remediation.ShouldContain("statement");
    }

    [Theory]
    // Every shape in the corpus is "<statement> } | <cmd>" at the tail of a longer one-liner.
    [InlineData("foreach($n in 'a','b'){ $n } | Out-File tmp/x.txt -Encoding utf8")]
    [InlineData("foreach($u in @('a','b')){ try{ $u }catch{ 'e' } } | ConvertTo-Json")]
    [InlineData("if ($true) { 1 } | Out-Null")]
    [InlineData("switch (1) { 1 { 'a' } } | Out-Null")]
    [InlineData("while ($false) { 1 } | Out-Null")]
    public void StatementPipedFrom_IsRefused_WithNamedRemediation(string script)
    {
        var error = PowerShellPreflight.Validate(script);

        error.ShouldNotBeNull();
        error!.Remediation.ShouldNotBeNull();
        error.Remediation!.ShouldContain("$(");
    }

    [Fact]
    public void ThrowIfInvalid_StatementPipedFrom_MessageCarriesTheCorrectedIdiom()
    {
        var ex = Should.Throw<ArgumentException>(() =>
            PowerShellPreflight.ThrowIfInvalid("foreach($i in 1,2){ $i } | Sort-Object"));

        ex.Message.ShouldContain("An empty pipe element is not allowed.");
        ex.Message.ShouldContain("$(foreach");
        // The generic "write a tmp/*.ps1 file" hint is WRONG for this shape - moving invalid syntax
        // into a file does not make it valid - so the named remediation must replace it, not append.
        ex.Message.ShouldNotContain(PowerShellPreflight.RemediationHint);
    }

    // ---------------------------------------------------------------------------------------------
    // Clause 3 - the corrected idiom must execute normally (it parses clean, so it must not be
    // refused). Paired with clause 1 this proves the remediation the platform prints actually works.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void CorrectedSubexpressionIdiom_IsNotRefused()
    {
        PowerShellPreflight.Validate("$(foreach($i in 1,2){ $i }) | Sort-Object -Descending")
            .ShouldBeNull();
    }

    // ---------------------------------------------------------------------------------------------
    // Clause 4 - every parser message in the issue's Evidence table must be detected when the text is
    // supplied inline. Counts are the 7-day corpus frequencies.
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("foreach($i in 1,2){ $i } | Sort-Object", "An empty pipe element is not allowed.")]                 // 47
    [InlineData("Get-Item (Join-Path a b", "Missing closing ')' in expression.")]                                   // 20
    [InlineData("$s = @'x\nbody\n'@", "No characters are allowed after a here-string header")]                      // 5
    [InlineData("Write-Output \"`u{ZZZZ}\"", "The Unicode escape sequence is not valid.")]                          // 2
    [InlineData("foreach ($x 1,2) { $x }", "Missing 'in' after variable in foreach loop.")]                         // 2
    [InlineData("Get-Process > ", "Missing file specification after redirection operator.")]                        // 1
    [InlineData("$a[]", "Array index expression is missing or not valid.")]                                         // 1
    [InlineData("$(Get-Item", "Missing closing ')' in subexpression.")]                                             // 1
    public void EvidenceTableParserMessages_AreDetectedInline(string script, string expectedFragment)
    {
        var error = PowerShellPreflight.Validate(script);

        error.ShouldNotBeNull($"'{script}' does not parse, so the preflight must refuse it");
        error!.Message.ShouldContain(expectedFragment);
    }

    // ---------------------------------------------------------------------------------------------
    // Clause 2 - the exec-array route. 73 of the 85 misses arrived as
    // ["pwsh","-NoProfile","-Command","<unparseable>"], so the array form must reach the same gate.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ExecArrayStyleInlineScript_ReachesTheParserGate()
    {
        var args = new[] { "-NoProfile", "-Command", "foreach($i in 1,2){ $i } | Sort-Object" };

        PowerShellPreflight.TryGetInlineScript(args, inlineScript: null, out var script).ShouldBeTrue();
        PowerShellPreflight.Validate(script).ShouldNotBeNull();
    }

    [Fact]
    public void ShellStringStyleInlineScript_ReachesTheParserGate()
    {
        var baseArgs = new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command" };

        PowerShellPreflight
            .TryGetInlineScript(baseArgs, "Get-Item (Join-Path a b", out var script)
            .ShouldBeTrue();
        PowerShellPreflight.Validate(script).ShouldNotBeNull();
    }

    // ---------------------------------------------------------------------------------------------
    // Clause 5 - NON-VACUITY GUARD. The 17 corpus commands that parsed clean are not a contradiction:
    // their ParserError came from a script FILE the command invoked, not from the inline text. Those
    // must pass. Do not weaken this test - it is what stops "refuse everything" from going green.
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("pwsh -NoProfile -File tmp/broken.ps1")]
    [InlineData("pwsh -NoProfile -File scripts/tool.ps1 -Json '{\"name\":\"value\"}'")]
    [InlineData("& './scripts/broken.ps1'")]
    [InlineData(". ./profile.ps1; Get-Process")]
    public void ScriptFileSyntaxError_IsNotRefused(string script)
    {
        // The inline text itself parses cleanly; whatever the invoked file contains is the file's
        // problem and is discovered at runtime, not by a preflight that cannot see it.
        PowerShellPreflight.Validate(script).ShouldBeNull();
    }

    [Fact]
    public void FileFlagInvocation_IsNotPreflightedAtAll()
    {
        // Belt and braces for clause 5 via the other seam: -File short-circuits before any parse.
        var args = new[] { "-NoProfile", "-File", "tmp/has-a-syntax-error.ps1" };

        PowerShellPreflight.TryGetInlineScript(args, inlineScript: null, out _).ShouldBeFalse();
    }

    // ---------------------------------------------------------------------------------------------
    // Clause 6 - NON-VACUITY GUARD / anti-false-positive fence. Nothing that parses without error may
    // ever be refused. This is the class of defect #2757, #2905 and #3566 were all filed for, and it
    // is the reason the gate refuses ONLY on a genuine ParseError and fails open on everything else.
    // ---------------------------------------------------------------------------------------------

    [Theory]
    // The #2757 shape: single-quoted JSON payload to a skill wrapper.
    [InlineData("pwsh -NoProfile -File scripts/send.ps1 -Json '{\"name\":\"value\"}'")]
    // The #2905 shape: here-string append, the platform's durable-write idiom.
    [InlineData("$s = @'\nit's \"quoted\" | piped\n'@\nAdd-Content -Path 'log.md' -Value $s")]
    // Ordinary pipelines, hashtables, subexpressions, escapes, backtick-n, ranges.
    [InlineData("Get-Process | Sort-Object CPU | Select-Object -First 5")]
    [InlineData("Get-ChildItem | Where-Object { $_.Length -gt 0 } | Measure-Object -Sum Length")]
    [InlineData("$h = @{}; foreach ($k in 'a','b') { $h[$k] = 1 }; $h | ConvertTo-Json")]
    [InlineData("$(foreach($i in 1,2){ $i }) | Sort-Object")]
    [InlineData("Write-Output \"a`nb\"")]
    [InlineData("Write-Output \"${env:PATH}\"")]
    [InlineData("Get-Item 'C:\\path with | pipe and } brace'")]
    [InlineData("gh issue list --limit 500 --json number,title | ConvertFrom-Json")]
    [InlineData("Get-Content x.txt | Select-String -Pattern 'a|b' | Select-Object -First 3")]
    [InlineData("1..5 | ForEach-Object { $_ * 2 }")]
    [InlineData("try { Get-Item x } catch { $_.Exception.Message } finally { 'done' }")]
    [InlineData("$env:X = 'y'; & 'C:\\Program Files\\PowerShell\\7\\pwsh.exe' -NoProfile -File a.ps1")]
    [InlineData("Get-Process -Name pwsh 2> err.txt")]
    [InlineData("if ($true) { 'a' } else { 'b' }")]
    [InlineData("$x = $(Get-Date).Year; \"$x\"")]
    public void CleanParsingCommand_IsNeverRefused(string script)
    {
        PowerShellPreflight.Validate(script).ShouldBeNull(
            $"'{script}' parses without error, so refusing it would be a #3566-class false positive");
    }

    [Fact]
    public void EmptyAndWhitespaceScripts_FailOpen()
    {
        // Fail open on anything that is not a definite ParseError.
        PowerShellPreflight.Validate(null).ShouldBeNull();
        PowerShellPreflight.Validate(string.Empty).ShouldBeNull();
        PowerShellPreflight.Validate("   ").ShouldBeNull();
    }
}
