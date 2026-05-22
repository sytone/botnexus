using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Isolation;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Isolation;

/// <summary>
/// Container isolation strategy — runs the agent in a Docker container. The container
/// boundary protects the user by hiding the host file system, network, and other agents;
/// the agent sees only the volumes and ports explicitly granted to it.
/// </summary>
/// <remarks>
/// Planned. When implemented, this will pull/build an image, mount only the volumes the
/// agent needs (workspace, mounted secrets), restrict network egress per agent policy,
/// and communicate with the isolated agent process over gRPC/HTTP. Suitable for untrusted
/// agents, multi-tenant hosts, and workloads where a clean blast radius is required.
/// Hardened container runtimes (e.g., gVisor, Firecracker, Kata Containers) may be exposed
/// as separate strategies or as a configuration option on this strategy.
/// </remarks>
public sealed class ContainerIsolationStrategy : IIsolationStrategy
{
    /// <inheritdoc />
    public string Name => "container";

    /// <inheritdoc />
    public Task<IAgentHandle> CreateAsync(AgentDescriptor descriptor, AgentExecutionContext context, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException(
            $"The '{Name}' isolation strategy is not yet implemented. " +
            "Use 'in-process' for development or contribute a container runner.");
    }
}
