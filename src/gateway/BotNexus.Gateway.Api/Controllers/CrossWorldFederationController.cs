using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Federation;
using BotNexus.Gateway.Tools;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using GatewaySessionStatus = BotNexus.Gateway.Abstractions.Models.SessionStatus;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// Receives federated cross-world relay messages from peer gateways. Each call creates
/// (or reuses) a local <see cref="Conversation"/> via <see cref="IConversationStore"/>
/// and pins the receiver-side session to it BEFORE invoking the target agent — mirroring
/// the persist-before-prompt shape proven on the sender side in PR #548 / F-3.
/// </summary>
/// <remarks>
/// <para>
/// The receiver-side conversation is owned by the local target agent. Source identity
/// (<c>SourceWorldId</c>, <c>SourceAgentId</c>, sender-side conversation/session ids) is
/// stashed on <see cref="Conversation.Metadata"/> only — it is NOT promoted into
/// <see cref="Conversation.Purpose"/> or <see cref="Conversation.Title"/>, both of which
/// are rendered into the target agent's system prompt by
/// <c>SystemPromptBuilder.BuildConversationContextSection</c>. Promoting caller-controlled
/// strings into those positions is an XPIA (cross-prompt injection) vector.
/// </para>
/// <para>
/// <strong>Session reuse:</strong> the sender may supply <c>RemoteSessionId</c> from a
/// previous turn to continue an in-flight cross-world exchange. The receiver validates
/// that the supplied session id (a) exists, (b) is owned by the target agent, (c) is a
/// cross-world AgentAgent session, and (d) was originally minted for the same
/// <c>SourceWorldId</c>/<c>SourceAgentId</c>. Any mismatch returns <c>409 Conflict</c>;
/// missing supplied id returns <c>404</c>. Without the id, a fresh session +
/// conversation are minted.
/// </para>
/// </remarks>
[ApiController]
[Route("api/federation/cross-world")]
public sealed class CrossWorldFederationController(
    IAgentRegistry registry,
    IAgentSupervisor supervisor,
    ISessionStore sessionStore,
    IConversationStore conversationStore,
    ISessionWriteLock sessionWriteLock,
    CrossWorldInboundAuthService inboundAuthService,
    IOptionsMonitor<PlatformConfig> platformConfig,
    ILogger<CrossWorldFederationController> logger) : ControllerBase
{
    private const string ConversationTitle = "Cross-world agent exchange";
    private static readonly ChannelKey CrossWorldChannel = ChannelKey.From("cross-world");

    private readonly string _localWorldId = WorldIdentityResolver.Resolve(platformConfig.CurrentValue).Id;

    /// <summary>
    /// Accepts a relayed message from a peer gateway, runs it through the local target agent,
    /// and returns the agent's response together with the receiver-local session id (which the
    /// sender stores as <c>RemoteSessionId</c> for subsequent turns).
    /// </summary>
    [HttpPost("relay")]
    public async Task<ActionResult<CrossWorldRelayResponse>> RelayAsync(
        [FromBody] CrossWorldRelayRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.SourceWorldId))
            return BadRequest(new { error = "sourceWorldId is required." });
        if (string.IsNullOrWhiteSpace(request.SourceAgentId))
            return BadRequest(new { error = "sourceAgentId is required." });
        if (string.IsNullOrWhiteSpace(request.TargetAgentId))
            return BadRequest(new { error = "targetAgentId is required." });
        if (string.IsNullOrWhiteSpace(request.Message))
            return BadRequest(new { error = "message is required." });

        var targetAgentId = AgentId.From(request.TargetAgentId);

        // Auth BEFORE agent-existence lookup — without this ordering, an unauthenticated caller can
        // probe `registry.Contains(targetAgentId)` and distinguish "registered → 401" from
        // "unregistered → 404", enumerating local agent ids without ever presenting a valid
        // X-Cross-World-Key (PR #549 critique sweep — security LOW finding).
        var presentedApiKey = Request.Headers.TryGetValue("X-Cross-World-Key", out var keyHeader)
            ? keyHeader.ToString()
            : null;
        if (!inboundAuthService.TryAuthorize(request.SourceWorldId, targetAgentId, presentedApiKey, out var authError))
            return Unauthorized(new { error = authError });

        if (!registry.Contains(targetAgentId))
            return NotFound(new { error = $"Target agent '{request.TargetAgentId}' is not registered." });

        // Per-session lock holds across resolve → write → prompt → reload → save → (seal-on-error).
        // The single critical race that motivated #551 is two concurrent senders supplying the
        // SAME RemoteSessionId: without the lock they can interleave their per-turn
        // active-exchange-id writes and satisfy each other's freshness gate with the wrong
        // payload. For supplied RemoteSessionId we acquire the lock BEFORE ResolveSessionAsync
        // so a concurrent caller can never observe the session as Active in resolve and then
        // sneak past after the first caller seals it (rubber-duck critique #3). For fresh
        // sessions we resolve first to mint the new sessionId, then acquire for uniformity —
        // the lock is functionally a no-op in that branch because no other caller can possibly
        // hold the same just-minted id. The whole try/catch including the error-seal SaveAsync
        // is INSIDE the lock scope so a second caller cannot sneak in between the failure and
        // the seal persist (rubber-duck critique #4).
        if (!string.IsNullOrWhiteSpace(request.RemoteSessionId))
        {
            var suppliedSessionId = SessionId.From(request.RemoteSessionId);
            await using var lease = await sessionWriteLock
                .AcquireAsync(suppliedSessionId, cancellationToken)
                .ConfigureAwait(false);

            var resolved = await ResolveSessionAsync(request, targetAgentId, cancellationToken).ConfigureAwait(false);
            if (resolved.Error is { } resolveError)
                return resolveError;

            return await ExecuteRelayAsync(request, resolved.GatewaySession!, resolved.Conversation!, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var resolved = await ResolveSessionAsync(request, targetAgentId, cancellationToken).ConfigureAwait(false);
            if (resolved.Error is { } resolveError)
                return resolveError;

            await using var lease = await sessionWriteLock
                .AcquireAsync(resolved.GatewaySession!.SessionId, cancellationToken)
                .ConfigureAwait(false);

            return await ExecuteRelayAsync(request, resolved.GatewaySession!, resolved.Conversation!, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs the actual relay turn — append user entry, persist, prompt the target, reload,
    /// consume the completion gate, persist the outcome. Invoked under the per-session lock
    /// acquired by <see cref="RelayAsync"/>. The lock holds across <c>PromptAsync</c> by
    /// design — the freshness gate in <see cref="AgentExchangeCompletionGate"/> requires that
    /// the tool's persisted finish payload be readable in the same logical turn that wrote
    /// the active id, and any concurrent caller would race the freshness invariant.
    /// </summary>
    private async Task<ActionResult<CrossWorldRelayResponse>> ExecuteRelayAsync(
        CrossWorldRelayRequest request,
        GatewaySession session,
        Conversation conversation,
        CancellationToken cancellationToken)
    {
        var sessionId = session.SessionId;

        // Idempotency guard (#566): if the sender retries with the same TurnId and the
        // last USER history entry already carries that key, skip the append. This prevents
        // duplicate user-turn entries when the sender cancels mid-turn and retries with
        // the same RemoteSessionId. The guard is a no-op when TurnId is null (legacy
        // senders or single-turn exchanges without retry semantics).
        // Note: check the last USER entry (not the absolute last entry, which may be an
        // assistant response from the previous turn's completion).
        var alreadyAppended = !string.IsNullOrEmpty(request.TurnId)
            && session.GetHistorySnapshot()
                .LastOrDefault(e => e.Role == MessageRole.User)
                    is { } lastUserEntry
            && lastUserEntry.TurnIdempotencyKey == request.TurnId;

        if (!alreadyAppended)
        {
            session.AddEntry(new SessionEntry
            {
                Role = MessageRole.User,
                Content = request.Message,
                TurnIdempotencyKey = request.TurnId
            });
        }

        // Persist BEFORE invoking the supervisor — same race fix the sender PR (#548) applies.
        // A concurrent reader (background flush, portal page-load) must never see this session
        // with ConversationId == null. The conversation is also pinned to ActiveSessionId so the
        // portal can render it as in-flight.
        await sessionStore.SaveAsync(session, cancellationToken).ConfigureAwait(false);

        conversation.ActiveSessionId = sessionId;
        await conversationStore.SaveAsync(conversation, cancellationToken).ConfigureAwait(false);

        try
        {
            // Phase 8 (F-11) cross-world receiver: pin a fresh active-exchange id and clear stale
            // finish payload BEFORE invoking the agent so a previous turn's payload cannot satisfy
            // the equality gate in this turn. The receiver is single-prompt (no loop) but receivers
            // can reuse a session across many sender turns, so the per-call clear is mandatory.
            var exchangeId = Guid.NewGuid().ToString("N");
            AgentExchangeCompletionGate.PrepareTurn(session.Metadata, exchangeId);
            await sessionStore.SaveAsync(session, cancellationToken).ConfigureAwait(false);

            var handle = await supervisor.GetOrCreateAsync(session.AgentId, sessionId, cancellationToken).ConfigureAwait(false);
            var response = await handle.PromptAsync(request.Message, cancellationToken).ConfigureAwait(false);
            session.AddEntry(new SessionEntry
            {
                Role = MessageRole.Assistant,
                Content = response.Content ?? string.Empty
            });

            // Reload the session — the FinishAgentExchangeTool (if invoked by the target agent
            // during the turn) writes its payload through its own ISessionStore handle, so the
            // in-memory copy here may be stale.
            var refreshed = await sessionStore.GetAsync(sessionId, cancellationToken).ConfigureAwait(false)
                ?? session;

            // Same authoritative gate the sender uses (AgentExchangeCompletionGate) so both
            // call sites share one implementation — see plan-vs-impl critique NB-2 / bug-hunt
            // missing-test #2 on PR #553.
            var exchangeFinished = AgentExchangeCompletionGate.TryConsume(
                response,
                refreshed.Metadata,
                exchangeId,
                out var finishReason,
                out var finishSummary);

            // Clear the active-exchange id regardless of outcome so a follow-up turn for the same
            // session starts from a clean slate.
            session.ExchangeCompletion = session.ExchangeCompletion is { } activeState
                ? activeState with { ActiveExchangeId = null }
                : null;
            if (exchangeFinished)
            {
                // Echo the consumed payload onto the persisted session metadata for diagnostics
                // and for any downstream walker that lists sessions for this conversation.
                session.ExchangeCompletion = (session.ExchangeCompletion ?? new AgentExchangeCompletionState()) with
                {
                    FinishedExchangeId = exchangeId,
                    FinishedReason = finishReason ?? (session.ExchangeCompletion?.FinishedReason),
                    FinishedSummary = string.IsNullOrEmpty(finishSummary) ? null : finishSummary
                };
                // State-machine closure (bug-hunt MEDIUM-3 on PR #553): once the target agent has
                // explicitly signalled completion via finish_agent_exchange, the receiver-side
                // session is terminated. ResolveSessionAsync's sealed-session guard then refuses
                // any further sender turn that tries to reuse this RemoteSessionId, so the
                // sender cannot accidentally continue an exchange the target said was done.
                session.Status = GatewaySessionStatus.Sealed;
            }
            await sessionStore.SaveAsync(session, cancellationToken).ConfigureAwait(false);

            // P9-C: when the exchange is ending — either because the target invoked
            // finish_agent_exchange (exchangeFinished) OR the sender signalled this is the
            // final turn via CloseAfterResponse (single-shot / max-turns) — archive the
            // receiver-side conversation. Otherwise just clear ActiveSessionId so the portal
            // stops rendering it as "in flight" while the sender pauses between turns.
            //
            // Seal-when-archived rule (#626): whenever ArchiveOnExchangeEndAsync fires (for
            // either reason) we also seal the session so the state machine is consistent.
            // An Archived conversation with an Active session allows a follow-up sender relay
            // to resurrect ActiveSessionId on an Archived conversation, which is structurally
            // inconsistent and portal-visible. The exchangeFinished branch already seals above;
            // we seal here for the CloseAfterResponse-only path.
            if (exchangeFinished || request.CloseAfterResponse)
            {
                if (!exchangeFinished)
                {
                    // CloseAfterResponse forced archive but the target did not invoke
                    // finish_agent_exchange. Seal the session now so the receiver-side
                    // state machine matches: Archived conversation  +  Sealed session.
                    session.Status = GatewaySessionStatus.Sealed;
                    await sessionStore.SaveAsync(session, cancellationToken).ConfigureAwait(false);
                }
                await ArchiveOnExchangeEndAsync(conversation, sessionId, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                await ClearActiveSessionAsync(conversation, sessionId, CancellationToken.None).ConfigureAwait(false);
            }

            return Ok(new CrossWorldRelayResponse
            {
                Response = response.Content ?? string.Empty,
                // Surface the finish state on the wire so the sender's loop sees a non-"active"
                // status when the target has explicitly closed the exchange.
                // After #626 fix: CloseAfterResponse also seals, so surface "sealed" for that path too.
                Status = (exchangeFinished || request.CloseAfterResponse) ? "sealed" : "active",
                SessionId = sessionId.Value,
                ExchangeFinished = exchangeFinished,
                FinishReason = exchangeFinished ? finishReason : null,
                FinishSummary = exchangeFinished ? finishSummary : null
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // #553: caller-initiated cancellation must NOT seal the session. Sealing here would
            // poison the session for any sender retry — the sender's next call would hit the
            // sealed-session 409 guard in ResolveSessionAsync and the exchange would be
            // permanently broken by a transient client-side timeout / abort. Rethrow without
            // touching session.Status; the session remains Active and the sender can retry
            // with the same RemoteSessionId. Note: ActiveSessionId is intentionally LEFT
            // pinned so a fast retry sees the conversation as in-flight rather than briefly
            // flipping to "idle" then back to "active" in the portal.
            //
            // Guard is `when (cancellationToken.IsCancellationRequested)` so OCEs raised by
            // any OTHER token (e.g. an internal timeout linked downstream) still fall through
            // to the catch-all and seal — those are genuine failures, not caller intent.
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Cross-world relay failed for session '{SessionId}' on agent '{TargetAgentId}'.",
                sessionId, session.AgentId);
            session.ExchangeCompletion = session.ExchangeCompletion is { } activeState
                ? activeState with { ActiveExchangeId = null }
                : null;
            session.Status = GatewaySessionStatus.Sealed;
            session.Metadata["error"] = ex.Message;
            await sessionStore.SaveAsync(session, CancellationToken.None).ConfigureAwait(false);
            // P9-C: failed exchanges are terminal — archive rather than just clearing the
            // pointer. The session is already sealed; the conversation should not linger as
            // Active in portal/list APIs after a failure.
            await ArchiveOnExchangeEndAsync(conversation, sessionId, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Resolves the conversation+session pair for this relay. Either reuses the caller-supplied
    /// <c>RemoteSessionId</c> (after validating it really belongs to the same source) or mints
    /// a fresh pair.
    /// </summary>
    private async Task<ResolveResult> ResolveSessionAsync(
        CrossWorldRelayRequest request,
        AgentId targetAgentId,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.RemoteSessionId))
        {
            var supplied = SessionId.From(request.RemoteSessionId);
            var existing = await sessionStore.GetAsync(supplied, cancellationToken).ConfigureAwait(false);
            if (existing is null)
                return ResolveResult.Fail(NotFound(new { error = $"RemoteSessionId '{request.RemoteSessionId}' was not found on this gateway." }));

            if (!OwnedByRequester(existing, targetAgentId, request, out var mismatchReason))
                return ResolveResult.Fail(Conflict(new { error = $"RemoteSessionId '{request.RemoteSessionId}' rejected: {mismatchReason}" }));

            // Refuse to reactivate a sealed session — the previous turn failed and was sealed
            // deliberately. Reopening would mix new turns into a terminated transcript and might
            // mask the original failure (PR #549 critique sweep — bug-hunt BLOCKING #5).
            if (existing.Status == GatewaySessionStatus.Sealed)
                return ResolveResult.Fail(Conflict(new { error = $"RemoteSessionId '{request.RemoteSessionId}' is sealed and cannot be reused — start a new cross-world exchange." }));

            if (!existing.ConversationId.IsInitialized())
                return ResolveResult.Fail(Conflict(new { error = $"RemoteSessionId '{request.RemoteSessionId}' has no bound conversation — refuse to reuse." }));
            var existingConversationId = existing.ConversationId;

            var existingConv = await conversationStore.GetAsync(existingConversationId, cancellationToken).ConfigureAwait(false);
            if (existingConv is null)
                return ResolveResult.Fail(Conflict(new { error = $"RemoteSessionId '{request.RemoteSessionId}' references missing conversation." }));

            existing.Status = GatewaySessionStatus.Active;
            return ResolveResult.Ok(existing, existingConv);
        }

        // Fresh mint path.
        // Title is a CONSTANT — never caller-derived. SystemPromptBuilder.cs:601 injects Title
        // into the target system prompt; caller-controlled text there is an XPIA vector.
        // Initiator is null because cross-world citizens don't resolve in the local registries;
        // source identity is preserved on Metadata only.
        var conversation = await conversationStore.CreateAsync(new Conversation
        {
            ConversationId = ConversationId.Create(),
            AgentId = targetAgentId,
            Kind = ConversationKind.AgentAgent,
            Initiator = null,
            Title = ConversationTitle,
            Purpose = null,
            Status = ConversationStatus.Active,
            Metadata =
            {
                ["sourceWorldId"] = request.SourceWorldId,
                ["sourceAgentId"] = request.SourceAgentId,
                ["sourceConversationId"] = request.ConversationId,
                ["sourceSessionId"] = request.SourceSessionId,
                ["targetWorldId"] = _localWorldId,
                ["channelType"] = CrossWorldChannel.Value
            }
        }, cancellationToken).ConfigureAwait(false);

        var sessionId = SessionId.Create();
        var session = await sessionStore.GetOrCreateAsync(sessionId, targetAgentId, cancellationToken).ConfigureAwait(false);
        session.ConversationId = conversation.ConversationId;
        session.SessionType = SessionType.AgentAgent;
        session.ChannelType = CrossWorldChannel;
        session.CallerId = null;
        session.Status = GatewaySessionStatus.Active;

        // P9-F: Participants live on the Conversation, not the Session. Register the
        // source-side initiator and the local target as conversation participants so the
        // local IConversationStore.ListForCitizenAsync surfaces this exchange to both
        // species.
        await conversationStore.AddParticipantsAsync(
            conversation.ConversationId,
            [
                new SessionParticipant
                {
                    CitizenId = CitizenId.Of(AgentId.From(request.SourceAgentId)),
                    Role = "initiator"
                },
                new SessionParticipant
                {
                    CitizenId = CitizenId.Of(targetAgentId),
                    Role = "target"
                }
            ],
            cancellationToken).ConfigureAwait(false);

        session.Metadata["sourceWorldId"] = request.SourceWorldId;
        session.Metadata["sourceAgentId"] = request.SourceAgentId;
        session.Metadata["sourceConversationId"] = request.ConversationId;
        session.Metadata["sourceSessionId"] = request.SourceSessionId;
        session.Metadata["targetWorldId"] = _localWorldId;
        session.Metadata["conversationId"] = conversation.ConversationId.Value;

        return ResolveResult.Ok(session, conversation);
    }

    /// <summary>
    /// Validates that a caller-supplied <c>RemoteSessionId</c> truly belongs to the
    /// <c>(SourceWorldId, SourceAgentId, TargetAgentId)</c> triple the caller asserts. Without
    /// this check World A could relay through any session id it can guess and impersonate
    /// other worlds' transcripts.
    /// </summary>
    private static bool OwnedByRequester(
        GatewaySession existing,
        AgentId targetAgentId,
        CrossWorldRelayRequest request,
        out string reason)
    {
        if (existing.AgentId != targetAgentId)
        {
            reason = $"session is owned by agent '{existing.AgentId}', not target '{targetAgentId}'.";
            return false;
        }

        if (existing.ChannelType is null || !existing.ChannelType.Equals(CrossWorldChannel))
        {
            reason = "session is not a cross-world session.";
            return false;
        }

        if (existing.SessionType is null || !existing.SessionType.Equals(SessionType.AgentAgent))
        {
            reason = "session is not an agent-agent session.";
            return false;
        }

        if (!StringEquals(MetadataString(existing.Metadata, "sourceWorldId"), request.SourceWorldId))
        {
            reason = "session sourceWorldId does not match request.";
            return false;
        }

        if (!StringEquals(MetadataString(existing.Metadata, "sourceAgentId"), request.SourceAgentId))
        {
            reason = "session sourceAgentId does not match request.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// Extracts a string-typed metadata value. After a disk round-trip via
    /// <c>SqliteSessionStore</c>, string values are boxed as
    /// <see cref="System.Text.Json.JsonElement"/> rather than <see cref="string"/>; a naive
    /// <c>as string</c> cast silently returns <c>null</c> and the <see cref="OwnedByRequester"/>
    /// checks then 409 every legitimate reuse call. Matches the pattern in
    /// <c>AgentConverseTool.ResolveCallChainAsync</c> and <c>PreCompactionMemoryFlusher.
    /// GetLastFlushCycle</c> (PR #549 critique sweep — bug-hunt BLOCKING #1).
    /// </summary>
    private static string? MetadataString(IDictionary<string, object?> metadata, string key)
    {
        if (!metadata.TryGetValue(key, out var value) || value is null)
            return null;

        return value switch
        {
            string s => s,
            System.Text.Json.JsonElement element when element.ValueKind == System.Text.Json.JsonValueKind.String
                => element.GetString(),
            _ => null
        };
    }

    private static bool StringEquals(string? left, string? right)
        => string.Equals(left, right, StringComparison.Ordinal);

    /// <summary>
    /// Archives the cross-world A↔A <see cref="Conversation"/> when its receiver-side
    /// exchange terminates (Phase 9 / P9-C). Mirror of
    /// <c>AgentExchangeService.ArchiveOnExchangeEndAsync</c>: A↔A conversations are bounded
    /// by their exchange — when the exchange ends, the conversation is done.
    /// </summary>
    /// <remarks>
    /// Subsumes <see cref="ClearActiveSessionAsync"/> on the success-finished and error
    /// paths because <see cref="IConversationStore.ArchiveAsync(ConversationId, CancellationToken)"/> atomically sets
    /// <c>Status = Archived</c> AND <c>ActiveSessionId = null</c>. Strict pointer guard:
    /// only archives when latest <c>ActiveSessionId</c> equals
    /// <paramref name="expectedSessionId"/> (any other state — null, different SessionId,
    /// already-Archived — is skipped). Always uses <see cref="CancellationToken.None"/>
    /// so caller cancellation cannot skip the archive after the session is sealed.
    /// Helper-level dedup with <see cref="AgentExchangeService"/>'s identical method is
    /// intentional for P9-C scope; W-3 may consolidate into a shared lifecycle service.
    /// </remarks>
    private async Task ArchiveOnExchangeEndAsync(
        Conversation conversation,
        SessionId expectedSessionId,
        CancellationToken cancellationToken)
    {
        try
        {
            var latest = await conversationStore.GetAsync(conversation.ConversationId, cancellationToken).ConfigureAwait(false);
            if (latest is null)
                return;
            if (latest.Status == ConversationStatus.Archived)
                return;
            if (latest.ActiveSessionId != expectedSessionId)
            {
                logger.LogDebug(
                    "Skipping archive for cross-world conversation '{ConversationId}': ActiveSessionId is '{Current}', expected '{Expected}'.",
                    conversation.ConversationId, latest.ActiveSessionId, expectedSessionId);
                return;
            }
            await conversationStore.ArchiveAsync(conversation.ConversationId, "agent-exchange-completion", expectedSessionId.Value, "system", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to archive cross-world conversation '{ConversationId}' after exchange end.",
                conversation.ConversationId);
        }
    }

    /// <summary>
    /// Clears <see cref="Conversation.ActiveSessionId"/> only if it still points at the
    /// session this call started. Avoids clobbering a newer concurrent relay's pointer.
    /// Failure is swallowed — ActiveSessionId is a diagnostic, not a correctness contract.
    /// </summary>
    /// <remarks>
    /// P9-C: still required for the non-final relay branch (exchange continuing — sender
    /// will resume with the same <c>RemoteSessionId</c>). The terminal-archive case is
    /// covered by <see cref="ArchiveOnExchangeEndAsync"/>.
    /// </remarks>
    private async Task ClearActiveSessionAsync(
        Conversation conversation,
        SessionId expectedSessionId,
        CancellationToken cancellationToken)
    {
        try
        {
            var latest = await conversationStore.GetAsync(conversation.ConversationId, cancellationToken).ConfigureAwait(false);
            if (latest is null)
                return;
            if (latest.ActiveSessionId != expectedSessionId)
            {
                logger.LogDebug(
                    "Skipping ActiveSessionId clear for conversation '{ConversationId}': pointer is now '{Current}', expected '{Expected}'.",
                    conversation.ConversationId, latest.ActiveSessionId, expectedSessionId);
                return;
            }
            latest.ActiveSessionId = null;
            latest.UpdatedAt = DateTimeOffset.UtcNow;
            await conversationStore.SaveAsync(latest, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to clear ActiveSessionId on cross-world conversation '{ConversationId}' after exchange.",
                conversation.ConversationId);
        }
    }

    private readonly record struct ResolveResult(GatewaySession? GatewaySession, Conversation? Conversation, ActionResult<CrossWorldRelayResponse>? Error)
    {
        public static ResolveResult Ok(GatewaySession session, Conversation conversation)
            => new(session, conversation, null);

        public static ResolveResult Fail(ActionResult<CrossWorldRelayResponse> error)
            => new(null, null, error);
    }
}
