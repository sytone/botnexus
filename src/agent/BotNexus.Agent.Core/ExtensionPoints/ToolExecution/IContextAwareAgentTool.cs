using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;

namespace BotNexus.Agent.Core.ExtensionPoints.ToolExecution;

/// <summary>
/// Opts a tool into runtime-owned invocation metadata without accepting identity from model
/// arguments. Ordinary tools retain the existing execution contract.
/// </summary>
public interface IContextAwareAgentTool : IAgentTool
{
    /// <summary>Executes using the admitted loop context; a null run identity denotes a legacy caller.</summary>
    Task<AgentToolResult> ExecuteAsync(ToolExecutionContext context,
        CancellationToken cancellationToken = default, AgentToolUpdateCallback? onUpdate = null);
}
