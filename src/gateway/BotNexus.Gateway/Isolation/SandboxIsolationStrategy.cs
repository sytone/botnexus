using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Isolation;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Isolation;

/// <summary>
/// Sandbox isolation strategy — runs the agent in a separate OS process communicating
/// over IPC, confined by OS-level controls. The security boundary protects the user
/// from agent crashes, restricts what the agent can read or write on the host, and
/// limits the impact of prompt-injection or malicious tool output.
/// </summary>
/// <remarks>
/// Planned. When implemented, this will spawn a child process with reduced privileges,
/// a restricted file-system view, memory/CPU limits, and syscall filtering (seccomp /
/// AppArmor on Linux; Job Objects / AppContainer on Windows). Suitable for agents that
/// should not share the Gateway's memory or full host access but do not need the
/// overhead of a container.
/// </remarks>
public sealed class SandboxIsolationStrategy : IIsolationStrategy
{
    /// <inheritdoc />
    public string Name => "sandbox";

    /// <inheritdoc />
    public Task<IAgentHandle> CreateAsync(AgentDescriptor descriptor, AgentExecutionContext context, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException(
            $"The '{Name}' isolation strategy is not yet implemented. " +
            "Use 'in-process' for development or contribute an implementation.");
    }
}
