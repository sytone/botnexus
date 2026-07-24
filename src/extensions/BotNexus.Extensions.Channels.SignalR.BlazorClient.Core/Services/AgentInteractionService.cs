using System.Net;
using Microsoft.Extensions.Logging;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// Thin action layer for UI components — sends messages, steers, resets sessions,
/// manages conversations. All state mutations flow via <see cref="IClientStateStore"/>.
/// </summary>
public sealed class AgentInteractionService : IAgentInteractionService
{
    /// <summary>
    /// Default history page size (#1691): open on the most-recent 20 messages and page backwards
    /// 20 at a time on scroll-up. Used by both the initial load and <see cref="LoadMoreHistoryAsync"/>
    /// so desktop and mobile share one paging contract.
    /// </summary>
    public const int DefaultHistoryPageSize = 20;

    private readonly IClientStateStore _store;
    private readonly GatewayHubConnection _hub;
    private readonly IGatewayRestClient _restClient;
    private readonly ILogger<AgentInteractionService> _logger;

    public AgentInteractionService(
        IClientStateStore store,
        GatewayHubConnection hub,
        IGatewayRestClient restClient,
        ILogger<AgentInteractionService> logger)
    {
        _store = store;
        _hub = hub;
        _restClient = restClient;
        _logger = logger;
    }

    /// <summary>
    /// Strips carriage-return and newline characters from a client-supplied value before it is
    /// used in a log message template, preventing log forging / injected log lines
    /// (CodeQL: cs/log-forging). Conversation, agent and sub-agent identifiers arrive as raw
    /// strings from the UI/transport layer here (not the validated domain value-types), so they
    /// are untrusted at this seam. Null-safe.
    /// </summary>
    private static string? Sanitise(string? value) =>
        value?.Replace("\r", string.Empty, StringComparison.Ordinal)
              .Replace("\n", " ", StringComparison.Ordinal);

    private static MediaContentPartDto ToContentPart(DraftAttachment attachment)
    {
        if (attachment.MimeType.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
            return new MediaContentPartDto { MimeType = attachment.MimeType, FileName = attachment.FileName, Text = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(attachment.Base64Data)) };
        return new MediaContentPartDto { MimeType = attachment.MimeType, FileName = attachment.FileName, Base64Data = attachment.Base64Data };
    }

    // ── Messaging ─────────────────────────────────────────────────────────

    public Task SendMessageAsync(string agentId, string content)
        => SendMessageAsync(agentId, content, []);

    /// <inheritdoc />
    public async Task SendMessageAsync(string agentId, string content, IReadOnlyList<DraftAttachment> attachments)
    {
        var agent = _store.GetAgent(agentId);
        if (agent is null) return;

        // Ensure we have an active conversation
        if (agent.ActiveConversationId is null)
        {
            var convId = await CreateConversationAsync(agentId, title: null, select: true);
            if (convId is null)
            {
                AppendError(agentId, "Failed to create conversation before sending.");
                return;
            }
            agent = _store.GetAgent(agentId)!;
        }

        var convIdNow = agent.ActiveConversationId!;
        var conv = _store.GetConversation(convIdNow);
        if (conv is null) return;

        // Route the local user echo through the single append path so every call site
        // (send, steer, redirect) adds the user message and notifies identically.
        AppendUserMessage(agentId, content);

        try
        {
            // Always pass the conversation ID — the router handles direct lookup without binding scan.
            // Removed the IsDefault special-case that previously caused duplicate thread bindings and double fan-out.
            var result = attachments.Count == 0
                ? await _hub.SendMessageAsync(agentId, agent.ChannelType ?? "signalr", content, convIdNow)
                : await _hub.SendMessageWithMediaAsync(agentId, agent.ChannelType ?? "signalr", content,
                    attachments.Select(ToContentPart).ToArray(), convIdNow);
            _store.RegisterSession(agentId, result.SessionId, result.ChannelType, conversationId: convIdNow);

            // Refresh conversation so ActiveSessionId is current
            await RefreshConversationsForAgentAsync(agentId);
        }
        catch (Exception ex)
        {
            AppendError(agentId, $"Send failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Resolves the (conversationId, sessionId) pair that a conversation-targeted action
    /// (steer, follow-up, abort, redirect, reset, compact) must act on, anchored to the
    /// <em>currently displayed</em> conversation rather than the mutable agent-global
    /// <see cref="AgentState.SessionId"/> fallback.
    /// </summary>
    /// <remarks>
    /// The portal's ChatPanel is keyed only by agent id; the displayed conversation is
    /// <see cref="AgentState.ActiveConversationId"/>. Earlier code routed these actions through
    /// <c>AgentState.ActiveConversationSessionId</c>, which falls back to the agent-global
    /// <c>SessionId</c>. A bulk session refresh could leave that global pointing at an unrelated
    /// conversation's (often idle) session, so a steer landed on the wrong conversation and was
    /// silently dropped. Resolving the session from the displayed conversation's own
    /// <see cref="ConversationState.ActiveSessionId"/> with no global fallback guarantees the
    /// action targets the conversation the user is actually looking at.
    /// Returns <see langword="false"/> when there is no active conversation or it has no bound
    /// session yet — callers should treat that as "nothing to act on" instead of guessing.
    /// </remarks>
    private bool TryResolveActiveConversationTarget(string agentId, out string conversationId, out string sessionId)
    {
        conversationId = string.Empty;
        sessionId = string.Empty;

        var agent = _store.GetAgent(agentId);
        if (agent?.ActiveConversationId is not { } convId)
            return false;
        if (agent.Conversations.GetValueOrDefault(convId) is not { ActiveSessionId: { } sid })
            return false;

        conversationId = convId;
        sessionId = sid;
        return true;
    }

    public async Task SteerAsync(string agentId, string content)
    {
        if (!TryResolveActiveConversationTarget(agentId, out var convId, out var sessionId))
            return;

        AppendUserMessage(agentId, $"🔀 {content}");

        // Add entry to steering queue panel
        var entry = new SteeringEntry(Guid.NewGuid().ToString("N"), content, SteeringEntryKind.Steer, SteeringEntryStatus.Pending);
        _store.AddSteeringEntry(convId, entry);

        try
        {
            var result = await _hub.SteerAsync(agentId, sessionId, content, convId);
            _store.RegisterSession(agentId, result.SessionId, result.ChannelType, conversationId: convId);
            await RefreshConversationsForAgentAsync(agentId);
        }
        catch (Exception ex)
        {
            AppendError(agentId, $"Steer failed: {ex.Message}");
        }
    }

    public async Task FollowUpAsync(string agentId, string content)
    {
        if (!TryResolveActiveConversationTarget(agentId, out var convId, out var sessionId))
            return;

        AppendUserMessage(agentId, content);

        // Add entry to steering queue panel with FollowUp kind
        var entry = new SteeringEntry(Guid.NewGuid().ToString("N"), content, SteeringEntryKind.FollowUp, SteeringEntryStatus.Pending);
        _store.AddSteeringEntry(convId, entry);

        try
        {
            await _hub.FollowUpAsync(agentId, sessionId, content);
        }
        catch (Exception ex)
        {
            AppendError(agentId, $"Follow-up failed: {ex.Message}");
        }
    }

    public async Task AbortAsync(string agentId)
    {
        if (!TryResolveActiveConversationTarget(agentId, out var convId, out var sessionId))
            return;

        try
        {
            await _hub.AbortAsync(agentId, sessionId);
        }
        catch (Exception ex)
        {
            AppendError(agentId, $"Abort failed: {ex.Message}");
        }
        finally
        {
            // #2195: Stop is the user's escape hatch. Even if the gateway never delivers a RunEnded
            // (missed/misrouted event), the local turn-active bracket must clear so the input swaps
            // back to Send and the user is never fully locked out without a page reload. Force-clear
            // the active conversation's run state locally regardless of the hub result.
            ForceClearLocalTurnState(agentId, convId);
        }
    }

    // #2195: unconditionally clear the local run bracket (IsRunActive/IsStreaming/ActiveToolCalls)
    // for the given conversation so the portal recovers from a stuck turn-active state. Safe to call
    // even when the run already ended -- EndRun is idempotent.
    private void ForceClearLocalTurnState(string agentId, string conversationId)
    {
        var agent = _store.GetAgent(agentId);
        if (agent is null)
            return;

        agent.IsStreaming = false;
        agent.ProcessingStage = null;

        if (agent.Conversations.GetValueOrDefault(conversationId) is { } conv)
            conv.StreamState.EndRun();

        _store.NotifyChanged();
    }

    // ── Session management ────────────────────────────────────────────────


    public async Task InterruptAndSteerAsync(string agentId, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        if (!TryResolveActiveConversationTarget(agentId, out _, out var sessionId))
            return;

        AppendUserMessage(agentId, "[redirect] " + message);

        try
        {
            var delivered = await _hub.InterruptAndSteerAsync(agentId, sessionId, message);
            if (!delivered)
                AppendError(agentId, "Interrupt not delivered - agent was not running.");
        }
        catch (Exception ex)
        {
            AppendError(agentId, "Interrupt and steer failed: " + ex.Message);
        }
    }
    public async Task ResetSessionAsync(string agentId)
    {
        if (!TryResolveActiveConversationTarget(agentId, out _, out var sessionId))
            return;

        try
        {
            await _hub.ResetSessionAsync(agentId, sessionId);
            // Server will send SessionReset event; GatewayEventHandler handles it
        }
        catch (Exception ex)
        {
            AppendError(agentId, $"Reset failed: {ex.Message}");
        }
    }

    public async Task<CompactSessionResult?> CompactSessionAsync(string agentId)
    {
        if (!TryResolveActiveConversationTarget(agentId, out var convId, out var sessionId))
            return null;

        try
        {
            var result = await _hub.CompactSessionAsync(agentId, sessionId);
            // Inject the canonical compaction notification locally so the user
            // sees the same _[Session context compacted: ...]_ text regardless of
            // which path (auto-compact via channel push, or this RPC) triggered
            // compaction. The gateway no longer fans this out via the SignalR
            // channel for hub-initiated compactions, so we render it here.
            if (_store.GetConversation(convId) is { } conv)
            {
                var notificationText = result.Succeeded
                    ? $"_[Session context compacted: {result.Summarized} older messages summarised, {result.Preserved} recent messages preserved. Continuing…]_"
                    : $"_[Compaction failed: {result.FailureReason ?? "unknown error"}]_";
                conv.AppendMessage(new ChatMessage("System", notificationText, DateTimeOffset.UtcNow));
                _store.NotifyChanged();
            }

            return result;
        }
        catch (Exception ex)
        {
            AppendError(agentId, $"Compact failed: {ex.Message}");
            return null;
        }
    }

    // ── Conversation management ───────────────────────────────────────────

    public async Task<string?> CreateConversationAsync(string agentId, string? title = null, bool select = true)
    {
        var agent = _store.GetAgent(agentId);
        if (agent is null) return null;

        try
        {
            var request = new CreateConversationRequestDto(agentId, title);
            var dto = await _restClient.CreateConversationAsync(request);
            if (dto is null) return null;

            agent.Conversations[dto.ConversationId] = new ConversationState
            {
                ConversationId = dto.ConversationId,
                Title = dto.Title,
                IsDefault = dto.IsDefault,
                Status = dto.Status,
                ActiveSessionId = dto.ActiveSessionId,
                CreatedAt = dto.CreatedAt,
                UpdatedAt = dto.UpdatedAt,
                HistoryLoaded = true // brand new — nothing to load
            };

            if (select)
                _store.SetActiveConversation(agentId, dto.ConversationId);
            else
                _store.NotifyChanged();

            return dto.ConversationId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CreateConversation failed for {AgentId}", Sanitise(agentId));
            return null;
        }
    }

    public async Task SelectConversationAsync(string agentId, string conversationId)
    {
        var agent = _store.GetAgent(agentId);
        if (agent is null) return;
        if (!agent.Conversations.ContainsKey(conversationId)) return;

        _store.SetActiveConversation(agentId, conversationId);

        var conv = agent.Conversations.GetValueOrDefault(conversationId);

        // Best-effort REST hydration of per-conversation artifacts that arrive out of band from the
        // live SignalR stream (canvas #413, todo #1464, pending ask_user #1488). A reloaded tab, a
        // newly-opened window, or a mobile client that missed the live event would otherwise show
        // stale/empty panels; hydrating on select makes the persisted state reappear. Each call
        // shares the same fetch/apply/swallow envelope (HydrateBestEffortAsync) so a failing REST
        // call still silently degrades and a null result is a no-op.
        if (conv is not null && conv.CanvasHtml is null)
        {
            await HydrateBestEffortAsync(
                conversationId,
                () => _restClient.GetConversationCanvasAsync(agentId, conversationId),
                canvasHtml =>
                {
                    conv.CanvasHtml = canvasHtml;
                    conv.CanvasUpdatedAt = DateTimeOffset.UtcNow;
                    return true;
                },
                "canvas");
        }

        if (conv is not null && conv.TodoJson is null)
        {
            await HydrateBestEffortAsync(
                conversationId,
                () => _restClient.GetConversationTodoAsync(agentId, conversationId),
                todoJson =>
                {
                    conv.TodoJson = todoJson;
                    conv.TodoUpdatedAt = DateTimeOffset.UtcNow;
                    return true;
                },
                "todo");
        }

        // Only hydrate the pending ask_user prompt when nothing is already pending locally so a live
        // prompt is never clobbered by a slower REST round-trip. The factory guard remains: a fetched
        // payload only produces a change (and NotifyChanged) when it builds a valid prompt.
        if (_store.GetPendingAskUser(conversationId) is null)
        {
            await HydrateBestEffortAsync(
                conversationId,
                () => _restClient.GetConversationPendingAskUserAsync(agentId, conversationId),
                pendingJson =>
                {
                    if (AskUserPromptFactory.TryBuildFromPersistedJson(pendingJson, conversationId, out var pendingPrompt))
                    {
                        _store.SetPendingAskUser(pendingPrompt);
                        return true;
                    }

                    return false;
                },
                "pending ask_user");
        }

        // Fix #789: if the conversation was streaming when the user navigated away, a
        // terminal SignalR event (MessageEnd/Error/TurnInterrupted/RunEnded) may have been missed.
        // Reset stale streaming/run state and force a history reload so the UI reflects the
        // actual server-side turn result rather than a perpetual in-progress spinner.
        if (conv is not null && conv.StreamState.IsTurnActive)
        {
            conv.StreamState.EndRun();
            conv.HistoryLoaded = false; // force reload below
        }

        // Load history if not already loaded
        if (conv is not null && !conv.HistoryLoaded && !conv.IsLoadingHistory)
            await LoadConversationHistoryAsync(agentId, conversationId);
    }

    /// <summary>
    /// Best-effort REST hydration envelope shared by the per-conversation artifact hydration paths
    /// (canvas, todo, pending ask_user) in <see cref="SelectConversationAsync"/>. Captures the
    /// invariant "fetch a value, apply it if present, notify the store, and swallow any transport
    /// failure as a debug log" so each artifact only supplies its fetch delegate and apply logic.
    /// This keeps <see cref="SelectConversationAsync"/> focused on selection orchestration and gives
    /// the swallow-and-degrade behaviour a single tested seam rather than three copy-pasted blocks.
    /// </summary>
    /// <param name="conversationId">Conversation being hydrated (used only for diagnostic logging).</param>
    /// <param name="fetch">Best-effort REST getter; may return <c>null</c> when nothing is persisted.</param>
    /// <param name="apply">
    /// Applies the fetched value to local state and returns <c>true</c> when it produced an observable
    /// change (so the store is notified only when something actually changed). Never invoked for a
    /// <c>null</c> fetch result.
    /// </param>
    /// <param name="label">Human-readable artifact name for the failure log message.</param>
    private async Task HydrateBestEffortAsync(
        string conversationId,
        Func<Task<string?>> fetch,
        Func<string, bool> apply,
        string label)
    {
        try
        {
            var value = await fetch();
            if (value is not null && apply(value))
                _store.NotifyChanged();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Best-effort {Label} hydration failed for {ConversationId}", label, Sanitise(conversationId));
        }
    }

    /// <inheritdoc />
    public async Task<int> LoadMoreHistoryAsync(string agentId, string conversationId)
    {
        var agent = _store.GetAgent(agentId);
        var conv = agent?.Conversations.GetValueOrDefault(conversationId);
        if (conv is null)
            return 0;

        // Nothing older to fetch, a fetch is already in flight, or this is a virtual
        // (cron/sub-agent) transcript that is not paged backwards. Treat as a no-op so a
        // repeated scroll-to-top never double-fetches or pages a virtual session (#1691).
        if (!conv.HasMoreHistory || conv.IsLoadingHistory || conv.IsVirtualSession)
            return 0;

        conv.IsLoadingHistory = true;
        _store.NotifyChanged();
        try
        {
            var response = await _restClient.GetHistoryAsync(
                conversationId,
                limit: DefaultHistoryPageSize,
                offset: conv.LoadedHistoryRows);

            var entries = response?.Entries;
            if (entries is not { Count: > 0 })
            {
                // Empty older page => we have reached the start of the transcript.
                conv.HasMoreHistory = false;
                return 0;
            }

            // Prepend the older page above the current view. The store's PrependMessages keeps the
            // id->index map consistent; the component layer preserves the visual scroll position so
            // the viewport does not jump (#1691).
            var older = entries.Select(ProjectConversationEntry).ToList();
            _store.PrependMessages(conversationId, older);

            conv.LoadedHistoryRows += entries.Count;
            // Stop once a page returns fewer rows than the page size.
            conv.HasMoreHistory = entries.Count >= DefaultHistoryPageSize;
            return entries.Count;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // The conversation vanished server-side mid-scroll; stop paging rather than loop on 404.
            conv.HasMoreHistory = false;
            _logger.LogWarning(ex, "LoadMoreHistory 404 for conversation {ConversationId}", Sanitise(conversationId));
            return 0;
        }
        catch (Exception ex)
        {
            // A transient failure should not wedge the view; leave HasMoreHistory set so a later
            // scroll-up can retry, but surface nothing destructive to the timeline.
            _logger.LogError(ex, "LoadMoreHistory failed for {ConversationId}", Sanitise(conversationId));
            return 0;
        }
        finally
        {
            conv.IsLoadingHistory = false;
            _store.NotifyChanged();
        }
    }

    public async Task RenameConversationAsync(string agentId, string? conversationId, string newTitle)
    {
        if (conversationId is null) return;
        var agent = _store.GetAgent(agentId);
        if (agent is null) return;
        if (string.IsNullOrWhiteSpace(newTitle)) return;
        if (!agent.Conversations.TryGetValue(conversationId, out var conv)) return;

        try
        {
            await _restClient.RenameConversationAsync(conversationId, newTitle);
            conv.Title = newTitle;
            _store.NotifyChanged();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RenameConversation failed for {ConversationId}", Sanitise(conversationId));
        }
    }

    public async Task ArchiveConversationAsync(string agentId, string conversationId)
    {
        var agent = _store.GetAgent(agentId);
        if (agent is null) return;
        if (!agent.Conversations.TryGetValue(conversationId, out var conversation)) return;

        try
        {
            // All conversation cleanup — including virtual cron projections — routes through
            // DELETE /api/conversations/{conversationId}. The backend handles cron-session: IDs
            // idempotently (returns 204 even if no backing session exists), preserving session
            // records while hiding the conversation from the sidebar.
            var success = await _restClient.ArchiveConversationAsync(conversationId);

            if (!success)
            {
                _logger.LogWarning("Conversation cleanup returned failure for {ConversationId}", Sanitise(conversationId));
                return;
            }

            agent.Conversations.Remove(conversationId);

            // If this was the active conversation, switch to the next available or clear
            if (agent.ActiveConversationId == conversationId)
            {
                var next = agent.Conversations.Keys.FirstOrDefault();
                if (next is not null)
                {
                    _store.SetActiveConversation(agentId, next);
                }
                else
                {
                    agent.ActiveConversationId = null;
                    _store.NotifyChanged();
                }
            }
            else
            {
                _store.NotifyChanged();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ArchiveConversation failed for {ConversationId}", Sanitise(conversationId));
        }
    }

    public async Task SetConversationPinnedAsync(string agentId, string conversationId, bool pinned)
    {
        var agent = _store.GetAgent(agentId);
        if (agent is null) return;
        if (!agent.Conversations.TryGetValue(conversationId, out var conversation)) return;
        if (conversation.IsPinned == pinned) return;

        // Optimistically flip local state so the conversation immediately re-groups under "Pinned"
        // (or drops back out), then persist. Roll back if the gateway call fails.
        var previousPinned = conversation.IsPinned;
        conversation.IsPinned = pinned;
        _store.NotifyChanged();

        try
        {
            var success = await _restClient.PinConversationAsync(conversationId, pinned);
            if (!success)
            {
                _logger.LogWarning("Pin toggle returned failure for {ConversationId}", Sanitise(conversationId));
                conversation.IsPinned = previousPinned;
                _store.NotifyChanged();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SetConversationPinned failed for {ConversationId}", Sanitise(conversationId));
            conversation.IsPinned = previousPinned;
            _store.NotifyChanged();
        }
    }

    public async Task RefreshAgentsAsync()
    {
        try
        {
            var agents = await _restClient.GetAgentsAsync();
            foreach (var agent in agents)
            {
                if (!_store.Agents.ContainsKey(agent.AgentId))
                {
                    _store.UpsertAgent(new AgentState
                    {
                        AgentId = agent.AgentId,
                        DisplayName = agent.DisplayName,
                        Emoji = agent.Emoji,
                        Description = agent.Description,
                        IsBuiltIn = agent.IsBuiltIn,
                        IsConnected = true
                    });
                }
                else if (_store.GetAgent(agent.AgentId) is { } existing)
                {
                    existing.DisplayName = agent.DisplayName;
                    existing.Emoji = agent.Emoji;
                }
            }

            _store.NotifyChanged();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RefreshAgents failed");
        }
    }

    public async Task RefreshConversationsAsync(string agentId)
    {
        await RefreshConversationsForAgentAsync(agentId);
    }

    public async Task ViewSubAgentAsync(SubAgentInfo subAgent)
    {
        var subAgentId = subAgent.SubAgentId;

        var childSessionId = subAgent.ChildSessionId ?? subAgentId;
        if (!_store.Agents.ContainsKey(subAgentId))
        {
            _store.UpsertAgent(new AgentState
            {
                AgentId = subAgentId,
                DisplayName = subAgent.Name ?? $"Sub-agent {subAgentId[..Math.Min(8, subAgentId.Length)]}",
                SessionId = childSessionId,
                SessionType = "agent-subagent",
                IsConnected = true
            });
            _store.RegisterSession(subAgentId, childSessionId);
        }

        // #2247 decision: viewing a sub-agent is an EXPLICIT NON-NAVIGATIONAL OVERLAY, not a route
        // segment. It deliberately does NOT call NavigationManager, so it never rewrites the URL and
        // therefore never clobbers the user's underlying route-owned selection: a refresh or back/
        // forward returns to the user's own agent+conversation, not the read-only sub-agent transcript.
        // Because the browser URL stays put, the sub-agent view is a transient inspection layer that
        // survives only until the next real navigation - exactly the "must not clobber the underlying
        // user route silently" requirement. Route ownership stays with the user's conversation; the
        // SubAgentView source is the one seam the store lets promote a read-only session to the active
        // view (see #2243/#2246), and it is scoped to this single SelectView call.
        // #2243: this is the sole user-initiated path allowed to promote a read-only sub-agent
        // session to the active view. Pass SelectionSource.SubAgentView, the one source the store's
        // anti-hijack guard lets through onto a read-only agent.
        _store.SelectView(subAgentId, string.Empty, SelectionSource.SubAgentView);
        _store.NotifyChanged();

        // Load history if needed
        var agent = _store.GetAgent(subAgentId)!;
        var convId = agent.ActiveConversationId;
        if (convId is null || (agent.Conversations.GetValueOrDefault(convId) is { } conv && !conv.HistoryLoaded))
        {
            // Sub-agent sessions are loaded by session ID
            await LoadSubAgentHistoryAsync(subAgentId);
        }
    }

    public async Task RespondToAskUserAsync(
        string conversationId,
        string requestId,
        string? freeFormText,
        string[]? selectedValues,
        bool cancelled)
    {
        await _hub.RespondToAskUserAsync(
            conversationId,
            requestId,
            freeFormText,
            selectedValues,
            cancelled);
    }

    public void ClearLocalMessages(string agentId)
    {
        var agent = _store.GetAgent(agentId);
        if (agent?.ActiveConversationId is null) return;

        var conv = agent.Conversations.GetValueOrDefault(agent.ActiveConversationId);
        if (conv is null) return;

        conv.ClearMessages();
        conv.HistoryLoaded = false;
        conv.AppendMessage(new ChatMessage("System", "Local messages cleared.", DateTimeOffset.UtcNow));
        _store.NotifyChanged();
    }

    // ── Private helpers ───────────────────────────────────────────────────

    private async Task LoadConversationHistoryAsync(string agentId, string conversationId)
    {
        var agent = _store.GetAgent(agentId);
        if (agent is null) return;
        var conv = agent.Conversations.GetValueOrDefault(conversationId);
        if (conv is null || conv.IsLoadingHistory) return;
        if (conv.HistoryLoaded)
            return; // Already loaded from server — don't reload

        // Guard: never load history while the conversation is mid-stream.
        // Messages.Clear() would wipe tool-call messages and the partially-built
        // assistant buffer that HandleContentDelta is accumulating. The deferred
        // refresh mechanism (DrainPendingConversationRefreshes) handles post-stream
        // updates; attempting a load here would lose visible streamed text (#759).
        if (conv.StreamState.IsStreaming)
            return;

        conv.IsLoadingHistory = true;
        conv.HistoryLoadFailed = false; // clear any prior failure before re-attempting (#1697)
        _store.NotifyChanged();

        try
        {
            // Virtual sessions (cron/soul projections) read the raw session transcript;
            // regular conversations read the merged conversation history with boundaries.
            if (conv.IsVirtualSession && conv.ActiveSessionId is { Length: > 0 } sessionId)
            {
                await LoadVirtualHistoryAsync(conv, sessionId);
            }
            else
            {
                await LoadRegularHistoryAsync(agent, conv, conversationId);
            }
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            HandleHistoryNotFound(agent, agentId, conversationId, ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LoadHistory failed for {ConversationId}", Sanitise(conversationId));
            conv.HistoryLoaded = true; // don't retry
            conv.HistoryLoadFailed = true; // surface a load-error empty state instead of a blank pane (#1697)
        }
        finally
        {
            conv.IsLoadingHistory = false;
            _store.NotifyChanged();
        }
    }

    /// <summary>
    /// Loads transcript entries for a virtual-session conversation (cron/soul/sub-agent
    /// projections) from the session-history endpoint and replaces the displayed messages.
    /// </summary>
    private async Task LoadVirtualHistoryAsync(ConversationState conv, string sessionId)
    {
        const int virtualHistoryLimit = 200;
        var sessionResponse = await _restClient.GetSessionHistoryAsync(sessionId, limit: virtualHistoryLimit);
        conv.ClearMessages();
        if (sessionResponse?.Entries is { Count: > 0 })
        {
            foreach (var message in sessionResponse.Entries.Select(ToChatMessage))
                conv.AppendMessage(message);
        }

        conv.HistoryLoaded = true;
    }

    /// <summary>
    /// Loads merged conversation history (including session-boundary and compaction
    /// dividers) from the conversation-history endpoint and replaces the displayed
    /// messages, then syncs the agent-global session pointer when this conversation
    /// is the active one.
    /// </summary>
    private async Task LoadRegularHistoryAsync(AgentState agent, ConversationState conv, string conversationId)
    {
        var response = await _restClient.GetHistoryAsync(conversationId, limit: DefaultHistoryPageSize, offset: 0);

        conv.ClearMessages();
        conv.LoadedHistoryRows = 0;
        conv.HasMoreHistory = false;

        if (response?.Entries is { Count: > 0 } entries)
        {
            foreach (var entry in entries)
                conv.AppendMessage(ProjectConversationEntry(entry));

            // The endpoint pages backwards into older history; a full page means there is more to
            // fetch on scroll-up, while a short page means we have reached the start (#1691).
            conv.LoadedHistoryRows = entries.Count;
            conv.HasMoreHistory = entries.Count >= DefaultHistoryPageSize;
        }

        conv.HistoryLoaded = true;

        // Sync session ID
        if (agent.ActiveConversationId == conversationId && conv.ActiveSessionId is not null)
            agent.SessionId = conv.ActiveSessionId;
    }

    /// <summary>
    /// Projects a single conversation-history entry into a displayable <see cref="ChatMessage"/>,
    /// rendering session-boundary and compaction dividers as system rows and routing ordinary
    /// entries through <see cref="ToChatMessage(ConversationHistoryEntryDto)"/>. Shared by the
    /// initial load and the scroll-up load-more path so both build identical timelines (#1691).
    /// </summary>
    private static ChatMessage ProjectConversationEntry(ConversationHistoryEntryDto entry)
    {
        if (entry.Kind == "boundary")
        {
            var label = $"Session \u00b7 {entry.Timestamp.ToLocalTime():MMM d HH:mm} \u00b7 {entry.SessionId}";
            return new ChatMessage("System", string.Empty, entry.Timestamp)
            {
                Kind = "boundary",
                BoundaryLabel = label,
                BoundarySessionId = entry.SessionId
            };
        }

        if (entry.Kind == "compaction")
        {
            var label = "Context compacted \u00b7 " + entry.Timestamp.ToLocalTime().ToString("MMM d HH:mm");
            return new ChatMessage("System", entry.Content ?? string.Empty, entry.Timestamp)
            {
                Kind = "compaction",
                BoundaryLabel = label,
                BoundarySessionId = entry.SessionId
            };
        }

        return ToChatMessage(entry);
    }

    /// <summary>
    /// Recovers from a 404 on history load: the conversation no longer exists server-side,
    /// so it is dropped locally. When it was the active conversation, the store is asked to
    /// re-select the most recent remaining conversation (default first, then newest) as a
    /// route-navigation-equivalent selection, or — when none remain — the active conversation
    /// pointer is cleared and the selection flagged invalid so the UI resolves it on next render.
    /// This handler mutates data + selection-invalid signalling only; it never promotes a view
    /// onto a read-only session (#2246).
    /// </summary>
    private void HandleHistoryNotFound(AgentState agent, string agentId, string conversationId, HttpRequestException ex)
    {
        _logger.LogWarning(ex, "History 404 for conversation {ConversationId}", Sanitise(conversationId));
        agent.Conversations.Remove(conversationId);
        if (agent.ActiveConversationId == conversationId)
        {
            var nextConversationId = agent.Conversations.Values
                .OrderByDescending(c => c.IsDefault)
                .ThenByDescending(c => c.UpdatedAt)
                .Select(c => c.ConversationId)
                .FirstOrDefault();
            if (nextConversationId is not null)
            {
                _store.SetActiveConversation(agentId, nextConversationId);
            }
            else
            {
                agent.ActiveConversationId = null;
                _store.MarkSelectionInvalid();
                _store.NotifyChanged();
            }
        }
    }

    private async Task LoadSubAgentHistoryAsync(string subAgentId)
    {
        // Sub-agents use session history endpoint, not conversation history
        var agent = _store.GetAgent(subAgentId);
        if (agent is null) return;

        // Create a virtual conversation for the sub-agent session
        var convId = $"subagent-session:{subAgentId}";
        if (!agent.Conversations.TryGetValue(convId, out var conv))
        {
            conv = new ConversationState
            {
                ConversationId = convId,
                Title = "Sub-agent session",
                Status = "Active",
                ActiveSessionId = subAgentId,
                IsVirtualSession = true,
                VirtualSessionKind = "subagent",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                HistoryLoaded = false
            };
            agent.Conversations[convId] = conv;
        }

        // Keep ActiveSessionId in sync with the actual child session ID
        conv.ActiveSessionId = agent.SessionId ?? subAgentId;
        agent.ActiveConversationId = convId;
        if (conv.HistoryLoaded || conv.IsLoadingHistory) return;

        conv.IsLoadingHistory = true;
        _store.NotifyChanged();

        try
        {
            // Use session history endpoint for sub-agent session transcripts.
            // Use the child session ID (from SubAgentInfo.ChildSessionId propagated via AgentState.SessionId)
            // rather than the ephemeral sub-agent run ID to avoid 404s.
            var sessionIdForHistory = agent.SessionId ?? subAgentId;
            var response = await _restClient.GetSessionHistoryAsync(sessionIdForHistory, limit: 50);
            conv.ClearMessages();
            if (response?.Entries is { Count: > 0 })
            {
                foreach (var message in response.Entries.Select(ToChatMessage))
                    conv.AppendMessage(message);
            }

            conv.HistoryLoaded = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LoadSubAgentHistory failed for {SubAgentId}", Sanitise(subAgentId));
            conv.HistoryLoaded = true;
        }
        finally
        {
            conv.IsLoadingHistory = false;
            _store.NotifyChanged();
        }
    }

    private async Task RefreshConversationsForAgentAsync(string agentId)
    {
        try
        {
            var listTask = _restClient.GetConversationsAsync(agentId);
            var sessionsTask = _restClient.GetSessionsAsync(agentId);
            await Task.WhenAll(listTask, sessionsTask);

            var list = listTask.Result;
            _store.SeedConversations(agentId, list);

            foreach (var session in sessionsTask.Result)
                _store.RegisterSession(session.AgentId, session.SessionId, session.ChannelType, session.SessionType, session.ConversationId);

            // Fetch canvas for the auto-selected conversation on initial load (#383)
            var agent = _store.GetAgent(agentId);
            var activeConvId = agent?.ActiveConversationId;
            if (activeConvId is not null && agent!.Conversations.TryGetValue(activeConvId, out var activeConv))
            {
                try
                {
                    var canvasHtml = await _restClient.GetConversationCanvasAsync(agentId, activeConvId);
                    if (canvasHtml is not null)
                    {
                        activeConv.CanvasHtml = canvasHtml;
                        activeConv.CanvasUpdatedAt = DateTimeOffset.UtcNow;
                    }
                }
                catch (Exception ex) { _logger.LogDebug(ex, "Best-effort canvas hydration failed for {ConversationId}", Sanitise(activeConvId)); }
            }

            _store.NotifyChanged();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RefreshConversations failed for {AgentId}", Sanitise(agentId));
        }
    }

    private void AppendUserMessage(string agentId, string content)
    {
        var agent = _store.GetAgent(agentId);
        var convId = agent?.ActiveConversationId;
        if (convId is null || agent!.Conversations.GetValueOrDefault(convId) is not { } conv) return;

        conv.AppendMessage(new ChatMessage("User", content, DateTimeOffset.UtcNow));
        _store.NotifyChanged();
    }

    private void AppendError(string agentId, string message)
    {
        var agent = _store.GetAgent(agentId);
        var convId = agent?.ActiveConversationId;
        if (convId is not null && agent!.Conversations.GetValueOrDefault(convId) is { } conv)
        {
            conv.AppendMessage(new ChatMessage("Error", message, DateTimeOffset.UtcNow));
        }

        _store.NotifyChanged();
    }

    private static string MapRole(string role) => role.ToLowerInvariant() switch
    {
        "user" => "User",
        "assistant" => "Assistant",
        "tool" => "Tool",
        "error" => "Error",
        "system" => "System",
        _ => role
    };

    /// <summary>
    /// Single source of truth for projecting a session-history transcript entry into a
    /// displayable <see cref="ChatMessage"/>. Used by the virtual-session loader and the
    /// sub-agent loader (both read <see cref="SessionHistoryEntryDto"/>); the conversation
    /// loader uses the <see cref="ConversationHistoryEntryDto"/> overload. Internal so the
    /// projection contract (tool-call flag, ANSI-stripped result, role mapping) can be
    /// asserted directly in tests.
    /// </summary>
    internal static ChatMessage ToChatMessage(SessionHistoryEntryDto entry) =>
        ToChatMessage(
            entry.Role,
            entry.Content,
            entry.Timestamp,
            entry.ToolName,
            entry.ToolCallId,
            entry.ToolArgs,
            entry.ToolIsError,
            entry.ThinkingContent);

    /// <summary>
    /// Single source of truth for projecting a conversation-history transcript entry
    /// (the non-boundary, non-compaction case) into a displayable <see cref="ChatMessage"/>.
    /// Boundary and compaction entries are handled by the conversation loader itself and
    /// are not routed through this factory.
    /// </summary>
    internal static ChatMessage ToChatMessage(ConversationHistoryEntryDto entry) =>
        ToChatMessage(
            entry.Role,
            entry.Content,
            entry.Timestamp,
            entry.ToolName,
            entry.ToolCallId,
            entry.ToolArgs,
            entry.ToolIsError,
            entry.ThinkingContent);

    // Shared projection logic for both transcript-entry DTO shapes. An entry is treated
    // as a tool call when it carries a tool name; only then is its content surfaced as the
    // (ANSI-stripped) tool result. AnsiStripper.Strip is null-safe and preserves a null
    // content unchanged, matching the pre-refactor inline projections exactly.
    private static ChatMessage ToChatMessage(
        string? role,
        string? content,
        DateTimeOffset timestamp,
        string? toolName,
        string? toolCallId,
        string? toolArgs,
        bool toolIsError,
        string? thinkingContent)
    {
        var isToolCall = toolName is not null;
        return new ChatMessage(MapRole(role ?? "system"), content ?? string.Empty, timestamp)
        {
            ToolName = toolName,
            ToolCallId = toolCallId,
            ToolArgs = toolArgs,
            ToolIsError = toolIsError,
            ThinkingContent = thinkingContent,
            IsToolCall = isToolCall,
            ToolResult = isToolCall ? AnsiStripper.Strip(content) : null
        };
    }
}
