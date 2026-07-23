using BotNexus.Agent.Core.Tools;

namespace BotNexus.AgentCore.Tests.Tools;

/// <summary>
/// Unit tests for <see cref="PowerShellPreflight"/>. These assert the exact syntax-error signatures
/// the real PowerShell parser produces (verified locally against
/// <c>System.Management.Automation.Language.Parser.ParseInput</c>) are reproduced by the lightweight
/// scanner, and - critically - that valid one-liners pass through untouched.
/// </summary>
public class PowerShellPreflightTests
{
    // === Happy path: valid scripts must NOT be rejected ===

    [Theory]
    [InlineData("Get-Process | Sort-Object CPU | Select-Object -First 5")]
    [InlineData("Get-ChildItem | Where-Object { $_.Length -gt 0 }")]
    [InlineData("Write-Output \"${env:PATH}\"")]
    [InlineData("Write-Output \"${var}:\"")]
    [InlineData("$a = @{ Name = \"x\" }; $a")]
    [InlineData("Write-Output \"a | b\"")]
    [InlineData("if ($true) { Write-Output 'hi' }")]
    [InlineData("foreach ($x in 1..3) { if ($x) { $x } }")]
    [InlineData("Get-Item 'C:\\path with | pipe and } brace'")]
    [InlineData("Get-Process; Get-Service")]
    [InlineData("Get-Process -Name pwsh")]
    public void Validate_ValidScript_ReturnsNull(string script)
    {
        PowerShellPreflight.Validate(script).ShouldBeNull();
    }

    [Fact]
    public void ThrowIfInvalid_ValidScript_DoesNotThrow()
    {
        // A simple valid one-liner must pass through unchanged (no exception).
        Should.NotThrow(() =>
            PowerShellPreflight.ThrowIfInvalid("Get-Process | Sort-Object CPU | Select-Object -First 5"));
    }

    // === Sad path: empty pipe element ===

    [Theory]
    [InlineData("Get-Process | Sort-Ob |")]
    [InlineData("Get-Process |")]
    [InlineData("Get-Process | | Sort-Object")]
    [InlineData("| leading")]
    public void Validate_EmptyPipeElement_IsRejected(string script)
    {
        var error = PowerShellPreflight.Validate(script);
        error.ShouldNotBeNull();
        error!.Message.ShouldBe("An empty pipe element is not allowed.");
    }

    // === Sad path: malformed ${var}: variable reference ===

    [Fact]
    public void Validate_BareBraceVariableFollowedByColon_IsRejected()
    {
        var error = PowerShellPreflight.Validate("${var}:");
        error.ShouldNotBeNull();
        error!.Message.ShouldBe("Unexpected token ':' in expression or statement.");
    }

    [Theory]
    [InlineData("${var:}")]
    [InlineData("${:}")]
    [InlineData("${}")]
    [InlineData("Write-Output \"${var:}\"")]
    public void Validate_MissingVariableName_IsRejected(string script)
    {
        var error = PowerShellPreflight.Validate(script);
        error.ShouldNotBeNull();
        error!.Message.ShouldBe("Variable reference is not valid. The variable name is missing.");
    }

    // === Sad path: unbalanced / nested braces ===

    [Theory]
    [InlineData("if ($true) { Write-Output 'hi' ")]
    [InlineData("foreach ($x in 1..3) { if ($x) { $x }")]
    [InlineData("$a = @{ Name = 'x'")]
    public void Validate_MissingClosingBrace_IsRejected(string script)
    {
        var error = PowerShellPreflight.Validate(script);
        error.ShouldNotBeNull();
        error!.Message.ShouldBe("Missing closing '}' in statement block or type definition.");
    }

    [Theory]
    [InlineData("} extra")]
    [InlineData("Write-Output 'x' }")]
    public void Validate_UnexpectedClosingBrace_IsRejected(string script)
    {
        var error = PowerShellPreflight.Validate(script);
        error.ShouldNotBeNull();
        error!.Message.ShouldBe("Unexpected token '}' in expression or statement.");
    }

    // === Rejection message content ===

    [Fact]
    public void ThrowIfInvalid_Rejected_ThrowsWithRemediationHint()
    {
        var ex = Should.Throw<ArgumentException>(() =>
            PowerShellPreflight.ThrowIfInvalid("Get-Process | Sort-Ob |"));

        ex.Message.ShouldContain("An empty pipe element is not allowed.");
        ex.Message.ShouldContain("tmp/");
        ex.Message.ShouldContain("-File");
        ex.Message.ShouldContain("offset");
    }

    // === Executable / inline-script detection ===

    [Theory]
    [InlineData("pwsh", true)]
    [InlineData("powershell", true)]
    [InlineData("pwsh.exe", true)]
    [InlineData("powershell.exe", true)]
    [InlineData("PWSH", true)]
    [InlineData(@"C:\Program Files\PowerShell\7\pwsh.exe", true)]
    [InlineData("/usr/bin/pwsh", true)]
    [InlineData("bash", false)]
    [InlineData("python", false)]
    [InlineData("cmd.exe", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsPowerShellExecutable_ClassifiesCorrectly(string? exe, bool expected)
    {
        PowerShellPreflight.IsPowerShellExecutable(exe).ShouldBe(expected);
    }

    [Fact]
    public void TryGetInlineScript_ShellToolStyle_ReturnsTrailingScript()
    {
        // ShellTool: base args carry -Command, script appended separately as inlineScript.
        var baseArgs = new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command" };
        var found = PowerShellPreflight.TryGetInlineScript(baseArgs, "Get-Process | Sort-Ob |", out var script);

        found.ShouldBeTrue();
        script.ShouldBe("Get-Process | Sort-Ob |");
    }

    [Fact]
    public void TryGetInlineScript_ExecToolStyle_ReturnsNextArgAfterCommand()
    {
        // ExecTool: everything in one array; script is the element after -Command.
        var args = new[] { "-NoProfile", "-Command", "Get-Process |" };
        var found = PowerShellPreflight.TryGetInlineScript(args, inlineScript: null, out var script);

        found.ShouldBeTrue();
        script.ShouldBe("Get-Process |");
    }

    [Fact]
    public void TryGetInlineScript_FileInvocation_IsNotPreflighted()
    {
        // -File means a script path, not inline text - must be skipped entirely.
        var args = new[] { "-NoProfile", "-File", "tmp/script.ps1" };
        PowerShellPreflight.TryGetInlineScript(args, inlineScript: null, out _).ShouldBeFalse();

        var shellArgs = new[] { "-NoProfile", "-File" };
        PowerShellPreflight.TryGetInlineScript(shellArgs, "tmp/script.ps1", out _).ShouldBeFalse();
    }

    [Fact]
    public void TryGetInlineScript_NoCommandFlag_ReturnsFalse()
    {
        var args = new[] { "-NoProfile", "-Version" };
        PowerShellPreflight.TryGetInlineScript(args, inlineScript: null, out _).ShouldBeFalse();
    }
}
