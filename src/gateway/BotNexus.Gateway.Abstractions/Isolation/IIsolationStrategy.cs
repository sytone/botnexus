using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Abstractions.Isolation;

/// <summary>
/// Defines an execution environment strategy for running agent instances.
/// Each strategy controls how an agent's code and state are isolated.
/// </summary>
/// <remarks>
/// <para>Isolation strategies are registered by name and selected per-agent via
/// <see cref="AgentDescriptor.IsolationStrategy"/>.</para>
/// <para>Built-in strategies:</para>
/// <list type="bullet">
///   <item><b>in-process</b> — Runs the agent directly in the Gateway process.
///   Fastest, no isolation. Default for development and simple deployments.</item>
///   <item><b>sandbox</b> — Runs the agent in a restricted AppDomain or process
///   with limited permissions. Stub — Phase 2.</item>
///   <item><b>container</b> — Runs the agent in a Docker container.
///   Stub — Phase 2.</item>
///   <item><b>remote</b> — Delegates to a remote agent service via HTTP/gRPC.
///   Stub — Phase 2.</item>
/// </list>
/// </remarks>
public interface IIsolationStrategy
{
    /// <summary>
    /// The unique name of this isolation strategy (e.g., "in-process", "container").
    /// Must match the value used in <see cref="AgentDescriptor.IsolationStrategy"/>.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Creates a new agent handle within this isolation boundary.
    /// </summary>
    /// <param name="descriptor">The agent descriptor with configuration.</param>
    /// <param name="context">Execution context including session info and history.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A handle to the running agent.</returns>
    Task<IAgentHandle> CreateAsync(
        AgentDescriptor descriptor,
        AgentExecutionContext context,
        CancellationToken cancellationToken = default);
}
