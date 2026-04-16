---
id: improvement-extension-config-inheritance
title: "World-Level Extension Config Defaults with Agent-Level Overrides"
type: improvement
priority: medium
status: delivered
created: 2025-07-23
updated: 2026-04-16
author: nova
tags: [configuration, extensions, agents, DRY]
ddd_types: [AgentDescriptor, PlatformConfig, AgentDefinitionConfig, ExtensionsConfig]
---

# World-Level Extension Config Defaults with Agent-Level Overrides

## Summary

Extension configuration is duplicated across every agent definition. There is no world/gateway-level default — each agent repeats the full config block for every extension (e.g., `botnexus-skills`, `botnexus-exec`, `botnexus-web`). This violates DRY, makes it easy for agents to drift, and forces users to update N agent blocks when a shared setting changes.

## Problem

Current `config.json` repeats identical blocks per agent:

```json
{
  "agents": {
    "nova": {
      "extensions": {
        "botnexus-skills": { "enabled": true, "maxLoadedSkills": 20, "maxSkillContentChars": 100000 },
        "botnexus-exec": { "enabled": true },
        "botnexus-process": { "enabled": true }
      }
    },
    "assistant": {
      "extensions": {
        "botnexus-skills": { "enabled": true, "maxLoadedSkills": 20, "maxSkillContentChars": 100000 },
        "botnexus-exec": { "enabled": true },
        "botnexus-process": { "enabled": true }
      }
    },
    "aurum": {
      "extensions": {
        "botnexus-skills": { "enabled": true, "maxLoadedSkills": 20, "maxSkillContentChars": 100000 },
        "botnexus-exec": { "enabled": true },
        "botnexus-process": { "enabled": true }
      }
    }
  }
}
```

Three agents, three identical copies. Add a fourth agent, you must remember to copy the block again.

### What's missing

- No `gateway.extensions.defaults` section for world-level extension config
- `ResolveExtensionConfig<T>()` reads directly from `AgentDescriptor.ExtensionConfig` with no fallback
- `AgentDescriptorBuilder` passes agent-level config as-is with no merge step

## Proposal

### Config Schema

Add a `defaults` section under `gateway.extensions`:

```json
{
  "gateway": {
    "extensions": {
      "enabled": true,
      "path": null,
      "defaults": {
        "botnexus-skills": { "enabled": true, "maxLoadedSkills": 20, "maxSkillContentChars": 100000 },
        "botnexus-exec": { "enabled": true },
        "botnexus-process": { "enabled": true },
        "botnexus-web": {
          "search": { "provider": "copilot", "maxResults": 5 },
          "fetch": { "maxLengthChars": 20000, "timeoutSeconds": 30 }
        }
      }
    }
  },
  "agents": {
    "nova": {
      "extensions": {
        "botnexus-skills": { "maxLoadedSkills": 30 },
        "botnexus-web": { "fetch": { "maxLengthChars": 50000 } }
      }
    },
    "assistant": {},
    "aurum": {
      "extensions": {
        "botnexus-exec": { "enabled": false }
      }
    }
  }
}
```

### Merge Semantics

```
gateway.extensions.defaults[extensionId]   <- world defaults
  deep-merged with
agents.<id>.extensions[extensionId]        <- agent overrides (wins on conflict)
  = effective extension config
```

- **Deep merge**: Nested objects are merged recursively, not replaced wholesale
- **Agent wins**: Any key explicitly set at agent level overrides the world default
- **Absent = inherit**: If an agent has no `extensions` block or no entry for an extension, it inherits the world default entirely
- **Explicit disable**: An agent can set `"enabled": false` to opt out of a world-default extension
- **Absent at both levels**: Extension gets no config (existing behavior — `ResolveExtensionConfig` returns null)

### Implementation

#### 1. Update `ExtensionsConfig`

```csharp
public sealed class ExtensionsConfig
{
    public string? Path { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// World-level default extension configuration, keyed by extension ID.
    /// Merged with agent-level overrides to produce effective config.
    /// </summary>
    public Dictionary<string, JsonElement>? Defaults { get; set; }
}
```

#### 2. Add merge logic

Create a `ExtensionConfigMerger` utility:

```csharp
public static class ExtensionConfigMerger
{
    /// <summary>
    /// Deep-merges world defaults with agent overrides.
    /// Agent values win on conflict. Null/missing sections inherit from world.
    /// </summary>
    public static Dictionary<string, JsonElement> Merge(
        Dictionary<string, JsonElement>? worldDefaults,
        Dictionary<string, JsonElement>? agentOverrides)
    {
        // Implementation: iterate all keys from both dictionaries,
        // for each extension ID, deep-merge the JsonElement trees.
    }
}
```

#### 3. Update `AgentDescriptorBuilder` (or equivalent)

Where `AgentDescriptor.ExtensionConfig` is populated from `AgentDefinitionConfig.Extensions`, insert the merge step:

```csharp
var worldDefaults = platformConfig.Gateway?.Extensions?.Defaults;
var agentOverrides = agentConfig.Extensions;
var effectiveConfig = ExtensionConfigMerger.Merge(worldDefaults, agentOverrides);
// Pass effectiveConfig to AgentDescriptor
```

#### 4. No changes to `ResolveExtensionConfig<T>()`

It already reads from `AgentDescriptor.ExtensionConfig` — once the merge is done upstream, it works transparently.

### Deep Merge Rules for JsonElement

| World Value | Agent Value | Result |
|-------------|-------------|--------|
| `{ "a": 1, "b": 2 }` | `{ "b": 3, "c": 4 }` | `{ "a": 1, "b": 3, "c": 4 }` |
| `{ "a": 1 }` | absent | `{ "a": 1 }` |
| absent | `{ "b": 2 }` | `{ "b": 2 }` |
| `{ "nested": { "x": 1 } }` | `{ "nested": { "y": 2 } }` | `{ "nested": { "x": 1, "y": 2 } }` |
| `"string"` | `"override"` | `"override"` (scalars replaced, not merged) |
| `[1, 2]` | `[3, 4]` | `[3, 4]` (arrays replaced, not merged) |

## Acceptance Criteria

1. World-level defaults in `gateway.extensions.defaults` are applied to all agents
2. Agent-level overrides deep-merge with world defaults (agent wins)
3. Agents with no `extensions` block inherit world defaults entirely
4. Agents can disable a world-default extension with `"enabled": false`
5. Existing configs without `defaults` section continue to work (backward compatible)
6. `ResolveExtensionConfig<T>()` requires no changes
7. Config schema `$schema` version updated if needed
8. Unit tests for merge logic: both-present, world-only, agent-only, nested merge, scalar override, array replace, explicit disable

## Risks

- **Breaking change?** No — `defaults` is additive. Existing configs have no `defaults` section and continue to work exactly as before.
- **Deep merge complexity**: JSON deep merge has edge cases (arrays, nulls). Keep it simple: objects merge recursively, everything else replaces.
- **Config validation**: Should warn if an agent overrides a key that doesn't exist in world defaults (potential typo).
