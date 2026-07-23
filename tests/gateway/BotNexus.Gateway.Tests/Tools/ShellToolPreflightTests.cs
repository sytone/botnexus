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
}
