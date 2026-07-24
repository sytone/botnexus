using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Abstractions.Services;

/// <summary>
/// The single channel-agnostic entry point for resolving a pending <c>ask_user</c> prompt (#2322).
/// </summary>
/// <remarks>
/// <para>
/// Before this seam existed, each channel reached into <see cref="IAskUserResponseRegistry"/>
/// directly and invented its own validation and error semantics: <c>GatewayHub.RespondToAskUser</c>
/// built an <see cref="AskUserResponse"/> inline, and <c>PendingAskUserInterceptor</c> built a
/// different, free-text-only one. Two entry points meant two behaviours, and a third channel would
/// have meant a third.
/// </para>
/// <para>
/// Every channel now calls <see cref="ResolveAsync"/>. The registry stays an internal
/// implementation detail of the resolver: channels never touch it.
/// </para>
/// </remarks>
public interface IAskUserPromptResolver
{
    /// <summary>
    /// Resolves the pending prompt for a conversation, whatever the originating channel.
    /// </summary>
    /// <param name="submission">Free-form text, structured selections, and/or explicit cancel.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome, describing success or the precise reason resolution was rejected.</returns>
    ValueTask<AskUserResolutionResult> ResolveAsync(
        AskUserSubmission submission,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the request id of the prompt currently pending on a conversation, when one is
    /// waiting. Channels use this to decide whether an inbound message should be treated as an
    /// answer rather than a new turn.
    /// </summary>
    bool TryGetPendingRequestId(ConversationId conversationId, out string requestId);
}
