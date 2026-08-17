namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Image payload attached to an <see cref="AgentUserMessage"/>, owned by the gateway (#3040).
/// </summary>
/// <param name="Value">The serialized image payload value (data URI or absolute URL).</param>
/// <remarks>
/// The gateway previously published <c>BotNexus.Agent.Core.Types.AgentImageContent</c> through its
/// abstraction assemblies, so every consumer of a "gateway contract" was in fact bound to the agent
/// implementation. This is the gateway's own equivalent; the isolation strategy - the layer that is
/// entitled to know about core - maps between the two at the boundary.
/// </remarks>
public sealed record AgentImageContent(string Value);

/// <summary>
/// A user-authored message crossing the gateway-to-agent seam, with optional multimodal image
/// payloads. This is the gateway's own contract type (#3040), not a re-export of an agent-core type.
/// </summary>
/// <param name="Content">The user message text content.</param>
/// <param name="Images">Optional image payloads for multimodal models.</param>
/// <remarks>
/// <para>
/// <b>Why this type exists.</b> <c>IAgentHandle</c> - the central gateway-to-agent seam - typed six
/// of its methods on <c>BotNexus.Agent.Core.Types.UserMessage</c>, concealed behind
/// <c>using AgentUserMessage = BotNexus.Agent.Core.Types.UserMessage;</c>. A reader saw a
/// gateway-sounding name and reasonably assumed a gateway contract, and any scan for core leakage
/// that greps namespaces rather than resolving aliases reported zero. The abstraction assemblies
/// exist precisely so a downstream component can avoid depending on the agent implementation, so
/// publishing core types through them defeated their only purpose.
/// </para>
/// <para>
/// The shape is deliberately identical to the core type it replaces so the mapping at the boundary
/// is total and lossless in both directions - there is no behaviour to get wrong, which is what
/// makes this a type-boundary refactor rather than a semantic change.
/// </para>
/// </remarks>
public sealed record AgentUserMessage(string Content, IReadOnlyList<AgentImageContent>? Images = null)
{
    /// <summary>
    /// Marks this message as a system-injected side turn (e.g. a pre-compaction memory flush,
    /// #1845) that must only be consumed at a genuine idle turn boundary.
    /// </summary>
    /// <remarks>
    /// A normal steered user message (default <see langword="false"/>) is drained at the next turn
    /// boundary even mid-flight. A <see cref="DeferWhileBusy"/> message is held aside by the agent
    /// loop while the current run still has pending tool calls, then released once the run reaches
    /// an idle boundary. This prevents a mid-work flush turn from consuming the loop's continuation
    /// and abandoning the original in-flight task.
    /// </remarks>
    public bool DeferWhileBusy { get; init; }
}
