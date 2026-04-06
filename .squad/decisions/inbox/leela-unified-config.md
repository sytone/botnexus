# Unified Config + Agent Directory Architecture

**Author:** Leela (Lead/Architect)  
**Date:** 2026-04-09  
**Status:** Proposal  
**Requested by:** Jon Bullen  
**Supersedes:** Previous provider/model config proposal (learnings incorporated)

---

## Problem

Agent configuration is fragmented across three sources:

1. **`config.json` → `agents:{}`** — `PlatformConfigAgentSource` reads inline definitions. Missing `displayName`, `description`, `allowedModels`, `subAgents`, `maxConcurrentSessions`, `metadata`, `isolationOptions`. **Watch() returns null** — no hot-reload.
2. **`agentsDirectory` → `*.json` files** — `FileAgentConfigurationSource` reads individual files. Has full FSW hot-reload. Used by `FileAgentConfigurationWriter` for API-created agents.
3. **`~/.botnexus/agents/{id}/`** — Workspace files (SOUL.md, IDENTITY.md, USER.md, MEMORY.md) scaffolded flat in agent root by `BotNexusHome.ScaffoldAgentWorkspace()`.

Problems:
- `PlatformConfigAgentSource` doesn't hot-reload (Watch returns null)
- `AgentDefinitionConfig` is a subset of what `FileAgentConfigurationSource` supports
- `FileAgentConfigurationWriter` writes separate JSON files that duplicate config.json
- Workspace files have no subdirectory separation from potential future data files
- `ProviderConfig` lacks `Enabled` flag and model allowlists
- `AgentDescriptor` has no model restriction concept

---

## 1. Unified config.json Schema (Version 2)

```jsonc
{
  "$schema": "https://botnexus.dev/schemas/config-v2.json",
  "version": 2,

  "gateway": {
    "listenUrl": "http://localhost:5005",
    "defaultAgentId": "nova",
    "logLevel": "Information",
    "sessionStore": { "type": "Sqlite", "connectionString": "Data Source=~/.botnexus/sessions.db" },
    "cors": { "allowedOrigins": ["http://localhost:3000"] },
    "rateLimit": { "requestsPerMinute": 60 },
    "extensions": { "path": "extensions", "enabled": true },
    "apiKeys": {
      "tenant-a": {
        "apiKey": "bnx_...",
        "tenantId": "acme",
        "permissions": ["chat:send"]
      }
    }
  },

  "providers": {
    "github-copilot": {
      "enabled": true,              // NEW — default true for backward compat
      "apiKey": "auth:github",
      "baseUrl": null,
      "defaultModel": "claude-sonnet-4",
      "models": [                    // NEW — null = all, [] = none, [...] = allowlist
        "claude-sonnet-4",
        "gpt-4.1",
        "o4-mini"
      ]
    },
    "anthropic": {
      "enabled": true,
      "apiKey": "sk-ant-...",
      "defaultModel": "claude-sonnet-4-20250514",
      "models": null                 // null = all models from this provider
    },
    "openai": {
      "enabled": false               // Disabled provider — models hidden, auth skipped
    }
  },

  "agents": {
    "nova": {
      "displayName": "Nova",         // NEW — was missing from AgentDefinitionConfig
      "description": "General assistant", // NEW
      "provider": "github-copilot",
      "model": "claude-sonnet-4",
      "allowedModels": [             // NEW — empty/null = unrestricted (within provider allowlist)
        "claude-sonnet-4",
        "gpt-4.1"
      ],
      "systemPromptFile": "workspace/SOUL.md", // Relative to ~/.botnexus/agents/nova/
      "toolIds": ["read", "write", "shell"],
      "subAgents": ["researcher"],
      "isolationStrategy": "in-process",
      "maxConcurrentSessions": 5,
      "enabled": true,
      "metadata": {},
      "isolationOptions": {}
    },
    "researcher": {
      "displayName": "Researcher",
      "provider": "github-copilot",
      "model": "gpt-4.1",
      "toolIds": ["read", "grep", "glob"],
      "enabled": true
    }
  },

  "channels": {
    "web": { "type": "websocket", "enabled": true },
    "slack": { "type": "slack", "enabled": false, "settings": { "token": "xoxb-..." } }
  }
}
```

### Schema Changes Summary

| Class | Field | Change |
|-------|-------|--------|
| `ProviderConfig` | `Enabled` | **Add** — `bool`, default `true` |
| `ProviderConfig` | `Models` | **Add** — `List<string>?`, null = all |
| `AgentDefinitionConfig` | `DisplayName` | **Add** — `string?` |
| `AgentDefinitionConfig` | `Description` | **Add** — `string?` |
| `AgentDefinitionConfig` | `AllowedModels` | **Add** — `List<string>?`, null = unrestricted |
| `AgentDefinitionConfig` | `SubAgents` | **Add** — `List<string>?` |
| `AgentDefinitionConfig` | `MaxConcurrentSessions` | **Add** — `int?` |
| `AgentDefinitionConfig` | `Metadata` | **Add** — `JsonElement?` |
| `AgentDefinitionConfig` | `IsolationOptions` | **Add** — `JsonElement?` |
| `PlatformConfig` | `Version` | Bump default to `2`, accept `1` with migration |

All additions are backward-compatible: null/default values preserve existing behavior.

---

## 2. Agent Directory Structure

```
~/.botnexus/
├── config.json                    # Single source of truth for all config
├── agents/
│   ├── nova/
│   │   ├── workspace/             # Working context (user-editable)
│   │   │   ├── SOUL.md            # Agent personality / system prompt
│   │   │   ├── IDENTITY.md        # Agent identity
│   │   │   ├── USER.md            # User preferences
│   │   │   └── MEMORY.md          # Persistent memory
│   │   └── data/                  # Internal runtime data (managed by gateway)
│   │       └── sessions/          # Per-agent session history
│   └── researcher/
│       ├── workspace/
│       │   └── SOUL.md
│       └── data/
│           └── sessions/
├── extensions/
├── tokens/
├── logs/
└── sessions/                      # Global sessions (deprecated, migrate to per-agent)
```

### Changes to `BotNexusHome`

```csharp
// Current
private static readonly string[] RequiredDirectories = ["extensions", "tokens", "sessions", "logs", "agents"];

// New  
private static readonly string[] RequiredDirectories = ["extensions", "tokens", "logs", "agents"];
// Remove "sessions" from root — sessions move to per-agent data/sessions/

// Current scaffold: files in agent root
private static void ScaffoldAgentWorkspace(string agentDirectory)
{
    foreach (var file in WorkspaceScaffoldFiles)
        File.WriteAllText(Path.Combine(agentDirectory, file), string.Empty);
}

// New scaffold: files in workspace/ subdirectory
private static void ScaffoldAgentWorkspace(string agentDirectory)
{
    var workspacePath = Path.Combine(agentDirectory, "workspace");
    Directory.CreateDirectory(workspacePath);
    Directory.CreateDirectory(Path.Combine(agentDirectory, "data", "sessions"));
    foreach (var file in WorkspaceScaffoldFiles)
    {
        var path = Path.Combine(workspacePath, file);
        if (!File.Exists(path))
            File.WriteAllText(path, string.Empty);
    }
}
```

### Changes to `FileAgentWorkspaceManager`

```csharp
// Current: reads from agent root
public string GetWorkspacePath(string agentName)
    => _botNexusHome.GetAgentDirectory(agentName.Trim());

// New: reads from workspace/ subdirectory
public string GetWorkspacePath(string agentName)
    => Path.Combine(_botNexusHome.GetAgentDirectory(agentName.Trim()), "workspace");
```

### Migration of Existing Workspace Files

On `GetAgentDirectory()`, detect legacy layout (SOUL.md in agent root) and move files:

```csharp
private static void MigrateLegacyWorkspace(string agentDirectory)
{
    var workspacePath = Path.Combine(agentDirectory, "workspace");
    if (Directory.Exists(workspacePath))
        return; // Already migrated

    var hasLegacyFiles = WorkspaceScaffoldFiles
        .Any(f => File.Exists(Path.Combine(agentDirectory, f)));
    if (!hasLegacyFiles)
        return;

    Directory.CreateDirectory(workspacePath);
    foreach (var file in WorkspaceScaffoldFiles)
    {
        var src = Path.Combine(agentDirectory, file);
        var dst = Path.Combine(workspacePath, file);
        if (File.Exists(src))
            File.Move(src, dst);
    }
}
```

### `systemPromptFile` Resolution

Currently resolved relative to the config directory. Under the unified model:
- Absolute paths: used as-is
- Relative paths: resolved from `~/.botnexus/agents/{agent-id}/`

This means `"systemPromptFile": "workspace/SOUL.md"` resolves to `~/.botnexus/agents/nova/workspace/SOUL.md`. The path traversal guard in `PlatformConfigAgentSource` changes its base from `configDirectory` to the agent's home directory.

---

## 3. Migration Plan

### Phase A: Enrich Config Schema (Non-Breaking)

Add new fields to `ProviderConfig` and `AgentDefinitionConfig`. Update `PlatformConfigAgentSource.LoadAsync()` to map all new fields to `AgentDescriptor`. Existing configs with only `provider` + `model` continue to work.

### Phase B: Agent Directory Restructure

1. Update `BotNexusHome.ScaffoldAgentWorkspace()` to create `workspace/` + `data/sessions/`
2. Add `MigrateLegacyWorkspace()` call in `GetAgentDirectory()`
3. Update `FileAgentWorkspaceManager.GetWorkspacePath()` to return `workspace/` subdir
4. Update `systemPromptFile` resolution base path

### Phase C: Unified Config Source + Hot-Reload

1. Wire `PlatformConfigLoader.ConfigChanged` event to `PlatformConfigAgentSource` so inline agents hot-reload
2. Implement `PlatformConfigAgentSource.Watch()` (currently returns null)
3. Add `PlatformConfigAgentWriter` that writes back to `config.json` agents section
4. Wire API agent creation to write to config.json instead of separate files

### Phase D: Deprecate File-Based Agent Config

1. Add startup warning if `agentsDirectory` is configured: "Deprecated: agent definitions should be in config.json"
2. Keep `FileAgentConfigurationSource` functional for one release cycle
3. Add migration command: `botnexus config migrate-agents` — reads agents from directory files, merges into config.json
4. Remove `agentsDirectory` from `GatewaySettingsConfig` in next major version

### Phase E: Provider Model Filtering (from previous proposal)

1. Add `IModelFilter` decorator wrapping `ModelRegistry`
2. Implement 3-layer filtering: provider allowlist → API endpoints → per-agent intersection
3. Controllers switch from `ModelRegistry` to `IModelFilter`

---

## 4. Hot-Reload Architecture

### Current State

```
config.json change
  → PlatformConfigWatcher (FSW, 500ms debounce)
    → PlatformConfigLoader.ConfigChanged event
      → ??? (nothing subscribes in default wiring)
```

```
agentsDirectory/*.json change  
  → FileConfigurationWatcher (FSW, 250ms debounce)
    → FileAgentConfigurationSource.LoadAsync()
      → AgentConfigurationHostedService.OnSourceChanged()
        → Registry.Unregister/Register
```

**Gap:** `PlatformConfigAgentSource.Watch()` returns `null`. Inline agents don't hot-reload.

### Target State

```
config.json change
  → PlatformConfigWatcher (FSW, 500ms debounce)
    → PlatformConfigLoader.ConfigChanged event
      → PlatformConfigAgentSource re-reads IOptions<PlatformConfig>
        → AgentConfigurationHostedService.OnSourceChanged()
          → Registry.Unregister/Register
      → IModelFilter re-evaluates provider allowlists
```

### Implementation

`PlatformConfigAgentSource.Watch()` implementation:

```csharp
public IDisposable? Watch(Action<IReadOnlyList<AgentDescriptor>> onChanged)
{
    // Subscribe to PlatformConfigLoader.ConfigChanged
    void handler(PlatformConfig _)
    {
        var descriptors = LoadAsync(CancellationToken.None).GetAwaiter().GetResult();
        onChanged(descriptors);
    }
    PlatformConfigLoader.ConfigChanged += handler;
    return new CallbackDisposable(() => PlatformConfigLoader.ConfigChanged -= handler);
}
```

**Note:** The static `ConfigChanged` event on `PlatformConfigLoader` is already wired — `PlatformConfigWatcher.ReloadConfig()` fires it on every reload. We just need to subscribe.

### What Cannot Be Hot-Reloaded

| Setting | Hot-Reload? | Reason |
|---------|-------------|--------|
| `gateway.listenUrl` | ❌ | Kestrel binding — requires process restart |
| `gateway.apiKeys` | ✅ | Read per-request by auth middleware |
| `providers.*.apiKey` | ✅ | Resolved per-request via `GatewayAuthManager` |
| `providers.*.enabled` | ✅ | `IModelFilter` re-evaluates on config change |
| `providers.*.models` | ✅ | `IModelFilter` re-evaluates on config change |
| `agents.*` | ✅ | Registry update via Watch callback |
| `gateway.cors` | ⚠️ | Requires CORS policy rebuild (middleware restart) |
| `gateway.rateLimit` | ✅ | Read per-request |
| `gateway.sessionStore` | ❌ | Store type is a singleton — requires restart |
| `gateway.extensions` | ❌ | Extension loader runs once at startup |

---

## 5. Implementation Phases

| Phase | Work | Agent | Est |
|-------|------|-------|-----|
| A1 | Add `Enabled`, `Models` to `ProviderConfig` | Bender | S |
| A2 | Add `DisplayName`, `Description`, `AllowedModels`, `SubAgents`, `MaxConcurrentSessions`, `Metadata`, `IsolationOptions` to `AgentDefinitionConfig` | Bender | S |
| A3 | Update `PlatformConfigAgentSource.LoadAsync()` to map all new fields | Bender | S |
| A4 | Update `PlatformConfigLoader.Validate()` for new fields | Bender | S |
| B1 | Restructure `BotNexusHome` scaffold to `workspace/` + `data/sessions/` | Kif | S |
| B2 | Add `MigrateLegacyWorkspace()` auto-migration | Kif | S |
| B3 | Update `FileAgentWorkspaceManager` workspace path | Kif | S |
| B4 | Update `systemPromptFile` resolution to agent home base | Kif | M |
| C1 | Implement `PlatformConfigAgentSource.Watch()` via ConfigChanged subscription | Bender | S |
| C2 | Add `PlatformConfigAgentWriter` (write-back to config.json agents section) | Farnsworth | M |
| C3 | Wire API agent CRUD to `PlatformConfigAgentWriter` | Farnsworth | S |
| D1 | Add deprecation warning for `agentsDirectory` | Kif | S |
| D2 | Add `botnexus config migrate-agents` CLI command | Hermes | M |
| E1 | Implement `IModelFilter` decorator | Bender | M |
| E2 | 3-layer filtering: provider → API → per-agent | Bender | M |
| E3 | Wire controllers to `IModelFilter` | Bender | S |

**Size:** S = small (< 1 hour), M = medium (1-3 hours)

**Recommended execution order:** A1-A4 → B1-B4 → C1-C3 → E1-E3 → D1-D2

Phases A and B can run in parallel (different agents, no conflicts). Phase C depends on A. Phase D is deferred to a future release. Phase E can run after A.

---

## 6. What Gets Removed

### Immediate (Phase C complete)

| File | Reason |
|------|--------|
| `FileAgentConfigurationWriter.cs` | Replaced by `PlatformConfigAgentWriter` |
| `NoOpAgentConfigurationWriter` (if exists) | Replaced by `PlatformConfigAgentWriter` |
| `AddFileAgentConfiguration()` extension | No longer the default registration path |

### Deferred (Phase D, next major version)

| File / Member | Reason |
|---------------|--------|
| `FileAgentConfigurationSource.cs` | Agents come from config.json only |
| `FileConfigurationWatcher` (inner class) | Replaced by `PlatformConfigLoader.Watch()` pipeline |
| `PlatformConfig.AgentsDirectory` | Deprecated field |
| `GatewaySettingsConfig.AgentsDirectory` | Deprecated field |
| `PlatformConfig.GetAgentsDirectory()` | Deprecated helper |
| `PlatformConfig.SessionsDirectory` | Sessions move to per-agent `data/sessions/` |
| `GatewaySettingsConfig.SessionsDirectory` | Same |
| `BotNexusHome.RequiredDirectories["sessions"]` | Sessions no longer at root level |

### Config Schema Properties Removed (v2)

- `agentsDirectory` — agents defined inline in config.json
- `sessionsDirectory` — sessions stored per-agent under `data/sessions/`

---

## 7. Backward Compatibility

| Scenario | Behavior |
|----------|----------|
| v1 config with `agents:{}` only | Works as-is. New fields default safely. |
| v1 config with `agentsDirectory` | Deprecated warning logged. `FileAgentConfigurationSource` still loads. |
| v1 config with both | Both sources active (current behavior). Deprecation warning for directory source. |
| Existing `~/.botnexus/agents/{id}/SOUL.md` (flat) | Auto-migrated to `workspace/SOUL.md` on first access. |
| `ProviderConfig` without `enabled`/`models` | Defaults to `enabled: true`, `models: null` (all). Zero-breaking. |
| `AgentDefinitionConfig` without new fields | All nullable, defaults match current behavior. |

---

## Open Questions

1. **Config write conflict** — `PlatformConfigAgentWriter` needs atomic read-modify-write of config.json. Use temp file + rename (same pattern as `FileAgentConfigurationWriter`). Risk: concurrent external edits. Mitigation: advisory file lock during write, re-read before merge.

2. **Per-agent sessions vs global sessions** — Should we keep the global `sessionStore` config for backward compat, or force per-agent sessions in Phase B? **Recommendation:** Keep global session store as-is; per-agent `data/sessions/` is for workspace-level session artifacts (not the full session store).

3. **JSON Schema generation** — Should we auto-generate the v2 schema from the C# types? **Recommendation:** Yes, use `PlatformConfigSchema` (already exists for v1 validation) and extend for v2 fields.
