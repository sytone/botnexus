using System.Security.Cryptography;
using System.Text;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Dispatching;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>Resumes only a supported exact idle resident session; never stops, resets or replaces it.</summary>
/// <remarks>The operator owns the SQL assignment and source checks. This boundary owns runtime
/// admission. A persisted intent precedes the one post. Unknown outcomes are never automatically
/// replayed. A prior receipt remains fenced until supported exact-outcome reconciliation.</remarks>
[ApiController]
[Route("api/agents/{agentId}/conversations/{conversationId}/recovery")]
public sealed class ConversationRecoveryController(
    IConversationStore conversations,
    ISessionStore sessions,
    IAgentSupervisor supervisor,
    IInboundMessageOrchestrator orchestrator) : ControllerBase
{
    private const string ReceiptKey = "conversationRecovery";

    /// <summary>Admits at most one continuation after queue, run and tracked-command reservations.</summary>
    [HttpPost("resume")]
    public async Task<IActionResult> Resume(string agentId, string conversationId,
        [FromBody] ConversationResumeRequest request, CancellationToken cancellationToken)
    {
        if (!AgentId.TryFrom(agentId, out var agent) || !ConversationId.TryFrom(conversationId, out var id) ||
            !SessionId.TryFrom(request.ExpectedSessionId, out var expected) ||
            request.ExpectedMessageCount is null or < 0 ||
            string.IsNullOrWhiteSpace(request.OperationId) || request.OperationId.Length > 160 ||
            string.IsNullOrWhiteSpace(request.Message) || Encoding.UTF8.GetByteCount(request.Message) > 16384)
            return BadRequest(new { status = "invalid-recovery-request" });
        var conversation = await conversations.GetAsync(id, cancellationToken).ConfigureAwait(false);
        var writeFailure = ClientConversationWriteGuard.Check(this, conversation, agent);
        if (writeFailure is not null) return writeFailure;
        if (conversation is null || conversation.ActiveSessionId != expected || conversation.Status != ConversationStatus.Active)
            return Conflict(new { status = "binding-changed" });
        if (conversation.PendingAskUserJson is not null)
            return Conflict(new { status = "protected-prompt-pending" });
        var version = conversation.Version;
        var inbound = new InboundMessage
        {
            ChannelType = ChannelKey.From("api"), ChannelAddress = ChannelAddress.From(id.Value),
            SenderId = "api:conversation-recovery", Sender = CitizenId.Of(agent), Content = request.Message,
            RoutingHints = new InboundMessageRoutingHints(agent, expected, id),
            Metadata = new Dictionary<string, object?> { ["recoveryOperationId"] = request.OperationId }
        };
        using var queueReservation = orchestrator.TryReserveIdle(inbound);
        if (queueReservation is null) return Conflict(new { status = "queue-busy-or-unsupported" });
        var handle = supervisor.GetHandle(agent, expected);
        if (handle is null || handle.AgentId != agent || handle.SessionId != expected)
            return Conflict(new { status = "unloaded-or-unsupported" });
        using var runtimeReservation = supervisor.TryReserveMaintenance(handle);
        if (runtimeReservation is null) return Conflict(new { status = "execution-busy-or-unsupported" });
        if (handle.RecoverySafety != "safe")
            return Conflict(new { status = "runtime-safety-unavailable" });
        var session = await sessions.GetAsync(expected, cancellationToken).ConfigureAwait(false);
        if (session is null || session.AgentId != agent || session.ConversationId != id || session.Status != SessionStatus.Active)
            return Conflict(new { status = "session-unavailable" });
        if (request.ExpectedMessageCount is { } count && session.MessageCount != count)
            return Conflict(new { status = "activity-changed" });
        if (session.Metadata.TryGetValue(ReceiptKey, out var prior) && prior is not null)
        {
            // Unknown means possibly admitted. Unrelated transcript growth is not reconciliation.
            // Preserve this receipt until a separately supported exact-outcome reconciliation exists.
            return Conflict(new { status = "prior-recovery-unresolved", safeToRetry = false });
        }
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(request.Message)));
        var receipt = new Dictionary<string, object?>
        {
            ["operationId"] = request.OperationId, ["digest"] = digest, ["status"] = "unknown",
            ["messageCount"] = session.MessageCount, ["at"] = DateTimeOffset.UtcNow
        };
        var saved = await sessions.PatchMetadataAsync(expected,
            new Dictionary<string, object?> { [ReceiptKey] = receipt }, cancellationToken).ConfigureAwait(false);
        if (saved.Outcome != SessionMutationOutcome.Applied)
            return Conflict(new { status = "intent-not-saved", safeToRetry = false });
        var current = await conversations.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (current is null || current.AgentId != agent || current.ActiveSessionId != expected ||
            current.Version != version || current.Status != ConversationStatus.Active || current.PendingAskUserJson is not null ||
            !ReferenceEquals(handle, supervisor.GetHandle(agent, expected)) || handle.RecoverySafety != "safe")
            return Conflict(new { status = "binding-or-safety-changed", safeToRetry = false });
        // Both reservations remain held here. Queue processing is released only after the runtime
        // reservation disposes. No await or mutable source construction follows the final check.
        if (!queueReservation.TryPost(inbound))
            return Conflict(new { status = "admission-refused", safeToRetry = false });
        return Accepted(new { status = "admitted", conversationId, sessionId = expected.Value,
            operationId = request.OperationId, safeToRetry = false });
    }
}

/// <summary>One bounded continuation with mandatory observed message count. Null is retained only
/// for JSON deserialization and is rejected before any side effect.</summary>
public sealed record ConversationResumeRequest(string ExpectedSessionId, string OperationId, string Message,
    int? ExpectedMessageCount = null);
