using BotNexus.Agent.Core.Tools;

namespace BotNexus.AgentCore.Tests.Tools;

/// <summary>
/// Tests for the nested-quote heuristic added to <see cref="PowerShellPreflight"/> for issue #2417.
/// The heuristic names the actual mistake (a quoted argument value that itself carries unescaped
/// <c>"</c> or <c>$</c>) instead of surfacing the derived <c>An empty pipe element is not allowed.</c>
/// symptom the real parser emits once an outer quoting layer has eaten a <c>$variable</c>.
/// </summary>
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

    // === Nested quoting inside a -c/-Command/--jq/-Json argument value ===

    [Theory]
    [InlineData("pwsh -Command 'Write-Output \"$x\"'")]
    [InlineData("bash -c 'echo \"$HOME\"'")]
    [InlineData("jq --jq '.a | \"$b\"' file.json")]
    [InlineData("Invoke-Thing -Json '{\"a\": \"$v\"}'")]
    public void Validate_NestedQuotingInArgumentValue_IsRejected(string script)
    {
        var error = PowerShellPreflight.Validate(script);
        error.ShouldNotBeNull();
        error!.Message.ShouldContain("Nested quoting detected");
    }

    [Fact]
    public void ThrowIfInvalid_NestedQuoting_NamesTheMistakeAndTheFileRemedy()
    {
        var ex = Should.Throw<ArgumentException>(() =>
            PowerShellPreflight.ThrowIfInvalid("pwsh -Command 'Write-Output \"$x\"'"));

        ex.Message.ShouldContain("Nested quoting detected");
        ex.Message.ShouldContain("tmp/");
        ex.Message.ShouldContain("-File");
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
