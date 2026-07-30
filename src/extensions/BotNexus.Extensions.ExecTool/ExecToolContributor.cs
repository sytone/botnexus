using BotNexus.Agent.Core.Tools;
using BotNexus.Gateway.Abstractions.Agents;
using System.IO.Abstractions;

namespace BotNexus.Extensions.ExecTool;

/// <summary>
/// Builds the <c>exec</c> tool per agent session so it runs in that agent's workspace.
/// <para>
/// This exists because of issue #2416. <see cref="ExecTool"/> has always accepted a working
/// directory, but nothing ever supplied one: both of its constructor parameters were optional, so the
/// extension loader auto-registered it as a bare DI singleton and the child process inherited the
/// gateway process's current directory - the user profile on Windows. Meanwhile <c>shell</c> is
/// constructed per session by the workspace tool factory with the resolved workspace. The two
/// execution tools therefore silently disagreed, and the platform-documented "write <c>tmp/q.py</c>
/// then run it" recipe failed from <c>exec</c> with a "no such file" error for a file that had just
/// been written successfully.
/// </para>
/// <para>
/// Contributing the tool session-scoped is the mechanism the platform already uses for every other
/// workspace-aware extension tool (see the data-store and skills contributors), so <c>exec</c> now
/// gets the same workspace <c>shell</c> does. An explicit <c>workingDir</c> argument still wins.
/// </para>
/// </summary>
public sealed class ExecToolContributor : IAgentToolContributor
{
    private readonly IFileSystem? _fileSystem;

    /// <summary>
    /// Creates the contributor. The file system is injected so Windows <c>.cmd</c>/<c>.bat</c>
    /// resolution stays testable; when omitted the tool uses the real file system.
    /// </summary>
    public ExecToolContributor(IFileSystem? fileSystem = null)
    {
        _fileSystem = fileSystem;
    }

    /// <inheritdoc />
    public Task<AgentToolContribution> ContributeAsync(
        AgentToolContributionContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Honour the agent's tool allowlist. Previously exec reached agents through the extension tool
        // registry, which the isolation strategy filters by descriptor.ToolIds; contributing the tool
        // instead moves it past that filter, so the same gate is reapplied here. An empty list (or the
        // ["*"] alias) means "all tools", matching InProcessIsolationStrategy.IsWildcardToolIds.
        if (!IsToolAllowed(context.Descriptor.ToolIds, ExecToolName))
            return Task.FromResult(new AgentToolContribution([]));

        IReadOnlyList<IAgentTool> tools = [new ExecTool(context.WorkspacePath, _fileSystem)];
        return Task.FromResult(new AgentToolContribution(tools));
    }

    /// <summary>The registered name of the tool this contributor builds.</summary>
    internal const string ExecToolName = "exec";

    /// <summary>
    /// Mirrors the isolation strategy's allowlist semantics: no ids, or the single wildcard id,
    /// means every tool is permitted; otherwise the tool must be named explicitly.
    /// Exposed internally so the gate is directly testable without the gateway DI graph.
    /// </summary>
    internal static bool IsToolAllowed(IReadOnlyList<string> toolIds, string toolName)
        => toolIds.Count == 0
           || (toolIds.Count == 1 && toolIds[0] == "*")
           || toolIds.Contains(toolName, StringComparer.OrdinalIgnoreCase);
}
