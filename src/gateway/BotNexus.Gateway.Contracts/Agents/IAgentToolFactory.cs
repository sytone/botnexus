using BotNexus.Agent.Core.Tools;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Security;

namespace BotNexus.Gateway.Abstractions.Agents;

/// <summary>
/// Creates built-in workspace-scoped tools for an agent.
/// </summary>
public interface IAgentToolFactory
{
    /// <summary>
    /// Creates tools scoped to the provided working directory.
    /// </summary>
    /// <remarks>
    /// The workspace root is a <see cref="WorkingDir"/> rather than a bare string (#502). Every
    /// implementation and every file tool below this seam previously re-validated the same string
    /// with its own <c>IsNullOrWhiteSpace</c> throw; the value object performs that check once, at
    /// construction, so an unusable workspace is rejected by the caller that produced it instead of
    /// by whichever tool happened to be constructed first.
    /// </remarks>
    /// <param name="workingDirectory">Agent workspace root.</param>
    /// <param name="pathValidator">Path validator used by file tools.</param>
    /// <param name="shellCommand">Optional shell command override for the shell tool.</param>
    /// <returns>Built-in tools bound to the workspace.</returns>
    IReadOnlyList<IAgentTool> CreateTools(WorkingDir workingDirectory, IPathValidator? pathValidator = null, string[]? shellCommand = null);
}
