# Decision: Agent Editing Hot-Reload

**Author:** Leela (Lead/Architect)  
**Date:** 2025-07-25  
**Status:** proposed  
**Scope:** Gateway agent lifecycle, config reload, UI editing

## Context

When an agent is added or edited (via config file change, REST API, or Blazor UI), the
gateway already has infrastructure to detect changes and update the in-memory registry:

- `FileAgentConfigurationSource` uses `FileSystemWatcher` with 250ms debounce
- `PlatformConfigAgentSource` uses `IOptionsMonitor<PlatformConfig>.OnChange()`
- `AgentConfigurationHostedService.ApplyMergedDescriptors()` reconciles registry state
- `AgentsController` PUT/POST endpoints persist via `IAgentConfigurationWriter`

**The problem:** Newly registered or updated descriptors take effect for *future* sessions,
but active sessions retain the stale descriptor snapshot (model, tools, system prompt).
Additionally, the UI editing experience is limited — only model switching is supported
from `AgentConfigPanel`, and full edits require navigating to `/agents`.

## Decision

### 1. Source-of-Truth Rules

| Layer | Owns | Mutated by |
|-------|------|-----------|
| Config file (JSON) | Canonical agent definition | User edit, `IAgentConfigurationWriter` |
| `IAgentRegistry` (in-memory) | Active descriptor set | `AgentConfigurationHostedService`, `AgentsController` |
| `IAgentHandle` (session) | Runtime snapshot | New: descriptor-change notification |

**Rule:** Config file is king. Registry reflects file state. Active sessions *may* adopt
updated descriptors on next turn (opt-in per field).

### 2. Hot-Reload Architecture

```
FileSystemWatcher / IOptionsMonitor
        │
        ▼
AgentConfigurationHostedService.ApplyMergedDescriptors()
        │
        ├── Registry.Register / Unregister / Update
        │
        ▼
NEW: IAgentRegistry fires AgentDescriptorChanged event
        │
        ▼
IAgentSupervisor notifies active handles
        │
        ▼
Each IAgentHandle applies safe field updates on next turn
```

### 3. Safe vs Unsafe Field Updates on Active Sessions

| Field | Hot-reload safe? | Rationale |
|-------|-----------------|-----------|
| `ModelId` | ✅ Yes | Next LLM call uses new model |
| `AllowedModelIds` | ✅ Yes | Validation only |
| `DisplayName` / `Description` | ✅ Yes | Cosmetic |
| `SystemPrompt` / `SystemPromptFile` | ⚠️ Opt-in | May invalidate conversation context |
| `ToolIds` | ⚠️ Opt-in | Adding tools is safe; removing mid-conversation risky |
| `SubAgentIds` | ⚠️ Opt-in | Same as tools |
| `ApiProvider` | ❌ No | Requires new handle/connection |
| `IsolationStrategy` | ❌ No | Requires session restart |
| `MaxConcurrentSessions` | ✅ Yes | Supervisor-level, no handle impact |

### 4. Implementation Boundaries

**Gateway (Farnsworth/Hermes):**
- Add `event Action<AgentId, AgentDescriptor>? DescriptorChanged` to `IAgentRegistry`
- `DefaultAgentRegistry.Update()` fires the event (already publishes activity — wire event too)
- `DefaultAgentSupervisor` subscribes to the event and notifies active handles
- `IAgentHandle` gains `void ApplyDescriptorUpdate(AgentDescriptor updated)` — each handle
  decides which fields to adopt based on the safe/unsafe table above
- System prompt reload: re-run prompt pipeline on next turn if `SystemPromptFile` changed

**UI (Fry):**
- Enhance `AgentConfigPanel.razor` to support inline editing of more fields (not just model)
- Add real-time feedback: after PUT succeeds, show "Applied to N active sessions" badge
- `/agents` page: after save, show toast if hot-reload propagated vs "restart required"

**Config persistence (Hermes):**
- `PlatformConfigAgentWriter` (existing) writes back to `config.json` `agents` section
- Ensure write triggers `IOptionsMonitor` change notification (atomic write + rename pattern)

### 5. What NOT to Do

- **Do not restart active sessions** on descriptor change. Users lose conversation history.
- **Do not allow provider/isolation changes on live sessions.** Return 409 or queue for next session.
- **Do not bypass the config file.** All API mutations must persist to file so the gateway
  survives restart with the same state.
- **Do not watch system prompt files independently.** The `FileSystemWatcher` in
  `FileAgentConfigurationSource` already watches `*.*` with `IncludeSubdirectories = true` —
  prompt file changes trigger full reload. No additional watcher needed.

### 6. Risks

| Risk | Mitigation |
|------|-----------|
| Race between config write and watcher reload | Already mitigated by 250ms debounce timer |
| Stale tool references after hot-remove | Handle validates tool availability before dispatch; logs warning |
| Concurrent descriptor updates | Registry uses `Lock`; last-write-wins is acceptable |
| UI optimistic update diverges from server | UI must re-fetch after PUT to confirm applied state |

### 7. Required Tests

- **`AgentRegistryDescriptorChangedEventTests`** — event fires on Update, not on Register/Unregister
- **`SupervisorHotReloadPropagationTests`** — supervisor forwards to active handles
- **`HandleApplyDescriptorUpdateTests`** — safe fields adopted, unsafe fields rejected
- **`FileWatcherPromptReloadTests`** — system prompt file edit triggers descriptor refresh
- **`AgentsControllerPutPropagationTests`** — PUT triggers registry.Update → event → handle
- **`ConcurrentUpdateSafetyTests`** — multiple rapid updates don't corrupt state
- **`UIAgentEditFormTests`** (bUnit) — form renders, validates, submits, shows feedback

## Alternatives Considered

1. **Full session restart on any change** — too disruptive, loses context
2. **Separate "draft" descriptor** — over-engineered for current scale
3. **WebSocket push of descriptor diffs** — premature; polling on next turn is simpler
