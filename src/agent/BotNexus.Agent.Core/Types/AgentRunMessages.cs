using System.Collections;
using System.Collections.Frozen;
using BotNexus.Agent.Core.ExtensionPoints.RunCompletion;

namespace BotNexus.Agent.Core.Types;

/// <summary>
/// A run-local message-list snapshot returned by the agent prompt APIs. Collection membership,
/// completion, identity and pending-tool IDs are captured before another run can start; message
/// payloads retain their existing shallow read-only semantics.
/// </summary>
/// <remarks>
/// Cancellation after admission returns this list rather than throwing. Cancellation before
/// admission still throws <see cref="OperationCanceledException"/> and has no run evidence.
/// A synthetic terminal message remains in the list for timeline compatibility but is not a
/// provider invocation or usage measurement. Real provider-aborted messages are not synthetic.
/// </remarks>
public sealed class AgentRunMessages : IReadOnlyList<AgentMessage>
{
    private readonly IReadOnlyList<AgentMessage> _messages;

    internal AgentRunMessages(IReadOnlyList<AgentMessage> messages, AgentRunId agentRunId,
        RunCompletionResult? completion, IReadOnlySet<string> pendingToolCalls,
        CancellationToken cancellationToken, AssistantAgentMessage? syntheticTerminalMessage = null,
        IReadOnlyList<AgentMessage>? evidenceMessages = null)
    {
        _messages = Array.AsReadOnly(messages.ToArray());
        EvidenceMessages = Array.AsReadOnly((evidenceMessages ?? messages).ToArray());
        AgentRunId = agentRunId;
        Completion = completion;
        PendingToolCalls = pendingToolCalls.ToFrozenSet(StringComparer.Ordinal);
        CancellationToken = cancellationToken;
        SyntheticTerminalMessage = syntheticTerminalMessage;
    }

    /// <summary>
    /// The run-local timeline, including provider output preceding a caught failure. It may contain
    /// more messages than the compatibility return list, which remains a singleton on failure.
    /// </summary>
    public IReadOnlyList<AgentMessage> EvidenceMessages { get; }
    /// <summary>The identity of this run, independent of subsequent agent state.</summary>
    public AgentRunId AgentRunId { get; }
    /// <summary>The terminal completion, or null when the runner supplied none.</summary>
    public RunCompletionResult? Completion { get; }
    /// <summary>Tool invocations that started without reporting an end in this run.</summary>
    public IReadOnlySet<string> PendingToolCalls { get; }
    /// <summary>
    /// The caller token when it cancelled, otherwise the signalled internal run token. A provider
    /// abort without token cancellation uses an already-cancelled token. None for other completions.
    /// </summary>
    public CancellationToken CancellationToken { get; }
    /// <summary>The runtime-generated terminal marker, or null for a provider terminal message.</summary>
    public AssistantAgentMessage? SyntheticTerminalMessage { get; }
    /// <inheritdoc />
    public int Count => _messages.Count;
    /// <inheritdoc />
    public AgentMessage this[int index] => _messages[index];
    /// <inheritdoc />
    public IEnumerator<AgentMessage> GetEnumerator() => _messages.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
