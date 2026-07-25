namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// Input for <see cref="IStartConversationService.StartAsync"/>: the agent the citizen picked, the
/// first message they typed, and the model they selected in the picker (plus the agent's own default
/// so the service can decide whether that selection is actually an override).
/// </summary>
/// <param name="AgentId">Agent the conversation is created for.</param>
/// <param name="FirstMessage">The citizen's first message, sent after any override is persisted.</param>
/// <param name="SelectedModel">
/// Model chosen in the UI. Null/blank means "use the agent default" and no override is written.
/// </param>
/// <param name="AgentDefaultModel">
/// The agent's configured default model. When <paramref name="SelectedModel"/> equals this the
/// selection is not an override, so nothing is persisted and the agent default stays in force.
/// </param>
public sealed record StartConversationRequest(
    string AgentId,
    string FirstMessage,
    string? SelectedModel = null,
    string? AgentDefaultModel = null);

/// <summary>
/// Outcome of a start-conversation attempt. Only a <see cref="Success"/> result carries an identity the
/// caller may navigate to; every failure path deliberately leaves <see cref="ConversationId"/> null so a
/// caller can never route to a conversation that was not successfully started (issue #2036).
/// </summary>
public sealed record StartConversationResult
{
    /// <summary>Whether the conversation was created, configured, and the first message sent.</summary>
    public bool Success { get; init; }

    /// <summary>Agent the conversation belongs to. Always populated for successes.</summary>
    public string? AgentId { get; init; }

    /// <summary>Newly created conversation id. Null on every failure path, by design.</summary>
    public string? ConversationId { get; init; }

    /// <summary>Model persisted as the per-conversation override, or null when the agent default applies.</summary>
    public string? AppliedModelOverride { get; init; }

    /// <summary>Human-readable failure reason for surfacing in the UI. Null on success.</summary>
    public string? Error { get; init; }

    /// <summary>Builds a navigable success result.</summary>
    public static StartConversationResult Started(string agentId, string conversationId, string? appliedModelOverride) =>
        new() { Success = true, AgentId = agentId, ConversationId = conversationId, AppliedModelOverride = appliedModelOverride };

    /// <summary>Builds a non-navigable failure result (never carries a conversation id).</summary>
    public static StartConversationResult Failed(string error) =>
        new() { Success = false, Error = error };
}

/// <summary>
/// Orchestrates "start a new conversation from the portal landing experience": create the conversation,
/// persist the selected model as that conversation's override, then send the citizen's first message.
/// </summary>
/// <remarks>
/// This lives in a service rather than a Razor component (issue #2036) so that the ordering guarantee -
/// the override must be persisted <em>before</em> the first message is sent, otherwise the very first turn
/// runs on the agent default - and the failure semantics are unit-testable. It intentionally composes the
/// existing <see cref="IAgentInteractionService"/> and <see cref="IGatewayRestClient"/> plumbing rather
/// than introducing a parallel conversation API.
/// </remarks>
public interface IStartConversationService
{
    /// <summary>
    /// Creates the conversation, applies a non-default model selection as the persisted
    /// per-conversation override, sends the first message, and returns the agent/conversation identity
    /// the caller needs to navigate to <c>/chat/{agentId}/{conversationId}</c>.
    /// </summary>
    Task<StartConversationResult> StartAsync(StartConversationRequest request, CancellationToken cancellationToken = default);
}
