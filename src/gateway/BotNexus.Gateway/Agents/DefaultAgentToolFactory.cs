using BotNexus.AgentCore.Tools;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Tools;

namespace BotNexus.Gateway.Agents;

public sealed class DefaultAgentToolFactory : IAgentToolFactory
{
    public IReadOnlyList<IAgentTool> CreateTools(string workingDirectory)
    {
        var resolved = Path.GetFullPath(workingDirectory);
        return
        [
            new ReadTool(resolved),
            new WriteTool(resolved),
            new EditTool(resolved),
            new ShellTool(workingDirectory: resolved),
            new ListDirectoryTool(resolved),
            new GrepTool(resolved),
            new GlobTool(resolved)
        ];
    }
}
