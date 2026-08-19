using BotNexus.Agent.Core.Types;
using BotNexus.Gateway.Contracts.Memory;

namespace BotNexus.Memory.Tools;

/// <summary>
/// The single execution-time enablement check shared by <see cref="MemorySaveTool"/>,
/// <see cref="MemorySearchTool"/> and <see cref="MemoryGetTool"/> (issue #3361).
/// </summary>
/// <remarks>
/// <para>
/// Deliberately one helper rather than three copies of the same <c>if</c>. The failure this fixes
/// is precisely that an enablement decision made in one place was not consulted in another; three
/// independent re-checks would be three chances for the next tool to be added without one.
/// </para>
/// <para>
/// The refusal is a normal tool result, not an exception. The model has to be able to read why it
/// was refused and stop retrying; an exception surfaces as a generic tool failure and invites a
/// retry loop against a control that will keep saying no.
/// </para>
/// </remarks>
public static class MemoryEnablementGate
{
    /// <summary>
    /// The refusal text returned by every memory tool when memory is disabled for the agent.
    /// </summary>
    /// <remarks>
    /// Actionable by contract: it names the condition, states that nothing was read or written, and
    /// points at the operator-side remedy, so neither the model nor a human reading the transcript
    /// mistakes the refusal for an empty store or a transient error.
    /// </remarks>
    public const string RefusalMessage =
        "Memory is disabled for this agent. No memory was read or written. "
        + "Re-enable memory in the agent's configuration to use the memory tools.";

    /// <summary>
    /// Returns the refusal result when memory is disabled, or <see langword="null"/> when the call
    /// may proceed.
    /// </summary>
    /// <remarks>
    /// A <see langword="null"/> <paramref name="provider"/> is passthrough - see
    /// <see cref="IMemoryEnablementProvider"/> for why. The check runs before any argument coercion
    /// or store resolution so a disabled agent performs <b>zero</b> calls into
    /// <see cref="BotNexus.Gateway.Contracts.Memory.IAgentMemory"/> or <see cref="IMemoryStore"/>.
    /// </remarks>
    public static AgentToolResult? Refuse(IMemoryEnablementProvider? provider)
        => provider is null || provider.IsMemoryEnabled()
            ? null
            : new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, RefusalMessage)]);
}
