using BotNexus.Agent.Core.Tools;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Security;
using BotNexus.Tools;
using System.IO.Abstractions;

namespace BotNexus.Gateway.Agents;

public sealed class DefaultAgentToolFactory : IAgentToolFactory
{
    private readonly ShellPreference _shellPreference;
    private readonly string? _platformConfigPath;
    private readonly string[]? _shellCommand;

    public DefaultAgentToolFactory(
        ShellPreference shellPreference = ShellPreference.Auto,
        string? platformConfigPath = null,
        string[]? shellCommand = null)
    {
        _shellPreference = shellPreference;
        _platformConfigPath = platformConfigPath;
        _shellCommand = shellCommand;
    }

    public IReadOnlyList<IAgentTool> CreateTools(string workingDirectory, IPathValidator? pathValidator = null, string[]? shellCommand = null)
    {
        var resolved = Path.GetFullPath(workingDirectory);
        var fileSystem = new FileSystem();

        IPathValidator effectivePathValidator;
        if (pathValidator is not null)
        {
            // A custom validator was provided — use it as-is; the caller is responsible
            // for its deny rules (including config.json protection if needed).
            effectivePathValidator = pathValidator;
        }
        else
        {
            // Build a policy that denies writes to the platform config path (issue #633).
            // Note: ShellTool does not use IPathValidator; restricting shell commands from
            // accessing config.json is tracked separately in issue #260.
            var deniedPaths = new List<string>();
            if (!string.IsNullOrWhiteSpace(_platformConfigPath))
                deniedPaths.Add(_platformConfigPath);

            var policy = deniedPaths.Count > 0
                ? new FileAccessPolicy { DeniedPaths = [.. deniedPaths] }
                : null;

            effectivePathValidator = new DefaultPathValidator(policy: policy, workspacePath: resolved);
        }

        var effectiveShellCommand = shellCommand ?? _shellCommand;

        return
        [
            new ReadTool(resolved, effectivePathValidator, fileSystem),
            new WriteTool(resolved, effectivePathValidator, fileSystem),
            new EditTool(resolved, effectivePathValidator, fileSystem),
            new ShellTool(workingDirectory: resolved, shellPreference: _shellPreference, shellCommand: effectiveShellCommand),
            new ListDirectoryTool(resolved, effectivePathValidator, fileSystem),
            new GrepTool(resolved, effectivePathValidator, fileSystem),
            new GlobTool(resolved, effectivePathValidator, fileSystem)
        ];
    }
}
