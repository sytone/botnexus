using BotNexus.Agent.Core.Tools;

namespace BotNexus.AgentCore.Tests.Tools;

/// <summary>
/// Tests for how <see cref="PowerShellPreflight"/> treats stacked quoting.
/// </summary>
/// <remarks>
/// <para>
/// <b>History.</b> Issue #2417 added a <c>Nested quoting detected</c> heuristic here and this class
/// originally pinned it as a REJECTION: any single-quoted value passed to
/// <c>-c</c>/<c>-Command</c>/<c>--jq</c>/<c>-Json</c> that contained an unescaped <c>"</c> or
/// <c>$</c> was refused before execution.
/// </para>
/// <para>
/// <b>Why those assertions were inverted, not deleted (issue #2757).</b> The premise was wrong.
/// Inside a SINGLE-quoted PowerShell string both <c>"</c> and <c>$</c> are literal by language
/// definition - there is no interpolation and no early termination - so no outer layer can consume
/// them. A replay of every distinct command the rule rejected over a 7-day window found 20 of 20
/// parsed with ZERO errors under
/// <c>[System.Management.Automation.Language.Parser]::ParseInput</c>, and the rule was refusing the
/// platform's own documented skill-wrapper convention
/// (<c>-Json '{"name":"value"}'</c>). The exact inputs #2417 pinned are therefore kept here with
/// their expectation flipped to "must execute", so the corpus is preserved and the behaviour change
/// is explicit and reviewable rather than silently dropped.
/// </para>
/// <para>
/// The parser-backed rules this class also covers - the empty-pipe-element annotation below - were
/// measured true-positive and are UNCHANGED.
/// </para>
/// </remarks>
public class PowerShellNestedQuoteTests
{
    // === Conservative: legitimate one-liners must still pass ===

    [Theory]
    [InlineData("Get-Process | Select-Object -First 5")]
    [InlineData("pwsh -NoProfile -File tmp/script.ps1")]
    [InlineData("jq -File tmp/filter.jq data.json")]
    [InlineData("jq --jq '.items[] | .name' data.json")]
    [InlineData("gh pr list --json number,title")]
    [InlineData("Get-Content x.json | ConvertFrom-Json")]
    [InlineData("pwsh -Command 'Get-Process'")]
    [InlineData("Write-Output \"total is $total\"")]
    [InlineData("docker run -c 'echo hello'")]
    public void Validate_LegitimateOneLiner_ReturnsNull(string script)
    {
        PowerShellPreflight.Validate(script).ShouldBeNull();
    }

    // === Stacked quoting inside a -c/-Command/--jq/-Json argument value ===
    // These are the EXACT inputs #2417 pinned as rejections. Each one parses cleanly under the real
    // PowerShell parser, so under #2757 each must now be allowed through. Re-enabling the
    // unconditional heuristic reddens this test by name.

    [Theory]
    [InlineData("pwsh -Command 'Write-Output \"$x\"'")]
    [InlineData("bash -c 'echo \"$HOME\"'")]
    [InlineData("jq --jq '.a | \"$b\"' file.json")]
    [InlineData("Invoke-Thing -Json '{\"a\": \"$v\"}'")]
    public void Validate_StackedQuotingInArgumentValue_IsAllowed_BecauseSingleQuotesAreLiteral(string script)
    {
        PowerShellPreflight.Validate(script).ShouldBeNull();
    }

    [Fact]
    public void ThrowIfInvalid_StackedQuoting_DoesNotRefuseBeforeExecution()
    {
        // #2417 asserted a throw here naming "Nested quoting detected" and the tmp/*.ps1 remedy.
        // That remedy cost an extra write + call per invocation for a command that never needed it,
        // so the refusal is gone; a soft advisory, if ever wanted, belongs alongside the executed
        // result, never as a pre-execution refusal.
        Should.NotThrow(() =>
            PowerShellPreflight.ThrowIfInvalid("pwsh -Command 'Write-Output \"$x\"'"));
    }

    // === Derived symptom annotation: empty pipe element caused by a consumed $variable ===

    [Fact]
    public void Validate_EmptyPipeElementWithEmptyAssignmentOperand_AnnotatesConsumedVariable()
    {
        // The classic shape: an outer quoting layer ate "$items", leaving "$x =  | Measure-Object".
        var error = PowerShellPreflight.Validate("$x =  | Measure-Object");

        error.ShouldNotBeNull();
        error!.Message.ShouldStartWith("An empty pipe element is not allowed.");
        error.Message.ShouldContain("$variable");
        error.Message.ShouldContain("consumed by an outer quoting layer");
    }

    [Fact]
    public void Validate_EmptyPipeElementWithoutLostVariable_IsNotAnnotated()
    {
        // A plain trailing pipe has no empty operand - it must keep the bare parser message.
        var error = PowerShellPreflight.Validate("Get-Process | Sort-Ob |");

        error.ShouldNotBeNull();
        error!.Message.ShouldBe("An empty pipe element is not allowed.");
    }
}
