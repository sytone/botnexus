using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Abstractions.Isolation;

/// <summary>
/// Defines the execution boundary that contains an agent instance.
/// Isolation is the security boundary between an agent and the user's environment —
/// it controls what the agent can reach, do, and exfiltrate.
/// </summary>
/// <remarks>
/// <para><b>Why isolation matters.</b> Agents act on behalf of users but cannot be
/// fully trusted: they may be prompt-injected, may execute attacker-controlled tool
/// output, or may simply make mistakes. The isolation strategy is the platform's
/// primary defense — it determines blast radius if an agent is compromised. A
/// stricter strategy reduces the user's exposure at the cost of speed and complexity.</para>
///
/// <para>Isolation strategies are registered by name and selected per-agent via
/// <see cref="AgentDescriptor.IsolationStrategy"/>. The strategy spectrum runs from
/// fastest/least-isolated to slowest/most-isolated:</para>
///
/// <list type="bullet">
///   <item><b>in-process</b> — Runs the agent inside the Gateway process. No security
///   boundary; the agent shares memory, file handles, and OS identity with the Gateway.
///   Fastest. Default for development and trusted single-user deployments.</item>
///
///   <item><b>sandbox</b> — Runs the agent in a separate OS process communicating
///   over IPC, confined by OS-level controls (e.g., reduced privileges, restricted
///   file system view, syscall filtering). Protects the host process from agent
///   crashes and limits what the agent can read/write directly. Planned.</item>
///
///   <item><b>container</b> — Runs the agent in a Docker container. The agent sees
///   only the volumes and network it is granted; the host file system and other
///   agents are invisible. Suitable for untrusted agents and multi-tenant hosts.
///   Planned.</item>
///
///   <item><b>remote</b> — Delegates execution to a remote machine via HTTP/gRPC.
///   The agent never runs on the user's machine at all. Strongest isolation from
///   user-local resources; appropriate when the agent must not see local files or
///   credentials. Planned.</item>
/// </list>
///
/// <para>Additional strategies (e.g., hardened-container runtimes such as gVisor or
/// Firecracker) may be added later via the same interface.</para>
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
