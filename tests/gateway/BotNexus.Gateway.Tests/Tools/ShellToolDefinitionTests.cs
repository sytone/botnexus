using BotNexus.Tools;

namespace BotNexus.Gateway.Tests.Tools;

public sealed class ShellToolDefinitionTests
{
    [Fact]
    public void Definition_PowerShellGuidanceNamesPidCollisionAndReplacement()
    {
        var tool = new ShellTool(shellPreference: ShellPreference.Pwsh);
        var description = tool.Definition.Description;

        description.ShouldContain("$PID");
        description.ShouldContain("read-only automatic variable");
        description.ShouldContain("processId");
    }
}
