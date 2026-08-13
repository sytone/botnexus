using BotNexus.Agent.Core.Tools;

namespace BotNexus.Agent.Core.Tests.Tools;

/// <summary>
/// Issue #2908. The shell tool already runs PowerShell, so wrapping a script in a second
/// <c>pwsh -Command "..."</c> makes the OUTER interpreter expand every <c>$name</c> and mangle
/// every <c>@{}</c> literal before the child process ever sees the text. The child then fails with
/// a downstream, actively misleading message (<c>The term '=@' is not recognized</c>). These tests
/// pin the preflight rejection for that shape and — just as importantly — pin the forms that must
/// keep working: single-quoted <c>-Command</c> arguments and the documented <c>-File</c> pattern.
/// </summary>
public sealed class PowerShellPreflightNestedInvocationTests
{
    [Theory]
    // AC1: the reported repro, both flag spellings, both interpolation triggers.
    [InlineData("pwsh -NoProfile -Command \"$h=@{A=1}; $h.Keys\"")]
    [InlineData("pwsh -NoProfile -c \"$j = Get-Content tmp/inbox.json -Raw | ConvertFrom-Json\"")]
    [InlineData("powershell -Command \"$b='http://localhost:8765'\"")]
    [InlineData("pwsh -NoProfile -Command \"@{Authorization='Bearer x'}\"")]
    // The nesting is still detected when it is not the first thing on the line.
    [InlineData("cd Q:\\repos\\ub; pwsh -NoProfile -Command \"$r = az repos pr show --id 1\"")]
    // A fully-qualified executable path must classify the same way.
    [InlineData("C:\\tools\\pwsh.exe -Command \"$x = 1\"")]
    public void DetectNestedInterpolation_DoubleQuotedCommandWithInterpolation_IsRejected(string command)
    {
        var error = PowerShellPreflight.DetectNestedPowerShellInterpolation(command);

        error.ShouldNotBeNull();
        // The message must name the real cause (outer interpolation), not the downstream symptom.
        error!.Message.Contains("outer", StringComparison.OrdinalIgnoreCase).ShouldBeTrue(error.Message);
        error.Message.Contains("single-quote", StringComparison.OrdinalIgnoreCase).ShouldBeTrue(error.Message);
        error.Message.Contains("already runs PowerShell", StringComparison.OrdinalIgnoreCase).ShouldBeTrue(error.Message);
    }

    [Theory]
    // AC2: single-quoted -Command arguments are the documented correct form and must pass.
    [InlineData("pwsh -NoProfile -Command '$h=@{A=1}; $h.Keys'")]
    [InlineData("pwsh -NoProfile -c '$h=@{A=1}'")]
    // AC3: the -File form is the documented correct pattern and must stay clear.
    [InlineData("pwsh -NoProfile -File tmp/x.ps1")]
    [InlineData("pwsh -NoProfile -File tmp/x.ps1 -Json '{\"a\":\"$b\"}'")]
    // A double-quoted -Command with nothing to interpolate is harmless.
    [InlineData("pwsh -NoProfile -Command \"Get-Date\"")]
    // No nested PowerShell at all.
    [InlineData("Get-ChildItem | Where-Object { $_.Name -like '*.cs' }")]
    [InlineData("$h = @{A=1}; $h.Keys")]
    // A mention of the shape inside a quoted string is text, not an invocation.
    [InlineData("Write-Output 'pwsh -NoProfile -Command \"$h=@{A=1}\"'")]
    public void DetectNestedInterpolation_SafeForms_AreNotRejected(string command)
    {
        PowerShellPreflight.DetectNestedPowerShellInterpolation(command).ShouldBeNull();
    }

    [Fact]
    public void ThrowIfInvalid_NestedDoubleQuotedCommand_Throws()
    {
        var ex = Should.Throw<ArgumentException>(
            () => PowerShellPreflight.ThrowIfInvalid("pwsh -NoProfile -Command \"$h=@{A=1}; $h.Keys\""));

        ex.Message.Contains("outer", StringComparison.OrdinalIgnoreCase).ShouldBeTrue(ex.Message);
        ex.Message.Contains("single-quote", StringComparison.OrdinalIgnoreCase).ShouldBeTrue(ex.Message);
    }

    [Fact]
    public void ThrowIfInvalid_NestedFileInvocation_DoesNotThrow()
    {
        // AC3 as an end-to-end assertion on the same entry point ShellTool uses.
        Should.NotThrow(() => PowerShellPreflight.ThrowIfInvalid("pwsh -NoProfile -File tmp/x.ps1"));
    }

    [Fact]
    public void ThrowIfInvalid_NestedSingleQuotedCommand_DoesNotThrow()
    {
        Should.NotThrow(() => PowerShellPreflight.ThrowIfInvalid("pwsh -NoProfile -Command '$h=@{A=1}; $h.Keys'"));
    }
}
