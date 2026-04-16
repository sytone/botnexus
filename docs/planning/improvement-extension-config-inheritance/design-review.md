# Design Review — Extension Config Inheritance

**Reviewer:** Leela
**Date:** 2025-07-23
**Grade:** A-

## Spec Assessment

### Grade Justification

The spec is well-structured, clearly motivated, and architecturally sound. The merge semantics table is precise, the acceptance criteria are testable, and the backward compatibility story is solid. It correctly identifies the three touch points (model, merge utility, builder) and correctly concludes that `ResolveExtensionConfig<T>()` needs zero changes.

### Gaps and Ambiguities

1. **"AgentDescriptorBuilder" doesn't exist.** The spec references `AgentDescriptorBuilder` but the actual builder is `PlatformConfigAgentSource.LoadFromConfig()` (`PlatformConfigAgentSource.cs:58-112`). The spec says "or equivalent" — acceptable, but implementation agents need the real file name.

2. **`JsonElement` is immutable.** The spec proposes operating on `JsonElement` but doesn't address that `JsonElement` is read-only in System.Text.Json. Deep merge requires converting to `JsonNode` (or writing via `Utf8JsonWriter`), merging, then converting back to `JsonElement`. This is a non-trivial implementation detail that will affect the `ExtensionConfigMerger` signature.

3. **Explicit `null` vs. absent.** The merge table covers absent keys but not explicit `null`. With `JsonElement`, `null` is `JsonValueKind.Null` while absent means the key doesn't exist in the dictionary. Decision needed: does an agent setting a key to `null` override (explicit nullification) or inherit? **Recommendation:** treat `JsonValueKind.Null` as an explicit override — agent said "I want this to be null."

4. **Config validation (typo warnings)** is mentioned in Risks but not in Acceptance Criteria. Should be deferred to a follow-up improvement to keep this change focused.

5. **Schema regeneration mechanism** is automatic. The schema is generated via NJsonSchema reflection on `PlatformConfig` (`PlatformConfigSchema.cs:114-124`). Adding `Defaults` to `ExtensionsConfig` will auto-generate into the schema when `dotnet run --project src\gateway\BotNexus.Cli -- config schema` is run. No manual schema editing needed.

## Codebase Analysis

### Current `ExtensionsConfig` — `PlatformConfig.cs:258-269`

```csharp
public sealed class ExtensionsConfig
{
    public string? Path { get; set; }
    public bool Enabled { get; set; } = true;
}
```

Only two properties: `Path` (extension discovery directory) and `Enabled` (toggle extension loading). No `Defaults` dictionary exists.

### Agent-Level Extension Config — `PlatformConfig.cs:311-315` (`AgentDefinitionConfig`)

```csharp
public Dictionary<string, JsonElement>? Extensions { get; set; }
```

Per-agent extension config keyed by extension ID. Each value is an opaque `JsonElement` blob deserialized by the extension itself.

### AgentDescriptor — `AgentDescriptor.cs:109-110`

```csharp
public IReadOnlyDictionary<string, JsonElement> ExtensionConfig { get; init; } =
    new Dictionary<string, JsonElement>();
```

The domain model's extension config bag. Populated at build time, read at runtime.

### Builder — `PlatformConfigAgentSource.cs:95`

```csharp
ExtensionConfig = agentConfig.Extensions ?? new Dictionary<string, JsonElement>()
```

**This is the merge insertion point.** Currently copies agent-level config directly — no fallback to world defaults.

### `ResolveExtensionConfig<T>()` — `InProcessIsolationStrategy.cs:387-399`

```csharp
private static T? ResolveExtensionConfig<T>(AgentDescriptor descriptor, string extensionId) where T : class
{
    if (descriptor.ExtensionConfig.TryGetValue(extensionId, out var element))
    {
        try { return JsonSerializer.Deserialize<T>(element.GetRawText()); }
        catch { /* invalid config — use defaults */ }
    }
    return null;
}
```

Simple lookup + deserialize. Called for `botnexus-skills` (line 188), `botnexus-mcp` (line 193), `botnexus-mcp-invoke` (line 204), `botnexus-web` (line 213). **No changes needed** — once the merge produces the effective config upstream, this method works transparently.

### Existing World/Agent Override Pattern — `PlatformConfigAgentSource.cs:165-178`

```csharp
private FileAccessPolicy? MapFileAccessPolicy(FileAccessPolicyConfig? agentLevel, FileAccessPolicyConfig? worldLevel)
{
    var effective = agentLevel ?? worldLevel; // Agent wins wholesale
    ...
}
```

`FileAccess` uses a simple "agent wins wholesale" pattern (no deep merge). The extension config improvement is more sophisticated — it needs recursive deep merge. Different pattern, but validates that the team already follows the convention of world defaults + agent overrides in `PlatformConfigAgentSource`.

### Schema Generation — `PlatformConfigSchema.cs:114-124`

Schema is auto-generated from C# types via `JsonSchema.FromType<PlatformConfig>()`. Adding `Defaults` to `ExtensionsConfig` will automatically appear in the schema. The `config schema` CLI command regenerates it.

### Consumers of Extension Config

| Extension ID | Config Type | File |
|---|---|---|
| `botnexus-skills` | `SkillsConfig` | `Extensions.Skills\SkillsConfig.cs` |
| `botnexus-mcp` | `McpExtensionConfig` | `Extensions.Mcp\McpExtensionConfig.cs` |
| `botnexus-mcp-invoke` | `McpInvokeConfig` | (in McpInvoke extension) |
| `botnexus-web` | `WebToolsConfig` | (in WebTools extension) |

All consumers are downstream of `AgentDescriptor.ExtensionConfig` — none need changes.

## Architectural Decisions

1. **Add `Defaults` property to `ExtensionsConfig`** in `PlatformConfig.cs`. Type: `Dictionary<string, JsonElement>?`. This sits alongside `Path` and `Enabled` under `gateway.extensions`.

2. **Create `ExtensionConfigMerger` as a static utility class** in `BotNexus.Gateway\Configuration\ExtensionConfigMerger.cs`. Internal to the Gateway assembly. Contains a single public method `Merge()` plus a private recursive `DeepMergeJsonElement()` helper.

3. **Use `JsonNode` API internally for deep merge.** Convert `JsonElement` → `JsonNode` via `JsonNode.Parse(element.GetRawText())`, perform recursive merge on `JsonNode` tree, then convert result back to `JsonElement` via `JsonDocument.Parse(node.ToJsonString()).RootElement.Clone()`. The `Clone()` call is essential — it produces a `JsonElement` that survives the `JsonDocument` disposal.

4. **Merge insertion point: `PlatformConfigAgentSource.LoadFromConfig()`** at line 95. Change from:
   ```csharp
   ExtensionConfig = agentConfig.Extensions ?? new Dictionary<string, JsonElement>()
   ```
   To:
   ```csharp
   ExtensionConfig = ExtensionConfigMerger.Merge(
       platformConfig.Gateway?.Extensions?.Defaults,
       agentConfig.Extensions)
   ```

5. **Null semantics:** `JsonValueKind.Null` from the agent override replaces the world default (explicit nullification). Absent keys inherit from world defaults.

6. **Array semantics:** Arrays are replaced wholesale (agent array replaces world array). No element-level merging. This matches the spec and avoids unbounded complexity.

7. **Schema regeneration** happens automatically via the NJsonSchema reflection pipeline. Run `dotnet run --project src\gateway\BotNexus.Cli -- config schema` after the code change to update `docs\botnexus-config.schema.json`.

8. **No changes to `ResolveExtensionConfig<T>()`** or any extension consumer. The merge is fully upstream.

## Risk Analysis

### 1. Backward Compatibility — LOW

`Defaults` is an additive property. Existing configs that lack `gateway.extensions.defaults` pass `null` to the merge function, which returns the agent-level config unchanged. Zero behavioral change for existing setups.

**Mitigation:** Unit test with `worldDefaults: null` confirming identity behavior.

### 2. JsonElement Immutability / Serialization Round-Trip — MEDIUM

Deep merge requires `JsonElement` → `JsonNode` → merge → `JsonElement` round-trip. This introduces serialization overhead and potential edge cases with:
- `JsonElement` values that reference disposed `JsonDocument` parents
- Very large config blobs (unlikely for extension config)
- Unicode escaping differences between round-trips

**Mitigation:** The round-trip happens only at agent descriptor build time (startup + config reload), not per-request. Verify via tests that round-tripped `JsonElement` values deserialize identically to originals.

### 3. Performance — LOW

`LoadFromConfig()` runs at:
- Gateway startup (once)
- `config.json` hot-reload (rare, operator-initiated)

Neither is a hot path. Even with 10 agents × 5 extensions, the merge is microseconds.

**Mitigation:** No action needed. Document that merge is build-time only.

### 4. Array Replace Semantics — LOW (document clearly)

Arrays are replaced wholesale per the spec. This is correct for most fields but could be surprising if a user expects `SkillsConfig.AutoLoad` to merge lists. For example, if world default has `AutoLoad: ["a", "b"]` and agent override has `AutoLoad: ["c"]`, the agent gets only `["c"]`, not `["a", "b", "c"]`.

**Mitigation:** Document this behavior in extension development docs. If users need additive arrays, they must repeat all values at agent level.

### 5. Empty Object vs. Absent — LOW

An agent with `"extensions": {}` (empty object) should still inherit all world defaults. An agent with `"extensions": { "botnexus-skills": {} }` should get world defaults for skills, merged with an empty override (effectively inheriting everything). The merge function handles this naturally: empty object merges produce the world default.

**Mitigation:** Unit test with empty agent override confirming full inheritance.

## Wave Plan

This is a focused improvement — **one wave** with parallel agent tracks.

### Wave 1: Implementation + Tests + Docs

| Agent | Track | Deliverables | Files |
|-------|-------|-------------|-------|
| **Farnsworth** | Core | 1. Add `Defaults` to `ExtensionsConfig`<br>2. Create `ExtensionConfigMerger`<br>3. Update `PlatformConfigAgentSource` merge call<br>4. Regenerate JSON schema | `src\gateway\BotNexus.Gateway\Configuration\PlatformConfig.cs` (add `Defaults` property to `ExtensionsConfig` at line ~265)<br>`src\gateway\BotNexus.Gateway\Configuration\ExtensionConfigMerger.cs` (new file)<br>`src\gateway\BotNexus.Gateway\Configuration\PlatformConfigAgentSource.cs` (line 95)<br>`docs\botnexus-config.schema.json` (regenerated via CLI) |
| **Hermes** | Tests | 1. `ExtensionConfigMerger` unit tests (8 cases)<br>2. `PlatformConfigAgentSource` integration tests with defaults | `tests\BotNexus.Gateway.Tests\Configuration\ExtensionConfigMergerTests.cs` (new file)<br>`tests\BotNexus.Gateway.Tests\PlatformConfigAgentSourceTests.cs` (add 2 new test methods) |
| **Kif** | Docs | 1. Update extension development docs with world defaults section | `docs\extension-development.md` (add "World-Level Extension Defaults" section) |

**Bender** (Runtime) and **Scribe** (Logging) — not needed. No runtime execution or orchestration changes.

**Sequencing:** Farnsworth starts first. Hermes can begin writing test scaffolding in parallel but must wait for Farnsworth's `ExtensionConfigMerger` API to stabilize before finalizing assertions. Kif works fully in parallel.

---

### Farnsworth — Detailed Instructions

#### Step 1: Add `Defaults` to `ExtensionsConfig`

In `src\gateway\BotNexus.Gateway\Configuration\PlatformConfig.cs`, update the `ExtensionsConfig` class (around line 258):

```csharp
public sealed class ExtensionsConfig
{
    public string? Path { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// World-level default extension configuration, keyed by extension ID.
    /// Deep-merged with agent-level overrides to produce effective config per agent.
    /// </summary>
    public Dictionary<string, JsonElement>? Defaults { get; set; }
}
```

#### Step 2: Create `ExtensionConfigMerger`

New file: `src\gateway\BotNexus.Gateway\Configuration\ExtensionConfigMerger.cs`

Public API:
```csharp
internal static class ExtensionConfigMerger
{
    /// <summary>
    /// Deep-merges world-level extension defaults with agent-level overrides.
    /// Agent values win on leaf conflicts. Objects merge recursively.
    /// Arrays and scalars are replaced wholesale by the agent override.
    /// </summary>
    public static Dictionary<string, JsonElement> Merge(
        Dictionary<string, JsonElement>? worldDefaults,
        Dictionary<string, JsonElement>? agentOverrides)
}
```

Implementation approach:
1. If both `null` → return empty `Dictionary<string, JsonElement>`
2. If only one is non-null → clone and return it
3. If both non-null → iterate union of keys from both dictionaries. For each extension ID:
   - Present only in world → use world `JsonElement`
   - Present only in agent → use agent `JsonElement`
   - Present in both → call `DeepMergeElement(worldElement, agentElement)`
4. `DeepMergeElement`: If both are `JsonValueKind.Object`, convert to `JsonNode`, recursively merge properties (agent wins on conflict), convert back to `JsonElement` via `JsonDocument.Parse(...).RootElement.Clone()`. Otherwise, return agent value (scalars and arrays replace wholesale).

#### Step 3: Update `PlatformConfigAgentSource`

In `src\gateway\BotNexus.Gateway\Configuration\PlatformConfigAgentSource.cs`, change line 95 from:
```csharp
ExtensionConfig = agentConfig.Extensions ?? new Dictionary<string, JsonElement>()
```
To:
```csharp
ExtensionConfig = ExtensionConfigMerger.Merge(
    platformConfig.Gateway?.Extensions?.Defaults,
    agentConfig.Extensions)
```

#### Step 4: Regenerate Schema

```shell
dotnet run --project src\gateway\BotNexus.Cli -- config schema
```

Verify the generated `docs\botnexus-config.schema.json` now includes `defaults` under the `ExtensionsConfig` definition.

---

### Hermes — Detailed Instructions

#### `ExtensionConfigMergerTests.cs` — Test Cases

New file: `tests\BotNexus.Gateway.Tests\Configuration\ExtensionConfigMergerTests.cs`

| # | Test Name | World | Agent | Expected |
|---|-----------|-------|-------|----------|
| 1 | `Merge_BothNull_ReturnsEmpty` | `null` | `null` | `{}` |
| 2 | `Merge_WorldOnly_ReturnsWorldDefaults` | `{ "ext": { "a": 1 } }` | `null` | `{ "ext": { "a": 1 } }` |
| 3 | `Merge_AgentOnly_ReturnsAgentConfig` | `null` | `{ "ext": { "b": 2 } }` | `{ "ext": { "b": 2 } }` |
| 4 | `Merge_BothPresent_DeepMergesObjects` | `{ "ext": { "a": 1, "b": 2 } }` | `{ "ext": { "b": 3, "c": 4 } }` | `{ "ext": { "a": 1, "b": 3, "c": 4 } }` |
| 5 | `Merge_NestedObjects_MergesRecursively` | `{ "ext": { "nested": { "x": 1 } } }` | `{ "ext": { "nested": { "y": 2 } } }` | `{ "ext": { "nested": { "x": 1, "y": 2 } } }` |
| 6 | `Merge_ScalarOverride_AgentWins` | `{ "ext": { "val": "world" } }` | `{ "ext": { "val": "agent" } }` | `{ "ext": { "val": "agent" } }` |
| 7 | `Merge_ArrayReplace_AgentWins` | `{ "ext": { "list": [1, 2] } }` | `{ "ext": { "list": [3] } }` | `{ "ext": { "list": [3] } }` |
| 8 | `Merge_AgentDisablesExtension_ExplicitFalse` | `{ "ext": { "enabled": true, "x": 1 } }` | `{ "ext": { "enabled": false } }` | `{ "ext": { "enabled": false, "x": 1 } }` |

Use `FluentAssertions` for assertions. Use `JsonSerializer.Deserialize<JsonElement>(json)` to construct test inputs. Compare results by deserializing back and asserting property values.

#### `PlatformConfigAgentSourceTests.cs` — Integration Tests

Add to existing test class:

1. **`LoadAsync_WithWorldDefaults_MergesIntoAgentExtensionConfig`** — Create a `PlatformConfig` with `Gateway.Extensions.Defaults` set and an agent with no `Extensions`. Verify the loaded `AgentDescriptor.ExtensionConfig` contains the world default keys.

2. **`LoadAsync_WithWorldDefaultsAndAgentOverrides_DeepMerges`** — Create a `PlatformConfig` with world defaults and agent-level overrides. Verify deep merge: agent override keys win, non-overlapping world keys are preserved.

---

### Kif — Detailed Instructions

Update `docs\extension-development.md` to add a section titled **"World-Level Extension Defaults"**:

- Explain that operators can define `gateway.extensions.defaults` with shared config
- Show before/after config.json examples (the ones from the spec)
- Document merge semantics: objects merge recursively, scalars and arrays replace
- Note that agents with no `extensions` block inherit everything
- Note that agents can disable with `"enabled": false`

---

## Open Items for User

1. **Explicit `null` override:** Should an agent setting a config key to `null` explicitly override (nullify) the world default, or should `null` be treated as "inherit"? **Recommendation:** `null` overrides (matches JSON semantics). Confirm or override.

2. **Config validation scope:** The spec mentions warning on typo keys as a risk. Should this be in scope for this wave or deferred to a follow-up? **Recommendation:** Defer — keep this change surgical.

3. **`FileAccess` deep merge alignment:** Currently `FileAccess` uses wholesale replacement (`agentLevel ?? worldLevel`). Should we align it with deep merge in a follow-up for consistency? Out of scope but worth noting.
