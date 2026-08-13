using BotNexus.Tools;

namespace BotNexus.Gateway.Tests.Tools;

/// <summary>
/// Verifies the inline-pwsh preflight added for issue #2103. When ShellTool is configured to use
/// PowerShell, an inline <c>-Command</c> script with a syntax error must be rejected BEFORE any
/// process is spawned, with the parser-style message and the file-based remediation hint. Valid
/// one-liners must still execute unchanged.
/// </summary>
public sealed class ShellToolPreflightTests
{
    private static ShellTool PwshTool() => new(shellPreference: ShellPreference.Pwsh);

    [Theory]
    [InlineData("Get-Process | Sort-Ob |", "An empty pipe element is not allowed.")]
    [InlineData("${var}:", "Unexpected token ':' in expression or statement.")]
    [InlineData("${var:}", "Variable reference is not valid. The variable name is missing.")]
    [InlineData("if ($true) { Write-Output 'hi' ", "Missing closing '}' in statement block or type definition.")]
    public async Task ExecuteAsync_InlinePwshSyntaxError_RejectedBeforeExecution(string command, string expectedMessage)
    {
        var tool = PwshTool();

        var ex = await Should.ThrowAsync<ArgumentException>(() => tool.ExecuteAsync(
            "preflight-call",
            new Dictionary<string, object?> { ["command"] = command }));

        ex.Message.ShouldContain(expectedMessage);
        // Remediation hint steers toward the file-based invocation.
        ex.Message.ShouldContain("tmp/");
        ex.Message.ShouldContain("-File");
    }

    [Fact]
    public async Task ExecuteAsync_ValidInlineOneLiner_ExecutesUnchanged()
    {
        var tool = PwshTool();

        var result = await tool.ExecuteAsync(
            "valid-call",
            new Dictionary<string, object?> { ["command"] = "Write-Output 'preflight-ok'" });

        result.Content[0].Value.ShouldContain("preflight-ok");
        result.Details.ShouldBeOfType<ShellTool.ShellToolDetails>().IsError.ShouldBeFalse();
    }

    /// <summary>
    /// Issue #2908, acceptance criterion 1: a nested double-quoted <c>pwsh -Command</c> is refused
    /// at the tool boundary, and the rejection names outer interpolation rather than the downstream
    /// <c>The term '=@' is not recognized</c> symptom the child process would have produced.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_NestedDoubleQuotedPwshCommand_RejectedBeforeExecution()
    {
        var tool = PwshTool();

        var ex = await Should.ThrowAsync<ArgumentException>(() => tool.ExecuteAsync(
            "nested-call",
            new Dictionary<string, object?> { ["command"] = "pwsh -NoProfile -Command \"$h=@{A=1}; $h.Keys\"" }));

        ex.Message.Contains("outer", StringComparison.OrdinalIgnoreCase).ShouldBeTrue(ex.Message);
        ex.Message.Contains("single-quote", StringComparison.OrdinalIgnoreCase).ShouldBeTrue(ex.Message);
        ex.Message.Contains("already runs PowerShell", StringComparison.OrdinalIgnoreCase).ShouldBeTrue(ex.Message);
    }

    /// <summary>
    /// Issue #2908, acceptance criterion 2: a single-quoted <c>-Command</c> argument is literal in the
    /// outer interpreter, so it must pass the preflight and actually run.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_NestedSingleQuotedPwshCommand_Executes()
    {
        var tool = PwshTool();

        var result = await tool.ExecuteAsync(
            "nested-single",
            new Dictionary<string, object?> { ["command"] = "pwsh -NoProfile -Command '$h=@{A=1;B=2}; $h.Keys | Sort-Object'" });

        result.Content[0].Value.ShouldContain("A");
        result.Details.ShouldBeOfType<ShellTool.ShellToolDetails>().IsError.ShouldBeFalse();
    }

    /// <summary>
    /// Issue #2908, acceptance criterion 3 - the trap. <c>-File</c> is the documented correct pattern
    /// and must never be caught by the nesting rule, even though the command text names <c>pwsh</c>.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_NestedPwshFileInvocation_IsNotRejected()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"preflight-2908-{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(scriptPath, "Write-Output 'file-form-ok'");

        try
        {
            var tool = PwshTool();

            var result = await tool.ExecuteAsync(
                "nested-file",
                new Dictionary<string, object?> { ["command"] = $"pwsh -NoProfile -File '{scriptPath}'" });

            result.Content[0].Value.ShouldContain("file-form-ok");
            result.Details.ShouldBeOfType<ShellTool.ShellToolDetails>().IsError.ShouldBeFalse();
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    /// <summary>
    /// Issue #2908, acceptance criterion 4: the rule must be visible at the point of use, in the tool
    /// description the model reads at call time - not only in WORLD.md.
    /// </summary>
    [Fact]
    public void Definition_Description_StatesShellAlreadyRunsPowerShell()
    {
        var description = PwshTool().Definition.Description;

        description.Contains("ALREADY RUNS PowerShell", StringComparison.OrdinalIgnoreCase).ShouldBeTrue(description);
        description.Contains("pwsh -Command", StringComparison.OrdinalIgnoreCase).ShouldBeTrue(description);
        description.Contains("single-quote", StringComparison.OrdinalIgnoreCase).ShouldBeTrue(description);
    }
}
