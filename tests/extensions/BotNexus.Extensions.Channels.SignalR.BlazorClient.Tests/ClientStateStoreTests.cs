using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Shouldly;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class ClientStateStoreTests
{
    [Fact]
    public void SeedAgents_adds_agents()
    {
        var store = new ClientStateStore();

        store.SeedAgents([
            new AgentSummary("a-1", "Alpha"),
            new AgentSummary("a-2", "Beta")
        ]);

        Assert.Equal(2, store.Agents.Count);
        Assert.Equal("Alpha", store.GetAgent("a-1")?.DisplayName);
        Assert.Equal("Beta", store.GetAgent("a-2")?.DisplayName);
    }

    [Fact]
    public void SeedAgents_preserves_agent_emoji()
    {
        var store = new ClientStateStore();

        store.SeedAgents([new AgentSummary("a-1", "Alpha", "✨")]);

        Assert.Equal("✨", store.GetAgent("a-1")?.Emoji);
    }

    [Fact]
    public void SeedAgents_updates_existing_agent()
    {
        var store = new ClientStateStore();
        store.SeedAgents([new AgentSummary("a-1", "Old")]);

        store.SeedAgents([new AgentSummary("a-1", "New")]);

        Assert.Equal("New", store.GetAgent("a-1")?.DisplayName);
    }

    [Fact]
    public void UpsertAgent_adds_new_agent()
    {
        var store = new ClientStateStore();

        store.UpsertAgent(new AgentState { AgentId = "a-1", DisplayName = "Agent", IsConnected = true });

        Assert.True(store.GetAgent("a-1")?.IsConnected);
        Assert.Equal("Agent", store.GetAgent("a-1")?.DisplayName);
    }

    [Fact]
    public void UpsertAgent_merges_metadata_without_destroying_conversations()
    {
        var store = new ClientStateStore();
        store.SeedAgents([new AgentSummary("a-1", "Alpha")]);
        store.SeedConversations("a-1", [CreateConversation("c-1", "a-1", "General")]);
        store.SetActiveConversation("a-1", "c-1");

        // Simulate a refresh that upserts with updated metadata
        store.UpsertAgent(new AgentState { AgentId = "a-1", DisplayName = "Alpha Updated", Emoji = "\ud83d\ude80", IsConnected = true });

        var agent = store.GetAgent("a-1");
        Assert.NotNull(agent);
        Assert.Equal("Alpha Updated", agent.DisplayName);
        Assert.Equal("\ud83d\ude80", agent.Emoji);
        Assert.True(agent.IsConnected);
        // Critical: conversations and active selection must be preserved
        Assert.Single(agent.Conversations);
        Assert.Equal("c-1", agent.ActiveConversationId);
    }

    [Fact]
    public void UpsertAgent_preserves_session_id_on_existing_agent()
    {
        var store = new ClientStateStore();
        store.SeedAgents([new AgentSummary("a-1", "Alpha")]);
        store.GetAgent("a-1")!.SessionId = "session-123";

        store.UpsertAgent(new AgentState { AgentId = "a-1", DisplayName = "Alpha", IsConnected = true });

        Assert.Equal("session-123", store.GetAgent("a-1")?.SessionId);
    }

    [Fact]
    public void SeedConversations_populates_agent_conversations_and_selects_default()
    {
        var store = CreateSeededStore();

        store.SeedConversations("a-1", [
            CreateConversation("c-1", "a-1", "General", isDefault: false, updatedAt: DateTimeOffset.UtcNow.AddMinutes(-1)),
            CreateConversation("c-2", "a-1", "Default", isDefault: true, updatedAt: DateTimeOffset.UtcNow)
        ]);

        var agent = store.GetAgent("a-1");
        Assert.NotNull(agent);
        Assert.Equal(2, agent.Conversations.Count);
        Assert.Equal("c-2", agent.ActiveConversationId);
    }

    [Fact]
    public void SetActiveConversation_updates_agent_and_clears_conversation_unread()
    {
        var store = CreateSeededStore();
        store.SeedConversations("a-1", [CreateConversation("c-1", "a-1", "General")]);
        store.GetAgent("a-1")!.Conversations["c-1"].UnreadCount = 3;

        store.SetActiveConversation("a-1", "c-1");

        Assert.Equal("c-1", store.GetAgent("a-1")?.ActiveConversationId);
        Assert.Equal(0, store.GetConversation("c-1")?.UnreadCount);
    }

    [Fact]
    public void GetConversation_searches_all_agents()
    {
        var store = CreateSeededStore();
        store.SeedConversations("a-1", [CreateConversation("c-1", "a-1", "One")]);
        store.SeedConversations("a-2", [CreateConversation("c-2", "a-2", "Two")]);

        Assert.Equal("Two", store.GetConversation("c-2")?.Title);
    }

    [Fact]
    public void AppendMessage_adds_message_to_conversation()
    {
        var store = CreateConversationStore();

        store.AppendMessage("c-1", new ChatMessage("User", "hello", DateTimeOffset.UtcNow));

        Assert.Single(store.GetMessages("c-1"));
        Assert.Equal("hello", store.GetMessages("c-1")[0].Content);
    }

    [Fact]
    public void PrependMessages_inserts_messages_at_start()
    {
        var store = CreateConversationStore();
        store.AppendMessage("c-1", new ChatMessage("Assistant", "newer", DateTimeOffset.UtcNow));

        store.PrependMessages("c-1", [
            new ChatMessage("User", "older-1", DateTimeOffset.UtcNow.AddMinutes(-2)),
            new ChatMessage("Assistant", "older-2", DateTimeOffset.UtcNow.AddMinutes(-1))
        ]);

        Assert.Equal(3, store.GetMessages("c-1").Count);
        Assert.Equal("older-1", store.GetMessages("c-1")[0].Content);
        Assert.Equal("older-2", store.GetMessages("c-1")[1].Content);
        Assert.Equal("newer", store.GetMessages("c-1")[2].Content);
    }

    [Fact]
    public void ClearMessages_clears_messages_and_marks_history_not_loaded()
    {
        var store = CreateConversationStore();
        store.GetConversation("c-1")!.HistoryLoaded = true;
        store.AppendMessage("c-1", new ChatMessage("User", "hello", DateTimeOffset.UtcNow));

        store.ClearMessages("c-1");

        Assert.Empty(store.GetMessages("c-1"));
        Assert.False(store.GetConversation("c-1")!.HistoryLoaded);
    }

    [Fact]
    public void SetStreaming_updates_conversation_and_agent_state()
    {
        var store = CreateConversationStore();

        store.SetStreaming("c-1", true);

        Assert.True(store.GetStreamState("c-1").IsStreaming);
        Assert.True(store.GetAgent("a-1")!.IsStreaming);
    }

    [Fact]
    public void AppendStreamBuffer_accumulates_delta_content()
    {
        var store = CreateConversationStore();

        store.AppendStreamBuffer("c-1", "hel");
        store.AppendStreamBuffer("c-1", "lo");

        Assert.Equal("hello", store.GetStreamState("c-1").Buffer);
    }

    [Fact]
    public void CommitStreamBuffer_appends_assistant_message_and_resets_stream_state()
    {
        var store = CreateConversationStore();
        store.SetStreaming("c-1", true);
        store.AppendStreamBuffer("c-1", "hello");
        store.GetStreamState("c-1").ThinkingBuffer = "thinking";

        store.CommitStreamBuffer("c-1");

        var messages = store.GetMessages("c-1");
        Assert.Single(messages);
        Assert.Equal("Assistant", messages[0].Role);
        Assert.Equal("hello", messages[0].Content);
        Assert.Equal("thinking", messages[0].ThinkingContent);
        Assert.False(store.GetStreamState("c-1").IsStreaming);
        Assert.Equal(string.Empty, store.GetStreamState("c-1").Buffer);
        Assert.Equal(string.Empty, store.GetStreamState("c-1").ThinkingBuffer);
    }

    [Fact]
    public void OnChanged_fires_for_mutations()
    {
        var store = CreateConversationStore();
        var count = 0;
        store.OnChanged += () => count++;

        store.AppendMessage("c-1", new ChatMessage("User", "hello", DateTimeOffset.UtcNow));
        store.SetStreaming("c-1", true);
        store.AppendStreamBuffer("c-1", "x");

        Assert.Equal(3, count);
    }

    [Fact]
    public void SetPendingAskUser_and_GetPendingAskUser_round_trip_prompt()
    {
        var store = CreateConversationStore();
        var prompt = new AskUserPromptState
        {
            RequestId = "req-1",
            ConversationId = "c-1",
            Prompt = "Need input",
            InputType = "FreeForm",
            AllowFreeForm = true
        };

        store.SetPendingAskUser(prompt);

        var pending = store.GetPendingAskUser("c-1");
        Assert.NotNull(pending);
        Assert.Equal("req-1", pending.RequestId);
        Assert.Equal("Need input", pending.Prompt);
    }

    [Fact]
    public void ClearPendingAskUser_removes_prompt()
    {
        var store = CreateConversationStore();
        store.SetPendingAskUser(new AskUserPromptState
        {
            RequestId = "req-1",
            ConversationId = "c-1",
            Prompt = "Need input",
            InputType = "FreeForm",
            AllowFreeForm = true
        });

        store.ClearPendingAskUser("c-1");

        Assert.Null(store.GetPendingAskUser("c-1"));
    }

    [Fact]
    public void ActiveConversationId_returns_active_conversation_for_active_agent()
    {
        var store = CreateSeededStore();
        store.SeedConversations("a-1", [CreateConversation("c-1", "a-1", "One")]);
        store.SelectView("a-1", string.Empty, SelectionSource.UserClick);
        store.SetActiveConversation("a-1", "c-1");

        Assert.Equal("c-1", store.ActiveConversationId);
    }

    [Fact]
    public void RemoveAgent_removes_agent_from_store()
    {
        var store = CreateSeededStore();

        store.RemoveAgent("a-1");

        Assert.Null(store.GetAgent("a-1"));
        Assert.Single(store.Agents);
    }

    [Fact]
    public void RemoveAgent_resets_active_agent_if_removed()
    {
        var store = CreateSeededStore();
        store.SelectView("a-1", string.Empty, SelectionSource.UserClick);

        store.RemoveAgent("a-1");

        Assert.NotEqual("a-1", store.ActiveAgentId);
    }

    [Fact]
    public void RemoveAgent_fires_OnChanged()
    {
        var store = CreateSeededStore();
        var fired = false;
        store.OnChanged += () => fired = true;

        store.RemoveAgent("a-1");

        Assert.True(fired);
    }

    // ── steer-routing regression: RegisterSession must not stomp the active conversation ──
    // Reproduces the production bug where a steer landed on a different conversation's idle
    // session. RefreshConversationsForAgentAsync loops over every session and calls
    // RegisterSession; the old logic blindly stamped agent.SessionId AND the active
    // conversation's ActiveSessionId with the last-iterated session, so steer/abort/compact
    // targeted the wrong (often idle) session.

    [Fact]
    public void RegisterSession_with_conversationId_binds_to_that_conversation_only()
    {
        var store = CreateSeededStore();
        store.SeedConversations("a-1", [
            CreateConversation("conv-active", "a-1", "Active", activeSessionId: "sess-active"),
            CreateConversation("conv-other", "a-1", "Other", activeSessionId: "sess-other")
        ]);
        store.SetActiveConversation("a-1", "conv-active");

        // A bulk refresh registers the OTHER conversation's session while conv-active is displayed.
        store.RegisterSession("a-1", "sess-other", "signalr", "user-agent", conversationId: "conv-other");

        // The other conversation's session binds to ITS conversation, not the active one.
        store.GetConversation("conv-other")!.ActiveSessionId.ShouldBe("sess-other");
        // Critical: the active conversation's binding is untouched.
        store.GetConversation("conv-active")!.ActiveSessionId.ShouldBe("sess-active",
            customMessage: "Registering another conversation's session must NOT overwrite the " +
                "active conversation's ActiveSessionId.");
    }

    [Fact]
    public void RegisterSession_with_conversationId_only_updates_global_for_active_conversation()
    {
        var store = CreateSeededStore();
        store.SeedConversations("a-1", [
            CreateConversation("conv-active", "a-1", "Active", activeSessionId: "sess-active"),
            CreateConversation("conv-other", "a-1", "Other", activeSessionId: "sess-other")
        ]);
        store.SetActiveConversation("a-1", "conv-active"); // sets agent.SessionId = sess-active

        // Registering a non-active conversation's session must NOT move the agent-global SessionId.
        store.RegisterSession("a-1", "sess-other", "signalr", "user-agent", conversationId: "conv-other");
        store.GetAgent("a-1")!.SessionId.ShouldBe("sess-active",
            customMessage: "agent.SessionId must only follow the ACTIVE conversation's session.");

        // Registering the active conversation's session does update the global fallback.
        store.RegisterSession("a-1", "sess-active-v2", "signalr", "user-agent", conversationId: "conv-active");
        store.GetAgent("a-1")!.SessionId.ShouldBe("sess-active-v2");
    }

    [Fact]
    public void RegisterSession_bulk_refresh_does_not_misroute_active_conversation_session()
    {
        // Full reproduction of the production scenario: conv-active is displayed with a live run;
        // a refresh iterates ALL sessions (the displayed one AND an unrelated idle conversation).
        // The displayed conversation's ActiveSessionId must remain its OWN session afterwards so
        // a subsequent steer targets the right session.
        var store = CreateSeededStore();
        store.SeedConversations("a-1", [
            CreateConversation("conv-active", "a-1", "Active", activeSessionId: "sess-active"),
            CreateConversation("conv-idle", "a-1", "Idle", activeSessionId: "sess-idle")
        ]);
        store.SetActiveConversation("a-1", "conv-active");

        // Simulate RefreshConversationsForAgentAsync's loop. Order matters: the idle session is
        // iterated LAST (this is what poisoned agent.SessionId in production).
        store.RegisterSession("a-1", "sess-active", "signalr", "user-agent", conversationId: "conv-active");
        store.RegisterSession("a-1", "sess-idle", "signalr", "user-agent", conversationId: "conv-idle");

        store.GetConversation("conv-active")!.ActiveSessionId.ShouldBe("sess-active");
        store.GetConversation("conv-idle")!.ActiveSessionId.ShouldBe("sess-idle");
        store.GetAgent("a-1")!.SessionId.ShouldBe("sess-active",
            customMessage: "After a full refresh, the agent-global session must still point at the " +
                "DISPLAYED conversation's session, not the last-iterated one.");
        // The displayed conversation's resolved session (used by steer/abort/compact) is correct.
        store.GetAgent("a-1")!.ActiveConversationSessionId.ShouldBe("sess-active");
    }

    [Fact]
    public void RegisterSession_cron_session_does_not_touch_active_conversation_or_global()
    {
        var store = CreateSeededStore();
        store.SeedConversations("a-1", [
            CreateConversation("conv-active", "a-1", "Active", activeSessionId: "sess-active")
        ]);
        store.SetActiveConversation("a-1", "conv-active");

        // A cron session must never poison the user-facing session/binding.
        store.RegisterSession("a-1", "cron:job-1:run-1", "internal", "cron", conversationId: "conv-active");

        store.GetAgent("a-1")!.SessionId.ShouldBe("sess-active");
        store.GetConversation("conv-active")!.ActiveSessionId.ShouldBe("sess-active");
    }

    [Fact]
    public void RegisterSession_legacy_no_conversationId_still_binds_active_conversation()
    {
        // Preserve the #314 race fix: a freshly established session (e.g. from a SendMessage
        // result, registered without a conversationId) binds to the active conversation so
        // MessageStart can resolve it before the REST refresh completes.
        var store = CreateSeededStore();
        store.SeedConversations("a-1", [
            CreateConversation("conv-new", "a-1", "New", activeSessionId: null)
        ]);
        store.SetActiveConversation("a-1", "conv-new");

        store.RegisterSession("a-1", "sess-new");

        store.GetConversation("conv-new")!.ActiveSessionId.ShouldBe("sess-new");
        store.GetAgent("a-1")!.SessionId.ShouldBe("sess-new");
    }

    // ── #2243 active-view anti-hijack guard ────────────────────────────────────

    [Fact]
    public void SelectView_rejects_switching_onto_a_read_only_sub_agent()
    {
        // #2243: a SubAgentSpawned or streaming event that lands around send time must never be
        // able to promote the sub-agent's read-only virtual session to the active view. The store
        // setter is the single choke point every non-user-click assignment flows through, so it
        // silently rejects a switch onto a read-only agent.
        var store = CreateSeededStore();
        store.SelectView("a-1", string.Empty, SelectionSource.UserClick);
        store.UpsertAgent(new AgentState
        {
            AgentId = "sub-1",
            DisplayName = "Sub-agent",
            SessionType = "agent-subagent", // => IsReadOnly == true
            IsConnected = true
        });

        store.SelectView("sub-1", string.Empty, SelectionSource.UserClick);

        store.ActiveAgentId.ShouldBe("a-1",
            customMessage: "A concurrent event must not hijack the active view onto a read-only " +
                "sub-agent session; the user's own agent stays active.");
    }

    [Fact]
    public void SelectView_rejects_marked_sub_agent_before_its_state_or_session_type_exists()
    {
        // #2243 race hardening, PORTED from the #2254 quick-patch to the #2246 SelectView seam.
        // The derived-IsReadOnly half of the guard only rejects a switch when the target's
        // SessionType is ALREADY stamped "agent-subagent". But HandleSubAgentSpawned registers the
        // sub-agent asynchronously, so around send time there is often NO AgentState for the
        // sub-agent yet (or one still carrying the default "user-agent" SessionType) when a
        // concurrent, non-user-initiated selection lands. MarkSubAgent records the id at spawn so the
        // SelectView guard rejects the switch independent of that SessionType-stamp ordering. Here we
        // deliberately do NOT create an AgentState for "sub-1", and drive the selection with a
        // NON-SubAgentView source (RouteNavigation) to model the background assignment.
        var store = CreateSeededStore();
        store.SelectView("a-1", string.Empty, SelectionSource.UserClick);

        store.MarkSubAgent("sub-1"); // spawn-time marking, before any AgentState/SessionType exists
        store.SelectView("sub-1", string.Empty, SelectionSource.RouteNavigation); // background switch

        store.ActiveAgentId.ShouldBe("a-1",
            customMessage: "A sub-agent marked at spawn time must be rejected by the guard even before " +
                "its AgentState exists or its SessionType has been stamped read-only.");

        // And once its AgentState is later registered (still not via SubAgentView), it stays rejected.
        store.UpsertAgent(new AgentState
        {
            AgentId = "sub-1",
            DisplayName = "Sub-agent",
            SessionType = "agent-subagent",
            IsConnected = true
        });
        store.SelectView("sub-1", string.Empty, SelectionSource.Bootstrap);
        store.ActiveAgentId.ShouldBe("a-1");

        // The explicit user "view sub-agent" interaction (SubAgentView source) still promotes it.
        store.SelectView("sub-1", string.Empty, SelectionSource.SubAgentView);
        store.ActiveAgentId.ShouldBe("sub-1");
    }

    [Fact]
    public void SelectView_allows_switching_between_user_agents()
    {
        var store = CreateSeededStore();

        store.SelectView("a-1", string.Empty, SelectionSource.UserClick);
        store.SelectView("a-2", string.Empty, SelectionSource.UserClick);

        store.ActiveAgentId.ShouldBe("a-2");
    }

    [Fact]
    public void SelectView_promotes_read_only_sub_agent_on_explicit_SubAgentView_source()
    {
        // The explicit "view sub-agent" user click is the ONE source allowed to switch the active
        // view onto a read-only session. It calls SelectView with SelectionSource.SubAgentView, which
        // bypasses the anti-hijack guard.
        var store = CreateSeededStore();
        store.SelectView("a-1", string.Empty, SelectionSource.UserClick);
        store.UpsertAgent(new AgentState
        {
            AgentId = "sub-1",
            DisplayName = "Sub-agent",
            SessionType = "agent-subagent",
            IsConnected = true
        });

        store.SelectView("sub-1", string.Empty, SelectionSource.SubAgentView);

        store.ActiveAgentId.ShouldBe("sub-1");
    }

    [Fact]
    public void SubAgentView_selection_does_not_leave_the_guard_open_for_later_events()
    {
        // The SubAgentView allowance must be scoped to the single SelectView call: a subsequent
        // background event switching onto another read-only sub-agent must still be rejected.
        var store = CreateSeededStore();
        store.UpsertAgent(new AgentState { AgentId = "sub-1", DisplayName = "Sub 1", SessionType = "agent-subagent", IsConnected = true });
        store.UpsertAgent(new AgentState { AgentId = "sub-2", DisplayName = "Sub 2", SessionType = "agent-subagent", IsConnected = true });

        store.SelectView("sub-1", string.Empty, SelectionSource.SubAgentView);
        store.SelectView("sub-2", string.Empty, SelectionSource.UserClick); // simulates a background SubAgentSpawned assignment

        store.ActiveAgentId.ShouldBe("sub-1",
            customMessage: "The sub-agent activation allowance must not persist past the explicit " +
                "SelectView(SubAgentView) call.");
    }

    // ── #2246 single SelectView seam ───────────────────────────────────────────

    [Fact]
    public void SelectView_sets_agent_and_conversation_atomically()
    {
        var store = CreateSeededStore();
        store.SeedConversations("a-1", [CreateConversation("c-1", "a-1", "One", activeSessionId: "sess-1")]);

        store.SelectView("a-1", "c-1", SelectionSource.UserClick);

        store.ActiveAgentId.ShouldBe("a-1");
        store.ActiveConversationId.ShouldBe("c-1",
            customMessage: "SelectView must set the active agent and its conversation in one atomic step.");
        store.GetAgent("a-1")!.SessionId.ShouldBe("sess-1");
    }

    [Fact]
    public void SelectView_with_empty_agent_clears_the_active_view()
    {
        var store = CreateSeededStore();
        store.SelectView("a-1", string.Empty, SelectionSource.UserClick);

        store.SelectView(string.Empty, string.Empty, SelectionSource.Bootstrap);

        store.ActiveAgentId.ShouldBeNull();
    }

    [Fact]
    public void ActiveAgentId_has_no_public_setter()
    {
        // Compile-time contract made explicit: the projection is read-only. Reflection asserts the
        // interface exposes no setter so a future edit cannot silently reintroduce the mutable seam.
        var prop = typeof(IClientStateStore).GetProperty(nameof(IClientStateStore.ActiveAgentId));
        prop.ShouldNotBeNull();
        prop!.CanWrite.ShouldBeFalse(
            customMessage: "ActiveAgentId must have NO public setter; the sole mutation path is SelectView (#2246).");
    }

    [Fact]
    public void Inbound_SubAgentSpawned_style_mutation_leaves_selection_unchanged()
    {
        // Inbound event handlers mutate data + NotifyChanged only; they never call SelectView. This
        // asserts that the kind of state churn a SubAgentSpawned handler performs (adding a read-only
        // sub-agent, registering its session, notifying) leaves the active-view selection byte-for-byte
        // unchanged — an event can never make a sub-agent the active view (#2243/#2246).
        var store = CreateSeededStore();
        store.SeedConversations("a-1", [CreateConversation("c-1", "a-1", "One")]);
        store.SelectView("a-1", "c-1", SelectionSource.UserClick);

        var agentBefore = store.ActiveAgentId;
        var convBefore = store.ActiveConversationId;

        // Simulate the data-only mutations an inbound SubAgentSpawned handler performs.
        store.UpsertAgent(new AgentState
        {
            AgentId = "sub-1",
            DisplayName = "Sub-agent",
            SessionType = "agent-subagent",
            IsConnected = true
        });
        store.RegisterSession("sub-1", "sub-1");
        store.NotifyChanged();

        store.ActiveAgentId.ShouldBe(agentBefore,
            customMessage: "An inbound event must not change the active agent.");
        store.ActiveConversationId.ShouldBe(convBefore,
            customMessage: "An inbound event must not change the active conversation.");
    }

    [Fact]
    public void RemoveAgent_of_active_agent_flags_pending_selection_invalid_without_auto_selecting()
    {
        // #2246: RemoveAgent is a data mutation. Removing the active agent must NOT auto-pick a
        // replacement view (that would be a second writer); it clears the selection and raises
        // PendingSelectionInvalid so the UI resolves a fresh selection on next render.
        var store = CreateSeededStore();
        store.SelectView("a-1", string.Empty, SelectionSource.UserClick);

        store.RemoveAgent("a-1");

        store.ActiveAgentId.ShouldBeNull();
        store.PendingSelectionInvalid.ShouldBeTrue(
            customMessage: "Removing the active agent must flag the selection invalid for UI resolution.");
    }

    [Fact]
    public void MarkSelectionInvalid_sets_pending_flag_and_next_SelectView_clears_it()
    {
        var store = CreateSeededStore();
        store.SelectView("a-1", string.Empty, SelectionSource.UserClick);

        store.MarkSelectionInvalid();
        store.PendingSelectionInvalid.ShouldBeTrue();

        store.SelectView("a-2", string.Empty, SelectionSource.RouteNavigation);
        store.PendingSelectionInvalid.ShouldBeFalse(
            customMessage: "Resolving the selection via SelectView must clear the pending-invalid flag.");
    }

    private static ClientStateStore CreateSeededStore()
    {
        var store = new ClientStateStore();
        store.SeedAgents([
            new AgentSummary("a-1", "Alpha"),
            new AgentSummary("a-2", "Beta")
        ]);
        return store;
    }

    [Fact]
    public void SeedConversations_filters_AgentAgent_and_AgentSubAgent_from_user_facing_list()
    {
        // Phase 4 / F-3 regression guard: agent-to-agent exchanges (AgentExchangeService.ConverseAsync)
        // and sub-agent supervision sessions (DefaultSubAgentManager) now create real Conversations
        // via IConversationStore. Without filtering, every agent_converse tool call would pollute the
        // user's conversation drawer and (worse) could auto-hijack the active tab via the
        // OrderByDescending(c => c.UpdatedAt) auto-select logic below. The portal must only seed
        // HumanAgent conversations.
        var store = new ClientStateStore();
        store.SeedAgents([new AgentSummary("a-1", "Alpha")]);

        store.SeedConversations("a-1", [
            CreateConversation("user-1", "a-1", "User chat", isDefault: true,
                updatedAt: DateTimeOffset.UtcNow.AddMinutes(-10), kind: "HumanAgent"),
            CreateConversation("aa-1", "a-1", "alpha ↔ beta",
                updatedAt: DateTimeOffset.UtcNow, kind: "AgentAgent"),
            CreateConversation("sa-1", "a-1", "alpha ↦ researcher",
                updatedAt: DateTimeOffset.UtcNow.AddMinutes(-1), kind: "AgentSubAgent"),
        ]);

        var agent = store.GetAgent("a-1");
        agent.ShouldNotBeNull();
        agent!.Conversations.Count.ShouldBe(1,
            customMessage: "AgentAgent + AgentSubAgent conversations must be filtered out of the " +
                "user-facing list. Got: " +
                string.Join(", ", agent.Conversations.Keys));
        agent.Conversations.ShouldContainKey("user-1");
        agent.Conversations.ShouldNotContainKey("aa-1",
            customMessage: "AgentAgent conversations are internal traffic and must not appear in " +
                "the portal's conversation drawer.");
        agent.Conversations.ShouldNotContainKey("sa-1",
            customMessage: "AgentSubAgent supervision sessions are internal and must not appear in " +
                "the portal's conversation drawer.");
        agent.ActiveConversationId.ShouldBe("user-1",
            customMessage: "Active tab must be the HumanAgent conversation -- not the more-recently " +
                "updated AgentAgent conversation. Auto-hijack guard.");
    }

    private static ClientStateStore CreateConversationStore()
    {
        var store = CreateSeededStore();
        store.SeedConversations("a-1", [CreateConversation("c-1", "a-1", "General")]);
        return store;
    }

    private static ConversationSummaryDto CreateConversation(
        string conversationId,
        string agentId,
        string title,
        bool isDefault = false,
        DateTimeOffset? updatedAt = null,
        string kind = "HumanAgent",
        string? activeSessionId = null) =>
        new(
            conversationId,
            agentId,
            title,
            isDefault,
            "Active",
            activeSessionId,
            0,
            DateTimeOffset.UtcNow.AddHours(-1),
            updatedAt ?? DateTimeOffset.UtcNow,
            kind);
}
