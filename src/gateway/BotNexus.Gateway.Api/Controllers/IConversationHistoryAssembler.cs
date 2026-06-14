using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// Assembles a chronological, cross-session conversation history view from the session store.
/// </summary>
/// <remarks>
/// This is the single home for the conversation history-assembly state machine -- cross-session
/// listing, the #732 null-conversation fallback, inter-session boundary-marker insertion,
/// <c>NO_REPLY</c> filtering (#773), folded-entry skipping, compaction-summary projection, and
/// newest-first paging. It was extracted out of <see cref="ConversationsController.GetHistory"/>
/// so the assembly logic is unit-testable in isolation (without spinning up MVC) and reusable by
/// the SignalR/portal path and any future client, instead of being trapped in an HTTP action.
/// </remarks>
public interface IConversationHistoryAssembler
{
    /// <summary>
    /// Builds the paginated history view for a conversation.
    /// </summary>
    /// <param name="conversationId">The conversation to assemble history for.</param>
    /// <param name="limit">
    /// Maximum number of entries to return. The caller is responsible for any upper bound
    /// (the controller clamps to 200 before calling); values are not re-clamped here.
    /// </param>
    /// <param name="offset">
    /// Zero-based offset from the most recent entry. <c>offset=0</c> returns the latest page,
    /// larger offsets page backwards into older history.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The assembled, paginated history, or <c>null</c> when the conversation does not exist
    /// (callers map <c>null</c> to 404).
    /// </returns>
    Task<ConversationHistoryResponse?> AssembleAsync(
        ConversationId conversationId,
        int limit,
        int offset,
        CancellationToken cancellationToken = default);
}
