using BotNexus.Tools;

namespace BotNexus.Gateway.Tests.Tools;

/// <summary>
/// Issue #3576, acceptance clause 1: a <c>shell</c> string command that the real PowerShell parser
/// rejects must be refused before a process is launched, and for the 47-occurrence
/// <c>foreach(...){...} | cmd</c> shape the rejection must name the <c>$(...)</c> correction.
/// Clause 6 is guarded here too: a clean-parsing command must still execute unchanged.
/// </summary>
public sealed class ShellToolParserGateTests
{
    private static ShellTool PwshTool() => new(shellPreference: ShellPreference.Pwsh);

    [Fact]
    public async Task ExecuteAsync_ForeachPipedFrom_RefusedAndNamesCorrection()
    {
        var tool = PwshTool();

        var ex = await Should.ThrowAsync<ArgumentException>(() => tool.ExecuteAsync(
            "parser-gate-call",
            new Dictionary<string, object?> { ["command"] = "foreach($i in 1,2){ $i } | Sort-Object" }));

        ex.Message.ShouldContain("An empty pipe element is not allowed.");
        ex.Message.ShouldContain("$(foreach");
    }

    [Theory]
    [InlineData("Get-Item (Join-Path a b", "Missing closing ')' in expression.")]
    [InlineData("foreach ($x 1,2) { $x }", "Missing 'in' after variable in foreach loop.")]
    [InlineData("$a[]", "Array index expression is missing or not valid.")]
    public async Task ExecuteAsync_UnparseableCommand_RefusedBeforeLaunch(string command, string fragment)
    {
        var tool = PwshTool();

        var ex = await Should.ThrowAsync<ArgumentException>(() => tool.ExecuteAsync(
            "parser-gate-call",
            new Dictionary<string, object?> { ["command"] = command }));

        ex.Message.ShouldContain(fragment);
    }

    [Fact]
    public async Task ExecuteAsync_CorrectedIdiom_ExecutesUnchanged()
    {
        var tool = PwshTool();

        var result = await tool.ExecuteAsync(
            "corrected-call",
            new Dictionary<string, object?>
            {
                ["command"] = "$(foreach($i in 1,2){ \"n$i\" }) | Sort-Object -Descending",
            });

        result.Content[0].Value.ShouldContain("n2");
        result.Details.ShouldBeOfType<ShellTool.ShellToolDetails>().IsError.ShouldBeFalse();
    }
}
