using System.Text;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Services;
using BotNexus.Gateway.Dispatching;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Services;

/// <summary>
/// Default <see cref="IAskUserCheckpointResumer"/>: resumes a conversation whose durable
/// <c>ask_user</c> checkpoint was answered or cancelled while no live waiter existed (issue #2047 -
/// the restart / reload / conversation-switch case). Rather than reconstructing an arbitrary provider
/// call stack, it models the answer as a new continuation turn seeded with the user's response (or an
/// explicit cancellation notice) and posts it through the canonical inbound lifecycle - the router
/// resolves/creates the active session and the orchestrator serialises it behind any in-flight work.
/// </summary>
/// <remarks>
/// The checkpoint service has already atomically claimed and cleared the checkpoint before calling
/// this, so this type never needs to re-check idempotency - it is only ever handed a single,
/// deduplicated continuation. The posted message carries the originating agent as its typed sender so
/// participant tracking and role derivation stay correct, and is stamped as an internal channel so it
/// does not echo back to a specific transport binding.
/// </remarks>
public sealed class AskUserCheckpointResumer(
    IConversationRouter conversationRouter,
    IInboundMessageOrchestrator messageOrchestrator,
    ILogger<AskUserCheckpointResumer> logger) : IAskUserCheckpointResumer
{
    private const string InternalChannel = "internal";

    /// <inheritdoc />
    public async Task ResumeAsync(AskUserRequest request, AskUserResponse response, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);

        var content = BuildContinuationText(request, response);

        // Route through the canonical inbound lifecycle seam so a fresh session is created (the
        // original session was almost certainly sealed by the restart) and the active-session
        // assignment is persisted before dispatch, exactly like an ordinary inbound message.
        var routing = await conversationRouter.ResolveInboundAsync(
            request.AgentId,
            ChannelKey.From(InternalChannel),
            ChannelAddress.From(request.AgentId.Value),
            request.ConversationId,
            cancellationToken,
            CitizenId.Of(request.AgentId)).ConfigureAwait(false);

        var accepted = messageOrchestrator.Post(new InboundMessage
        {
            ChannelType = ChannelKey.From(InternalChannel),
            SenderId = $"ask_user:{request.RequestId}",
            Sender = CitizenId.Of(request.AgentId),
            ChannelAddress = ChannelAddress.From(request.AgentId.Value),
            Content = content,
            RoutingHints = new InboundMessageRoutingHints(
                RequestedAgentId: request.AgentId,
                RequestedSessionId: routing.SessionId,
                RequestedConversationId: request.ConversationId)
        });

        if (!accepted)
        {
            logger.LogWarning(
                "ask_user continuation for conversation {ConversationId} (request {RequestId}) was refused by the orchestrator queue; the checkpoint was already cleared, so the user may need to re-prompt.",
                request.ConversationId, request.RequestId);
        }
        else
        {
            logger.LogInformation(
                "Resumed conversation {ConversationId} from durable ask_user checkpoint (request {RequestId}, cancelled={Cancelled}).",
                request.ConversationId, request.RequestId, response.WasCancelled);
        }
    }

    private static string BuildContinuationText(AskUserRequest request, AskUserResponse response)
    {
        if (response.WasCancelled)
        {
            return $"[ask_user] The user cancelled the pending prompt \"{request.Prompt}\" without answering. Continue accordingly (treat this as WasCancelled=true).";
        }

        var builder = new StringBuilder();
        builder.Append("[ask_user] The user answered the pending prompt \"")
            .Append(request.Prompt)
            .Append("\": ");

        var hasSelections = response.SelectedValues is { Count: > 0 };
        if (hasSelections)
        {
            builder.Append("selected ")
                .Append(string.Join(", ", response.SelectedValues!));
        }

        if (!string.IsNullOrWhiteSpace(response.FreeFormText))
        {
            if (hasSelections)
                builder.Append(" - ");
            builder.Append(response.FreeFormText!.Trim());
        }

        if (!hasSelections && string.IsNullOrWhiteSpace(response.FreeFormText))
        {
            builder.Append("(no content provided)");
        }

        return builder.ToString();
    }
}
