### Conversation Routing Architecture — Design Review & Bug Fix

**Decision Date:** 2026-05-07  
**Decided By:** Leela (Lead/Architect)  
**Status:** Approved — ready for implementation  

---

## Context

User reports that agents only respond on the default conversation. When switching to a non-default conversation in the WebUI, messages are sent but no response appears. The agent always replies on the default conversation.

Additionally, the user identifies tight coupling between GatewayHub and conversation routing logic that should be separated.

## Root Cause Analysis

**Bug: Dual routing with session mismatch in `GatewayHub.ResolveOrCreateSessionAsync`**

The message flow has a critical flaw — **two independent routing calls** that produce conflicting session IDs:

### Flow for Non-Default Conversation Message

1. **Client** (`AgentInteractionService.SendMessageAsync`) sends `conversationId` correctly via `_hub.SendMessageAsync(agentId, channelType, content, convIdNow)`.

2. **GatewayHub.SendMessageCore** (line 178-202) calls `ResolveOrCreateSessionAsync(agentId, channelType)` which calls the conversation router with **`conversationId: null`** (line 593). This always resolves via binding lookup → finds the DEFAULT conversation → returns **Session A** (the default conversation's session).

3. **Client subscribes** to SignalR group `session:A` (line 187).

4. **GatewayHub dispatches** `InboundMessage` with `SessionId = A` (from step 2) and `ConversationId = target_conv_id` (from client).

5. **GatewayHost.ProcessInboundMessageAsync** (line 263-270) calls the router AGAIN with `message.ConversationId = target_conv_id`. The router correctly resolves to the target conversation → returns **Session B**. `sessionId` is overridden to Session B (line 270).

6. **Agent processes** on Session B. Response is sent to SignalR group `session:B`.

7. **Client is subscribed to `session:A`** → response is never received. From the user's perspective, the agent "only responds on the default conversation" because the client is listening on the wrong session group.

### Secondary Issue

The `SendMessageResult` returned to the client (line 198-201) contains Session A's ID (the default), not the final Session B. The client registers this session, further cementing the mismatch.

## Architectural Issues Identified

### 1. Dual Routing (GatewayHub + GatewayHost)

`GatewayHub` directly depends on `IConversationRouter` and performs its own session resolution (line 38, 582-632). `GatewayHost` independently performs conversation routing (line 261-280). These two routing calls can produce different results.

**Violation:** Single Responsibility. Conversation routing has two owners.

### 2. Session Pre-Resolution in Channel Adapter

`GatewayHub.ResolveOrCreateSessionAsync` resolves a session BEFORE dispatching the message, then passes that session ID in the `InboundMessage`. This creates a contract expectation that the session is already determined — but `GatewayHost` may override it via the conversation router.

**Violation:** Liskov Substitution. The `SessionId` on `InboundMessage` does not guarantee where processing will occur.

### 3. SignalR Group Subscription is Timing-Dependent

The client must be subscribed to the correct session's SignalR group BEFORE the response is sent. Since the final session isn't known until after `GatewayHost` processes the message (fire-and-forget), the subscription is always based on stale data.

---

## Decision: Two-Phase Fix

### Phase 1 — Immediate Bug Fix (Bender)

**Goal:** Make non-default conversations work by eliminating the routing conflict.

**Change:** Modify `GatewayHub.ResolveOrCreateSessionAsync` to accept and pass `conversationId` to the router.

```
// BEFORE (line 588-594 of GatewayHub.cs):
var routingResult = await _conversationRouter.ResolveInboundAsync(
    agentId, channelType, channelAddress,
    threadId: null,
    conversationId: null,   // ← BUG: always null
    Context.ConnectionAborted);

// AFTER:
var routingResult = await _conversationRouter.ResolveInboundAsync(
    agentId, channelType, channelAddress,
    threadId: null,
    conversationId: conversationId,  // ← pass through from caller
    Context.ConnectionAborted);
```

**Files modified:**
| File | Change |
|------|--------|
| `GatewayHub.cs:ResolveOrCreateSessionAsync` | Add `string? conversationId = null` parameter, pass to router |
| `GatewayHub.cs:SendMessageCore` | Pass `conversationId` to `ResolveOrCreateSessionAsync` |
| `GatewayHub.cs:SendMessageWithMedia` | No change yet (no conversationId support — follow-up) |

**Why this works:**
- With the correct conversationId, the router returns Session B directly
- The client subscribes to `session:B`
- The dispatched `InboundMessage` has `SessionId = B` and `ConversationId = target_conv_id`
- GatewayHost's router call confirms Session B (no conflict)
- Response goes to `session:B` → client receives it

**Risk:** LOW — the router's `conversationId` path is already tested (it's used by GatewayHost today). We're just making GatewayHub use it too.

### Phase 2 — Decouple GatewayHub from Conversation Routing (Farnsworth)

**Goal:** Remove the dual-routing pattern. GatewayHub should dispatch messages without knowing about conversations. GatewayHost is the sole routing authority.

**Contracts:**

1. **Remove `IConversationRouter` dependency from `GatewayHub`** — the hub should not perform conversation routing. It dispatches raw messages to `IChannelDispatcher`.

2. **Add `DispatchResult` to `IChannelDispatcher.DispatchAsync`** — the dispatcher should return the resolved session ID so the hub can subscribe the client to the correct group.

   ```csharp
   // Current contract:
   Task DispatchAsync(InboundMessage message, CancellationToken ct);
   
   // Proposed contract:
   Task<DispatchResult> DispatchAsync(InboundMessage message, CancellationToken ct);
   
   public sealed record DispatchResult(
       SessionId SessionId,
       ConversationId? ConversationId,
       bool IsNewSession);
   ```

3. **GatewayHub.SendMessageCore simplification:**
   ```csharp
   // No more ResolveOrCreateSessionAsync — just dispatch and subscribe
   var result = await _dispatcher.DispatchAsync(new InboundMessage { ... }, ct);
   await SubscribeInternalAsync(result.SessionId);
   return new SendMessageResult(result.SessionId.Value, ...);
   ```

4. **Move session subscription to post-dispatch** — subscribe after the dispatch result is known, not before. The fire-and-forget pattern must change to await-then-subscribe for the initial subscription.

**Files modified:**
| File | Change |
|------|--------|
| `IChannelDispatcher.cs` (Contracts) | Return `DispatchResult` instead of `Task` |
| `GatewayHost.cs` | Implement `DispatchResult` return |
| `GatewayHub.cs` | Remove `IConversationRouter` dependency, simplify to dispatch-and-subscribe |
| `ChannelAdapterBase.cs` | Update `DispatchInboundAsync` to propagate `DispatchResult` |

**Dependency graph impact:**
```
BEFORE: GatewayHub → IConversationRouter, ISessionStore, IChannelDispatcher
AFTER:  GatewayHub → IChannelDispatcher (only)
```

This eliminates 2 dependencies from GatewayHub, making it a pure event relay.

### Phase 3 — Ensure Channel Extensions Only Use Conversation Layer (Farnsworth)

**Goal:** Channel adapters (Telegram, TUI, future Discord) dispatch via `IChannelDispatcher` and receive outbound via `SendAsync` / `SendStreamEventAsync`. They never touch `IConversationRouter` or `ISessionStore` directly.

**Audit findings:** Telegram and TUI adapters already follow this pattern — they use `ChannelAdapterBase.DispatchInboundAsync` which calls `IChannelDispatcher`. **No changes needed** for existing channel extensions.

The SignalR extension (GatewayHub) is the sole violator, addressed in Phase 2.

---

## Wave Plan

| Wave | Agent | Scope | Duration | Dependency |
|------|-------|-------|----------|------------|
| 1 | **Bender** | Phase 1 bug fix — pass conversationId through GatewayHub | 1h | None |
| 2 | **Hermes** | Tests for Phase 1 — verify non-default conversation routing, regression tests | 1.5h | Wave 1 |
| 3 | **Farnsworth** | Phase 2 — DispatchResult contract, GatewayHub decoupling | 3h | Wave 2 |
| 4 | **Hermes** | Tests for Phase 2 — dispatch result contract tests, GatewayHub has no conversation dependency | 2h | Wave 3 |

**Phase 1 is the priority.** It fixes the user's immediate bug with minimal risk. Phase 2 is architectural cleanup that prevents recurrence.

---

## Key Files Reference

| File | Role | Issue |
|------|------|-------|
| `src/extensions/.../SignalR/GatewayHub.cs:582-632` | Hub session resolution | Always passes `conversationId: null` to router |
| `src/gateway/BotNexus.Gateway/GatewayHost.cs:261-280` | Gateway conversation routing | Correctly uses `message.ConversationId` |
| `src/gateway/BotNexus.Gateway.Conversations/DefaultConversationRouter.cs:32-63` | Conversation resolver | Has correct fast-path for explicit `conversationId` |
| `src/extensions/.../SignalR/SignalRChannelAdapter.cs:43-109` | Response delivery | Sends to SignalR group `session:{sessionId}` |
| `src/extensions/.../BlazorClient/Services/AgentInteractionService.cs:36-73` | Client message send | Correctly passes `conversationId` |

---

## Verification Checklist

- [ ] Non-default conversation receives agent response in WebUI
- [ ] Default conversation still works
- [ ] Creating a new conversation and sending a message works
- [ ] Switching between conversations mid-session works
- [ ] Fan-out to other bindings still works
- [ ] Steer command works on non-default conversations
- [ ] `dotnet build BotNexus.slnx --nologo --tl:off` — zero errors
- [ ] `dotnet test BotNexus.slnx --nologo --tl:off` — zero failures
