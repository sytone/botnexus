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
    private readonly ExecTool _tool = new(workingDirectory: null, fileSystem: new MockFileSystem());

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
    public async Task ExecuteAsync_FileInvocation_IsNotInlineSyntaxPreflighted()
    {
        // -File takes a script path; the INLINE syntax rules (empty pipe element, malformed ${...},
        // unbalanced braces) are irrelevant to it and must never be applied. Original intent of this
        // test preserved: point at a REAL script whose content would trip every inline rule and
        // assert no inline-preflight rejection occurs.
        var script = Path.Combine(Path.GetTempPath(), "bn-2758-" + Guid.NewGuid().ToString("N") + ".ps1");
        await File.WriteAllTextAsync(script, "Get-Process | Sort-Ob |");
        try
        {
            var args = await _tool.PrepareArgumentsAsync(new Dictionary<string, object?>
            {
                ["command"] = new List<string> { "pwsh", "-NoProfile", "-File", script },
            });

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
        finally
        {
            File.Delete(script);
        }
    }

    [Fact]
    public async Task ExecuteAsync_MissingFileTarget_IsRejectedWithAPathDiagnosis()
    {
        // Issue #2758: pwsh reports a missing -File target as an argument-parsing error plus its
        // generic usage banner, which names neither the skill nor any candidate. The tool now
        // diagnoses it up front. This REPLACES the previous assertion that a missing -File target
        // produced no ArgumentException - that is the behaviour #2758 exists to reverse.
        var args = await _tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["command"] = new List<string> { "pwsh", "-NoProfile", "-File", "definitely-not-a-real-script.ps1" },
        });

        var ex = await Should.ThrowAsync<ArgumentException>(() => _tool.ExecuteAsync("file-call", args));

        ex.Message.ShouldContain("definitely-not-a-real-script.ps1");
        // Not under a skill scripts/ directory, so no candidate list is invented (AC5).
        ex.Message.Contains("Closest matches", StringComparison.Ordinal).ShouldBeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_MissingSkillWrapper_NamesSkillAndClosestMatches()
    {
        // AC1/AC2: candidates are enumerated from the skill's scripts/ directory at failure time, so
        // a wrapper added later appears automatically without a code change.
        var root = Path.Combine(Path.GetTempPath(), "bn-2758-" + Guid.NewGuid().ToString("N"));
        var scripts = Path.Combine(root, "skills", "teams", "scripts");
        Directory.CreateDirectory(scripts);
        foreach (var name in new[] { "ListChatMessages.ps1", "ListChannelMessages.ps1", "GetChatMessage.ps1" })
        {
            await File.WriteAllTextAsync(Path.Combine(scripts, name), "# wrapper");
        }

        try
        {
            var args = await _tool.PrepareArgumentsAsync(new Dictionary<string, object?>
            {
                ["command"] = new List<string>
                {
                    "pwsh", "-NoProfile", "-File", Path.Combine(scripts, "ListMessages.ps1"),
                },
            });

            var ex = await Should.ThrowAsync<ArgumentException>(() => _tool.ExecuteAsync("wrapper-call", args));

            ex.Message.ShouldContain("teams");
            ex.Message.ShouldContain("ListChatMessages.ps1");
            ex.Message.ShouldContain("ListChannelMessages.ps1");
            ex.Message.Contains("NOT executed", StringComparison.OrdinalIgnoreCase).ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
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
