using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Services;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Services;

/// <summary>
/// Default <see cref="IAskUserCheckpointService"/>: resolves an <c>ask_user</c> prompt as a durable,
/// resumable checkpoint (issue #2047). A live in-memory waiter is completed directly; otherwise the
/// durable <see cref="Conversation.PendingAskUserJson"/> checkpoint is atomically claimed and cleared
/// and a continuation is dispatched through the optional <see cref="IAskUserCheckpointResumer"/> so the
/// conversation resumes even after a gateway restart, reload, or conversation switch destroyed the
/// original blocked tool task.
/// </summary>
/// <remarks>
/// Atomicity is guaranteed by a per-conversation gate around the load-check-clear sequence, so two
/// competing submissions (duplicate client, cross-client race) cannot both claim the same checkpoint.
/// Exactly one resolves as <see cref="AskUserResolveOutcome.LiveCompleted"/> or
/// <see cref="AskUserResolveOutcome.ResumedFromCheckpoint"/>; the loser sees
/// <see cref="AskUserResolveOutcome.NoPendingCheckpoint"/> and does not resume the conversation again.
/// </remarks>
public sealed class AskUserCheckpointService : IAskUserCheckpointService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IAskUserResponseRegistry _registry;
    private readonly IConversationStore _conversationStore;
    private readonly IAskUserCheckpointResumer? _resumer;
    private readonly ILogger<AskUserCheckpointService> _logger;

    // Per-conversation async gates serialise the durable claim-and-clear so a duplicate or
    // cross-client submission cannot double-resume. Keyed on the normalised conversation id.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _claimGates =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Creates the checkpoint service.
    /// </summary>
    /// <param name="registry">Live in-memory waiter registry (fast path when the original task survives).</param>
    /// <param name="conversationStore">Durable conversation state that carries the pending prompt.</param>
    /// <param name="logger">Diagnostics logger.</param>
    /// <param name="resumer">Optional continuation dispatcher used for the restart/reload resume path.
    /// When omitted, a checkpoint can still be claimed and cleared but no continuation is dispatched
    /// (used by unit tests and hosts that have not wired resume).</param>
    public AskUserCheckpointService(
        IAskUserResponseRegistry registry,
        IConversationStore conversationStore,
        ILogger<AskUserCheckpointService> logger,
        IAskUserCheckpointResumer? resumer = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(conversationStore);
        ArgumentNullException.ThrowIfNull(logger);
        _registry = registry;
        _conversationStore = conversationStore;
        _logger = logger;
        _resumer = resumer;
    }

    /// <inheritdoc />
    public async Task<AskUserResolveOutcome> ResolveAsync(
        ConversationId conversationId,
        string requestId,
        AskUserResponse response,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (string.IsNullOrWhiteSpace(requestId))
            return AskUserResolveOutcome.NoPendingCheckpoint;

        var normalizedRequestId = requestId.Trim();

        // Fast path: a live waiter still exists (no restart happened). Complete it in-process and
        // let the tool's own finally clear the durable copy. This keeps the common case cheap and
        // avoids dispatching a redundant continuation.
        if (_registry.TryComplete(conversationId, normalizedRequestId, response))
            return AskUserResolveOutcome.LiveCompleted;

        // Slow path: no live waiter. Resolve against durable state under a per-conversation gate so
        // the claim-and-clear is atomic against concurrent submissions.
        var gate = _claimGates.GetOrAdd(conversationId.Value, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-check the live registry inside the gate: a waiter may have raced in (or the durable
            // claim may already have completed on another thread that then registered nothing).
            if (_registry.TryComplete(conversationId, normalizedRequestId, response))
                return AskUserResolveOutcome.LiveCompleted;

            var conversation = await _conversationStore.GetAsync(conversationId, cancellationToken).ConfigureAwait(false);
            if (conversation is null || string.IsNullOrEmpty(conversation.PendingAskUserJson))
                return AskUserResolveOutcome.NoPendingCheckpoint;

            AskUserRequest? pending;
            try
            {
                pending = JsonSerializer.Deserialize<AskUserRequest>(conversation.PendingAskUserJson, JsonOptions);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex,
                    "Discarding unparseable pending ask_user checkpoint for conversation {ConversationId}; clearing it.",
                    conversationId);
                pending = null;
            }

            if (pending is null)
            {
                // Corrupt/legacy row: clear it so it never swallows ordinary messages, and report
                // no pending checkpoint so the caller does not treat this as a resumed continuation.
                conversation.PendingAskUserJson = null;
                await _conversationStore.SaveAsync(conversation, cancellationToken).ConfigureAwait(false);
                return AskUserResolveOutcome.NoPendingCheckpoint;
            }

            if (!string.Equals(pending.RequestId, normalizedRequestId, StringComparison.Ordinal))
            {
                // Stale request id from a client that missed a newer prompt: leave the current
                // checkpoint untouched so the live prompt survives.
                _logger.LogInformation(
                    "ask_user response for conversation {ConversationId} carried stale request id {RequestId}; current pending is {PendingRequestId}.",
                    conversationId, normalizedRequestId, pending.RequestId);
                return AskUserResolveOutcome.RequestIdMismatch;
            }

            // Atomically claim: clear the durable checkpoint first so a concurrent submission that
            // acquires the gate next observes no pending prompt and resolves to NoPendingCheckpoint.
            conversation.PendingAskUserJson = null;
            await _conversationStore.SaveAsync(conversation, cancellationToken).ConfigureAwait(false);

            var normalizedResponse = response with { RequestId = pending.RequestId };

            if (_resumer is not null)
            {
                await _resumer.ResumeAsync(pending, normalizedResponse, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _logger.LogWarning(
                    "Claimed durable ask_user checkpoint for conversation {ConversationId} but no resumer is wired; the conversation will not auto-continue.",
                    conversationId);
            }

            return AskUserResolveOutcome.ResumedFromCheckpoint;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> TryResolveInboundTextAsync(
        ConversationId conversationId,
        string freeFormText,
        CancellationToken cancellationToken = default)
    {
        // Prefer the live registry's request id when a waiter is active; otherwise fall back to the
        // durable checkpoint so an inbound message after a restart is still captured as a response
        // (and not silently swallowed nor mis-dispatched as a fresh turn).
        string? requestId = _registry.TryGetPendingRequestId(conversationId, out var liveRequestId)
            ? liveRequestId
            : await ReadDurableRequestIdAsync(conversationId, cancellationToken).ConfigureAwait(false);

        if (requestId is null)
            return false;

        var response = new AskUserResponse
        {
            RequestId = requestId,
            FreeFormText = freeFormText
        };

        var outcome = await ResolveAsync(conversationId, requestId, response, cancellationToken).ConfigureAwait(false);
        return outcome is AskUserResolveOutcome.LiveCompleted or AskUserResolveOutcome.ResumedFromCheckpoint;
    }

    private async Task<string?> ReadDurableRequestIdAsync(ConversationId conversationId, CancellationToken cancellationToken)
    {
        var conversation = await _conversationStore.GetAsync(conversationId, cancellationToken).ConfigureAwait(false);
        if (conversation is null || string.IsNullOrEmpty(conversation.PendingAskUserJson))
            return null;

        try
        {
            var pending = JsonSerializer.Deserialize<AskUserRequest>(conversation.PendingAskUserJson, JsonOptions);
            return pending?.RequestId;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
