using BotNexus.Core.Models;

namespace BotNexus.Core.Abstractions;

/// <summary>Hook invoked at various points in the agent processing pipeline.</summary>
public interface IAgentHook
{
    /// <summary>Invoked before the agent processes a message.</summary>
    Task OnBeforeAsync(AgentHookContext context, CancellationToken cancellationToken = default);

    /// <summary>Invoked after the agent completes processing.</summary>
    Task OnAfterAsync(AgentHookContext context, CancellationToken cancellationToken = default);

    /// <summary>Invoked when an error occurs during processing.</summary>
    Task OnErrorAsync(AgentHookContext context, CancellationToken cancellationToken = default);
}
