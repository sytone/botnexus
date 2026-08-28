using BotNexus.Agent.Core.Types;
using System.IO.Abstractions.TestingHelpers;

namespace BotNexus.Extensions.ExecTool.Tests;

/// <summary>
/// Issue #3576, acceptance clause 2: the <c>exec</c> array route. 73 of the 85 avoidable weekly
/// failures arrived as <c>["pwsh","-NoProfile","-Command","&lt;unparseable&gt;"]</c>, so the array form
/// must reach the same parser gate the <c>shell</c> string form does - and must still let a clean
/// command through untouched (clause 6) and never preflight a <c>-File</c> script (clause 5).
/// </summary>
[Collection(ExecToolBackgroundRegistryCollection.Name)]
public class ExecToolParserGateTests : IDisposable
{
    private readonly ExecTool _tool = new(workingDirectory: null, fileSystem: new MockFileSystem());

    public void Dispose() => ExecTool.ClearBackgroundProcesses();

    [Theory]
    [InlineData("foreach($i in 1,2){ $i } | Sort-Object", "An empty pipe element is not allowed.")]
    [InlineData("Get-Item (Join-Path a b", "Missing closing ')' in expression.")]
    [InlineData("foreach ($x 1,2) { $x }", "Missing 'in' after variable in foreach loop.")]
    [InlineData("$a[]", "Array index expression is missing or not valid.")]
    public async Task ExecuteAsync_UnparseableInlineCommand_RefusedBeforeLaunch(
        string script,
        string expectedFragment)
    {
        var args = await _tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["command"] = new List<string> { "pwsh", "-NoProfile", "-Command", script },
        });

        var ex = await Should.ThrowAsync<ArgumentException>(
            () => _tool.ExecuteAsync("parser-gate-call", args));

        ex.Message.ShouldContain(expectedFragment);
    }

    [Fact]
    public async Task ExecuteAsync_ForeachPipedFrom_NamesTheSubexpressionCorrection()
    {
        var args = await _tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["command"] = new List<string>
            {
                "pwsh", "-NoProfile", "-Command", "foreach($i in 1,2){ $i } | ConvertTo-Json",
            },
        });

        var ex = await Should.ThrowAsync<ArgumentException>(
            () => _tool.ExecuteAsync("foreach-pipe-call", args));

        ex.Message.ShouldContain("$(foreach");
    }

    [Fact]
    public async Task ExecuteAsync_CorrectedIdiom_ExecutesNormally()
    {
        // Clause 3 + clause 6: the remediation the platform prints must itself run.
        var args = await _tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["command"] = new List<string>
            {
                "pwsh", "-NoProfile", "-Command", "$(foreach($i in 1,2){ $i }) | Sort-Object -Descending",
            },
        });

        var result = await _tool.ExecuteAsync("corrected-call", args);

        result.Content[0].Value.ShouldContain("2");
    }
}
