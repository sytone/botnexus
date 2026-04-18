using BotNexus.Agent.Core.Tools;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Security;
using BotNexus.Tools;
using System.IO.Abstractions;

namespace BotNexus.Gateway.Agents;

public sealed class DefaultAgentToolFactory : IAgentToolFactory
{
    public IReadOnlyList<IAgentTool> CreateTools(string workingDirectory, IPathValidator? pathValidator = null)
    {
        var resolved = Path.GetFullPath(workingDirectory);
        var fileSystem = new FileSystem();
        var effectivePathValidator = pathValidator ?? new DefaultPathValidator(policy: null, workspacePath: resolved);
        return
        [
            new ReadTool(resolved, effectivePathValidator, fileSystem),
            new WriteTool(resolved, effectivePathValidator, fileSystem),
            new EditTool(resolved, effectivePathValidator, fileSystem),
            new ShellTool(workingDirectory: resolved),
            new ListDirectoryTool(resolved, effectivePathValidator, fileSystem),
            new GrepTool(resolved, effectivePathValidator, fileSystem),
            new GlobTool(resolved, effectivePathValidator, fileSystem)
        ];
    }
}
