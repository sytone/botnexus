using BotNexus.Tools;

namespace BotNexus.Gateway.Tests.Tools;

/// <summary>Checks real launch, executable lookup and temp writes rather than only dictionary contents.</summary>
public sealed class LocalChildEnvironmentLaunchTests
{
    [Fact]
    public async Task Shell_DefaultBoundaryKeepsPathAndTemporaryDirectoryUsable()
    {
        // The command uses a PATH-resolved runtime and its own OS temp-directory API.
        var marker = "BN4749_TEMP_" + Guid.NewGuid().ToString("N");
        var command = "$p = [IO.Path]::Combine([IO.Path]::GetTempPath(), '" + marker
            + "'); try { [IO.File]::WriteAllText($p, 'ok'); [IO.File]::ReadAllText($p) } finally { [IO.File]::Delete($p) }";
        var tool = new ShellTool(shellPreference: ShellPreference.Pwsh);
        var args = await tool.PrepareArgumentsAsync(new Dictionary<string, object?> { ["command"] = command });
        var result = await tool.ExecuteAsync("temp-probe", args);
        result.Details.ShouldBeOfType<ShellTool.ShellToolDetails>().ExitCode.ShouldBe(0);
        string.Join("\n", result.Content.Select(c => c.Value)).Trim().ShouldBe("ok");
    }
}
