using BotNexus.Agent.Core.Types;
using System.IO.Abstractions.TestingHelpers;

namespace BotNexus.Extensions.ExecTool.Tests;

/// <summary>
/// Verifies the inline-pwsh preflight added for issue #2103 in the exec tool. When the command
/// invokes <c>pwsh</c>/<c>powershell</c> with an inline <c>-Command</c> script, a syntax error must
/// be rejected BEFORE any process is spawned, carrying the parser-style message and the file-based
/// remediation hint. Valid inline scripts and <c>-File</c> invocations pass through untouched.
/// </summary>
[Collection(ExecToolBackgroundRegistryCollection.Name)]
public class ExecToolPreflightTests : IDisposable
{
    private readonly ExecTool _tool = new(fileSystem: new MockFileSystem());

    public void Dispose() => ExecTool.ClearBackgroundProcesses();

    [Theory]
    [InlineData("Get-Process | Sort-Ob |", "An empty pipe element is not allowed.")]
    [InlineData("${var}:", "Unexpected token ':' in expression or statement.")]
    [InlineData("${var:}", "Variable reference is not valid. The variable name is missing.")]
    [InlineData("if ($true) { Write-Output 'hi' ", "Missing closing '}' in statement block or type definition.")]
    public async Task ExecuteAsync_InlinePwshSyntaxError_RejectedBeforeExecution(string script, string expectedMessage)
    {
        var args = await _tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["command"] = new List<string> { "pwsh", "-NoProfile", "-Command", script },
        });

        var ex = await Should.ThrowAsync<ArgumentException>(() => _tool.ExecuteAsync("preflight-call", args));

        ex.Message.ShouldContain(expectedMessage);
        ex.Message.ShouldContain("tmp/");
        ex.Message.ShouldContain("-File");
    }

    [Fact]
    public async Task ExecuteAsync_ValidInlinePwsh_ExecutesUnchanged()
    {
        var args = await _tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["command"] = new List<string> { "pwsh", "-NoProfile", "-Command", "Write-Output 'preflight-ok'" },
        });

        var result = await _tool.ExecuteAsync("valid-call", args);

        result.Content[0].Value.ShouldContain("preflight-ok");
    }

    [Fact]
    public async Task ExecuteAsync_FileInvocation_IsNotPreflighted()
    {
        // -File takes a script path; a would-be inline syntax error is irrelevant and must not be
        // preflighted. We only assert the preflight does not throw (the process itself will fail to
        // find the bogus path, which is fine - that is not a preflight rejection).
        var args = await _tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["command"] = new List<string> { "pwsh", "-NoProfile", "-File", "definitely-not-a-real-script.ps1" },
        });

        // Should NOT throw ArgumentException from preflight. Any process-start failure surfaces as a
        // Win32Exception / non-zero result, not an ArgumentException.
        try
        {
            var result = await _tool.ExecuteAsync("file-call", args);
            result.ShouldNotBeNull();
        }
        catch (Exception ex)
        {
            ex.ShouldNotBeOfType<ArgumentException>();
        }
    }

    [Fact]
    public async Task ExecuteAsync_NonPowerShellCommand_IsNotPreflighted()
    {
        // A non-pwsh command whose argument merely resembles a bad pwsh script must pass through.
        var args = await _tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["command"] = new List<string> { "cmd.exe", "/c", "echo Get-Process | Sort-Ob |" },
        });

        try
        {
            var result = await _tool.ExecuteAsync("nonpwsh-call", args);
            result.ShouldNotBeNull();
        }
        catch (Exception ex)
        {
            ex.ShouldNotBeOfType<ArgumentException>();
        }
    }
}
