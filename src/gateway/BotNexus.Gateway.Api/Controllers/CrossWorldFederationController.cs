using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Federation;
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

        // Phase 4 / F-3 (receiver branch): resolve or create the local conversation+session pair.
        var resolveResult = await ResolveSessionAsync(request, targetAgentId, cancellationToken).ConfigureAwait(false);
        if (resolveResult.Error is { } error)
            return error;

        var session = resolveResult.Session!;
        var conversation = resolveResult.Conversation!;
        var sessionId = session.SessionId;

        session.AddEntry(new SessionEntry
        {
            Role = MessageRole.User,
            Content = request.Message
        });

        // Persist BEFORE invoking the supervisor — same race fix the sender PR (#548) applies.
        // A concurrent reader (background flush, portal page-load) must never see this session
        // with ConversationId == null. The conversation is also pinned to ActiveSessionId so the
        // portal can render it as in-flight.
        await sessionStore.SaveAsync(session, cancellationToken).ConfigureAwait(false);

        conversation.ActiveSessionId = sessionId;
        await conversationStore.SaveAsync(conversation, cancellationToken).ConfigureAwait(false);

        try
        {
            var handle = await supervisor.GetOrCreateAsync(targetAgentId, sessionId, cancellationToken).ConfigureAwait(false);
            var response = await handle.PromptAsync(request.Message, cancellationToken).ConfigureAwait(false);
            session.AddEntry(new SessionEntry
            {
                Role = MessageRole.Assistant,
                Content = response.Content ?? string.Empty
            });
            await sessionStore.SaveAsync(session, cancellationToken).ConfigureAwait(false);

            // Leave the session Active so the sender can continue the exchange by supplying
            // RemoteSessionId on the next turn; clear ActiveSessionId so portal stops rendering
            // the conversation as "in flight" while the sender pauses between turns.
            await ClearActiveSessionAsync(conversation, sessionId, CancellationToken.None).ConfigureAwait(false);

            return Ok(new CrossWorldRelayResponse
            {
                Response = response.Content ?? string.Empty,
                Status = "active",
                SessionId = sessionId.Value
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Cross-world relay failed for session '{SessionId}' on agent '{TargetAgentId}'.",
                sessionId, targetAgentId);
            session.Status = GatewaySessionStatus.Sealed;
            session.Metadata["error"] = ex.Message;
            await sessionStore.SaveAsync(session, CancellationToken.None).ConfigureAwait(false);
            await ClearActiveSessionAsync(conversation, sessionId, CancellationToken.None).ConfigureAwait(false);
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

            if (existing.Session.ConversationId is not { } existingConversationId)
                return ResolveResult.Fail(Conflict(new { error = $"RemoteSessionId '{request.RemoteSessionId}' has no bound conversation — refuse to reuse." }));

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
        session.Session.ConversationId = conversation.ConversationId;
        session.SessionType = SessionType.AgentAgent;
        session.ChannelType = CrossWorldChannel;
        session.CallerId = null;
        session.Status = GatewaySessionStatus.Active;
        session.Participants.Clear();
        session.Participants.Add(new SessionParticipant
        {
            CitizenId = CitizenId.Of(AgentId.From(request.SourceAgentId)),
            Role = "initiator"
        });
        session.Participants.Add(new SessionParticipant
        {
            CitizenId = CitizenId.Of(targetAgentId),
            Role = "target"
        });
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
    /// Clears <see cref="Conversation.ActiveSessionId"/> only if it still points at the
    /// session this call started. Avoids clobbering a newer concurrent relay's pointer.
    /// Failure is swallowed — ActiveSessionId is a diagnostic, not a correctness contract.
    /// </summary>
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

    private readonly record struct ResolveResult(GatewaySession? Session, Conversation? Conversation, ActionResult<CrossWorldRelayResponse>? Error)
    {
        public static ResolveResult Ok(GatewaySession session, Conversation conversation)
            => new(session, conversation, null);

        public static ResolveResult Fail(ActionResult<CrossWorldRelayResponse> error)
            => new(null, null, error);
    }
}
