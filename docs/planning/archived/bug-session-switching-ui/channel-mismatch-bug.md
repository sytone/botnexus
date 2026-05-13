# Bug: Channel Type Mismatch Causes History Loss on Session Switch / Refresh

**Severity:** High — all session history appears empty after switching away and back, or on page refresh
**Found by:** Nova + Jon, 2026-04-12
**Root cause:** Channel type normalization mismatch between write path and read path

## Symptoms

- Switch to another agent session, switch back → "No messages yet"
- Refresh the page → "No messages yet"
- Messages are NOT lost (session + history still in store) — they just can't be found

## Root Cause

**Write path** (`GatewayHub.SendMessage`) normalizes channel types before saving:

```csharp
// GatewayHub.NormalizeChannelType()
"web chat" → "signalr"
"web-chat" → "signalr"
```

So sessions are stored with `ChannelType = "signalr"`.

**Read path** (`ChannelHistoryController.GetHistory`) does NOT normalize:

```csharp
// ChannelHistoryController.GetHistory() — line 50
var sessions = (await _sessions.ListByChannelAsync(
    AgentId.From(agentId), 
    ChannelKey.From(channelType),  // ← raw from URL, e.g. "web chat"
    cancellationToken))
```

And `SessionStoreBase.ListByChannelAsync` does strict equality:

```csharp
// SessionStoreBase.ListByChannelAsync() — line 47
.Where(session => session.ChannelType is not null && session.ChannelType == channelType)
```

**The comparison:** `"signalr"` == `"web chat"` → **false** → empty result → "No messages yet"

## How the UI triggers it

1. Sidebar renders channel items using `normalizeChannelKey()` which maps `"signalr"` → `"web chat"`
2. Sidebar click calls `openAgentTimeline(agentId, "web chat")`
3. Cold path fetches `/api/channels/web%20chat/agents/nova/history?limit=50`
4. Server can't find any sessions with `ChannelType == "web chat"` (they're all `"signalr"`)
5. Returns empty → "No messages yet"

## Fix Options

### Option A: Normalize in ChannelHistoryController (minimal, targeted)

```csharp
// In GetHistory(), before the query:
var normalizedChannelType = NormalizeChannelType(ChannelKey.From(channelType));
var sessions = (await _sessions.ListByChannelAsync(
    AgentId.From(agentId), normalizedChannelType, cancellationToken))
```

Requires moving/sharing `NormalizeChannelType` from `GatewayHub` (currently private static).

### Option B: Normalize in ListByChannelAsync (defensive, recommended)

```csharp
// In SessionStoreBase.ListByChannelAsync():
public async Task<IReadOnlyList<GatewaySession>> ListByChannelAsync(
    AgentId agentId, ChannelKey channelType, CancellationToken cancellationToken = default)
{
    var sessions = await EnumerateSessionsAsync(cancellationToken).ConfigureAwait(false);
    var normalized = NormalizeChannelType(channelType);
    return sessions
        .Where(session => session.AgentId == agentId)
        .Where(session => ChannelMatches(session.ChannelType, normalized))
        .OrderByDescending(session => session.CreatedAt)
        .ToList();
}
```

This protects ALL callers of `ListByChannelAsync`, not just the history controller.

### Option C: Client sends "signalr" to history endpoint

Change the UI to use `toHubChannelType()` instead of the display-normalized value for the REST call. Quick but doesn't fix the server-side fragility.

## Recommendation

**Option B** is the safest. The store layer should be alias-aware since channel types have known synonyms. But `NormalizeChannelType` and/or `ChannelMatches` need to be extracted from `GatewayHub` into a shared location (e.g., a static helper on `ChannelKey` itself, or a `ChannelKeyExtensions` class in `BotNexus.Domain`).

A complementary quick fix with **Option C** could also be applied client-side to unblock immediately while the server-side fix is done properly.

## Files Involved

| File | Role |
|------|------|
| `src/gateway/BotNexus.Gateway.Api/Hubs/GatewayHub.cs` | Has `NormalizeChannelType` (private static) and `ChannelMatches` |
| `src/gateway/BotNexus.Gateway.Api/Controllers/ChannelHistoryController.cs` | Calls `ListByChannelAsync` with raw channel type |
| `src/gateway/BotNexus.Gateway.Sessions/SessionStoreBase.cs` | `ListByChannelAsync` does strict equality |
| `src/BotNexus.WebUI/wwwroot/app.js` | `openAgentTimeline` passes `normalizeChannelKey()` output ("web chat") to REST endpoint |
| `src/domain/BotNexus.Domain/` | Candidate location for shared normalization logic on `ChannelKey` |
