using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Isolation;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Isolation;

/// <summary>
/// Remote isolation strategy — delegates execution to a remote machine over HTTP/gRPC.
/// The strongest isolation from user-local resources: the agent never runs on the user's
/// machine, so local files, credentials, processes, and network surfaces are invisible
/// to it unless explicitly forwarded.
/// </summary>
/// <remarks>
/// Planned. When implemented, this will forward prompts to a remote Gateway endpoint,
/// relay streaming events back to local callers, and enforce that any resource the
/// remote agent needs (workspace files, credentials, tool endpoints) be granted
/// explicitly. Appropriate when the user requires that an agent never touch their
/// machine — for sensitive data domains, or when delegating to a centrally-managed
/// agent fleet.
/// </remarks>
public sealed class RemoteIsolationStrategy : IIsolationStrategy
{
    /// <inheritdoc />
    public string Name => "remote";

    /// <inheritdoc />
    public Task<IAgentHandle> CreateAsync(AgentDescriptor descriptor, AgentExecutionContext context, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException(
            $"The '{Name}' isolation strategy is not yet implemented. " +
            "Use 'in-process' for development or configure a supported remote backend.");
    }
}
