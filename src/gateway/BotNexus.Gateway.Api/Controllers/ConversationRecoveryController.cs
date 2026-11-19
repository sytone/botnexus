using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
/// <remarks>SQL assignment/source checks belong to the supervisor. Native queue and run reservations
/// fence admission. Per-operation intents and actual processing outcomes remain separate from product
/// acceptance. Unknown intents are not resent, including after process restart or unrelated activity.</remarks>
[ApiController]
[Route("api/agents/{agentId}/conversations/{conversationId}/recovery")]
public sealed class ConversationRecoveryController(
    IConversationStore conversations,
    ISessionStore sessions,
    IAgentSupervisor supervisor,
    IInboundMessageOrchestrator orchestrator) : ControllerBase
{
    private const string LatestKey = "conversationRecovery";
    private const string OperationPrefix = "conversationRecoveryOperation:";
    private const int MaximumOperations = 64;

    /// <summary>Reads an exact original operation without exposing message text or starting execution.</summary>
    [HttpGet("operations/{operationId}")]
    public async Task<IActionResult> Status(string agentId, string conversationId,
        [FromQuery] string expectedSessionId, string operationId, CancellationToken cancellationToken)
    {
        if (!AgentId.TryFrom(agentId, out var agent) || !ConversationId.TryFrom(conversationId, out var id) ||
            !SessionId.TryFrom(expectedSessionId, out var expected) || !ValidOperation(operationId))
            return BadRequest(new { status = "invalid-recovery-request" });
        var conversation = await conversations.GetAsync(id, cancellationToken).ConfigureAwait(false);
        var denied = ClientConversationWriteGuard.Check(this, conversation, agent);
        if (denied is not null) return denied;
        if (conversation is null || conversation.ActiveSessionId != expected)
            return Conflict(new { status = "binding-changed" });
        var session = await sessions.GetAsync(expected, cancellationToken).ConfigureAwait(false);
        if (session is null || session.AgentId != agent || session.ConversationId != id)
            return Conflict(new { status = "session-unavailable" });
        var receipt = ReadReceipt(session, OperationKey(operationId));
        return receipt is null
            ? session.Metadata.ContainsKey(OperationKey(operationId))
                ? Conflict(new { status = "corrupt-recovery-receipt", safeToRetry = false })
                : NotFound(new { status = "operation-not-recorded" })
            : Ok(receipt);
    }

    /// <summary>Admits at most one continuation for the exact observed session and operation.</summary>
    [HttpPost("resume")]
    public async Task<IActionResult> Resume(string agentId, string conversationId,
        [FromBody] ConversationResumeRequest request, CancellationToken cancellationToken)
    {
        if (!AgentId.TryFrom(agentId, out var agent) || !ConversationId.TryFrom(conversationId, out var id) ||
            !SessionId.TryFrom(request.ExpectedSessionId, out var expected) || request.ExpectedMessageCount is null or < 0 ||
            !ValidOperation(request.OperationId) || string.IsNullOrWhiteSpace(request.Message) ||
            Encoding.UTF8.GetByteCount(request.Message) > 16384)
            return BadRequest(new { status = "invalid-recovery-request" });
        var conversation = await conversations.GetAsync(id, cancellationToken).ConfigureAwait(false);
        var writeFailure = ClientConversationWriteGuard.Check(this, conversation, agent);
        if (writeFailure is not null) return writeFailure;
        if (conversation is null || conversation.ActiveSessionId != expected || conversation.Status != ConversationStatus.Active)
            return Conflict(new { status = "binding-changed" });
        if (conversation.PendingAskUserJson is not null)
            return Conflict(new { status = "protected-prompt-pending" });
        var version = conversation.Version;
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(request.Message)));
        var operationKey = OperationKey(request.OperationId);
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
        if (handle.RecoverySafety != "safe") return Conflict(new { status = "runtime-safety-unavailable" });
        var session = await sessions.GetAsync(expected, cancellationToken).ConfigureAwait(false);
        if (session is null || session.AgentId != agent || session.ConversationId != id || session.Status != SessionStatus.Active)
            return Conflict(new { status = "session-unavailable" });
        var existing = ReadReceipt(session, operationKey);
        if (session.Metadata.ContainsKey(operationKey) && existing is null)
            return Conflict(new { status = "corrupt-recovery-receipt", safeToRetry = false });
        if (existing is not null)
            return existing.OperationId == request.OperationId && existing.Digest == digest && existing.MessageCount == request.ExpectedMessageCount
                ? Ok(existing) : Conflict(new { status = "operation-input-conflict", safeToRetry = false });
        if (session.MessageCount != request.ExpectedMessageCount) return Conflict(new { status = "activity-changed" });
        var prior = ReadReceipt(session, LatestKey);
        if (session.Metadata.ContainsKey(LatestKey) && (prior is null || prior.Status != "processing-finished"))
            return Conflict(new { status = "prior-recovery-unresolved", safeToRetry = false });
        if (session.Metadata.Keys.Count(key => key.StartsWith(OperationPrefix, StringComparison.Ordinal)) >= MaximumOperations)
            return Conflict(new { status = "recovery-history-limit", safeToRetry = false });
        var receipt = new ConversationRecoveryReceipt(request.OperationId, digest, "unknown", session.MessageCount,
            DateTimeOffset.UtcNow, null, null);
        await SaveReceiptAsync(receipt, expected, operationKey, cancellationToken).ConfigureAwait(false);
        var current = await conversations.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (current is null || current.AgentId != agent || current.ActiveSessionId != expected || current.Version != version ||
            current.Status != ConversationStatus.Active || current.PendingAskUserJson is not null ||
            !ReferenceEquals(handle, supervisor.GetHandle(agent, expected)) || handle.RecoverySafety != "safe")
            return Conflict(new { status = "binding-or-safety-changed", safeToRetry = false });

        async Task RecordStartedAsync()
        {
            // Invoked at the producer-owned execution boundary, not inferred from admission.
            var target = await conversations.GetAsync(id, CancellationToken.None).ConfigureAwait(false);
            var owned = await sessions.GetAsync(expected, CancellationToken.None).ConfigureAwait(false);
            if (target is null || target.AgentId != agent || target.ActiveSessionId != expected ||
                target.Status != ConversationStatus.Active || target.PendingAskUserJson is not null ||
                owned is null || owned.AgentId != agent || owned.ConversationId != id || owned.Status != SessionStatus.Active ||
                !ReferenceEquals(handle, supervisor.GetHandle(agent, expected)))
                throw new InvalidOperationException("Recovery execution binding changed.");
            var running = receipt with { Status = "processing-started", StartedAt = DateTimeOffset.UtcNow };
            await SaveReceiptAsync(running, expected, operationKey, CancellationToken.None).ConfigureAwait(false);
        }
        var control = new InboundExecutionControl(CancellationToken.None, RecordStartedAsync);
        if (!queueReservation.TryPost(inbound, control))
            return Conflict(new { status = "admission-refused", safeToRetry = false });
        // The worker is held behind this reservation until the admitted receipt is saved. Failure
        // to save leaves the original unknown intent and does not permit a second post.
        await SaveReceiptAsync(receipt with { Status = "admitted" }, expected, operationKey, CancellationToken.None).ConfigureAwait(false);
        if (queueReservation.Completion is { } completion)
            _ = RecordCompletionAsync(completion, receipt, expected, operationKey);
        return Accepted(new { status = "admitted", conversationId, sessionId = expected.Value,
            operationId = request.OperationId, safeToRetry = false });
    }

    private async Task RecordCompletionAsync(Task<InboundDispatchResult> completion,
        ConversationRecoveryReceipt receipt, SessionId sessionId, string operationKey)
    {
        try
        {
            var result = await completion.ConfigureAwait(false);
            var current = await sessions.GetAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
            var latest = current is null ? null : ReadReceipt(current, operationKey);
            // A processor return without actual-start evidence is not proof the agent ran.
            var finished = (latest ?? receipt) with
            {
                Status = latest?.Status == "processing-started" ? "processing-finished" : "processing-unconfirmed",
                FinishedAt = DateTimeOffset.UtcNow,
                DispatchStatus = result.Status.ToString()
            };
            await SaveReceiptAsync(finished, sessionId, operationKey, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Preserve admitted/started/unknown evidence. Never publish exception text, claim the
            // processor stopped successfully, or retry the underlying operation after uncertainty.
        }
    }

    private async Task SaveReceiptAsync(ConversationRecoveryReceipt receipt, SessionId sessionId,
        string operationKey, CancellationToken cancellationToken)
    {
        var saved = await sessions.PatchMetadataAsync(sessionId, new Dictionary<string, object?>
        {
            [operationKey] = receipt, [LatestKey] = receipt
        }, cancellationToken).ConfigureAwait(false);
        if (saved.Outcome != SessionMutationOutcome.Applied)
            throw new InvalidOperationException("Recovery receipt could not be saved.");
    }

    private static ConversationRecoveryReceipt? ReadReceipt(GatewaySession session, string key)
    {
        if (!session.Metadata.TryGetValue(key, out var value) || value is null) return null;
        try
        {
            var receipt = JsonSerializer.SerializeToElement(value).Deserialize<ConversationRecoveryReceipt>(
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (receipt is null || !ValidOperation(receipt.OperationId) || receipt.Digest is not { Length: 64 } ||
                receipt.Digest.Any(character => !char.IsAsciiHexDigit(character)) || receipt.MessageCount < 0 ||
                receipt.At == default || receipt.Status is not ("unknown" or "admitted" or "processing-started" or "processing-finished" or "processing-unconfirmed") ||
                (receipt.Status is "processing-started" or "processing-finished" && receipt.StartedAt is null) ||
                (receipt.Status is "processing-finished" or "processing-unconfirmed" && receipt.FinishedAt is null))
                return null;
            return receipt;
        }
        catch (JsonException) { return null; }
    }

    private static bool ValidOperation(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 160;
    private static string OperationKey(string value) => OperationPrefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

/// <summary>Bounded continuation with mandatory observed count; null deserialization fails closed.</summary>
public sealed record ConversationResumeRequest(string ExpectedSessionId, string OperationId, string Message,
    int? ExpectedMessageCount = null);

/// <summary>Exact operation evidence. Processing-finished is not product acceptance or command termination.</summary>
public sealed record ConversationRecoveryReceipt(string OperationId, string Digest, string Status, int MessageCount,
    DateTimeOffset At, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, string? DispatchStatus = null);
