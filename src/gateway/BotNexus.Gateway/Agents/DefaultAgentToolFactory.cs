using BotNexus.AgentCore.Tools;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Tools;
using System.IO.Abstractions;

namespace BotNexus.Gateway.Agents;

public sealed class DefaultAgentToolFactory : IAgentToolFactory
{
    public IReadOnlyList<IAgentTool> CreateTools(string workingDirectory)
    {
        var resolved = Path.GetFullPath(workingDirectory);
        var fileSystem = new FileSystem();
        return
        [
            new ReadTool(resolved, fileSystem),
            new WriteTool(resolved, fileSystem),
            new EditTool(resolved, fileSystem),
            new ShellTool(workingDirectory: resolved),
            new ListDirectoryTool(resolved, fileSystem),
            new GrepTool(resolved, fileSystem),
            new GlobTool(resolved)
        ];
    }
}
