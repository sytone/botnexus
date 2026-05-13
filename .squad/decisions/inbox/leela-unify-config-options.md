# Decision: Unify Configuration to .NET Provider/Options Pattern

**Author:** Leela (Lead/Architect)
**Date:** 2025-07-24
**Status:** proposed
**Branch:** `dev/223556219+Copilot/refactor-unify-config-options`

---

## Context

BotNexus has two parallel configuration pipelines:

1. **IConfiguration + IOptions/IOptionsMonitor** — the standard .NET pattern. `config.json` is already added via `AddJsonFile(path, reloadOnChange: true)` in `Program.cs`. `PlatformConfig` is bound via `AddOptions<PlatformConfig>().Bind(configuration)` and post-configured in `PlatformConfigPostConfigure`. Hot-reload works via `IOptionsMonitor<PlatformConfig>`.

2. **PlatformConfigLoader** — a custom static loader that reads/parses `config.json` independently using `JsonSerializer.Deserialize<PlatformConfig>`. Used by every CLI command, some API controllers, and startup bootstrapping.

Config writes similarly have two channels:
- `PlatformConfigWriter` / `PlatformConfigAgentWriter` — proper writers with locking, backup, atomic file moves.
- Raw `File.WriteAllTextAsync` calls in CLI commands (`AgentCommands`, `ConfigCommands`, `LocationsCommand`, `ProviderCommand`).

This creates duplication, inconsistency, and bypasses the reload pipeline.

---

## Audit Findings

### GAP-1: CLI commands bypass IOptions entirely

**Files:** `AgentCommands.cs`, `ConfigCommands.cs`, `LocationsCommand.cs`, `ProviderCommand.cs`, `MemoryCommands.cs`, `DoctorCommand.cs`, `ValidateCommand.cs`

All CLI commands call `PlatformConfigLoader.LoadAsync()` directly and write via `File.WriteAllTextAsync()`. They don't use `PlatformConfigWriter`, get no backup, no locking, and no reload notification.

**Classification:** OFFLINE — CLI runs without DI host. Acceptable for now but writes must be migrated to `PlatformConfigWriter`.

### GAP-2: API controllers call PlatformConfigLoader at request time

**Files:** `ConfigController.cs:119,272`, `LocationsController.cs:363`

Instead of reading from `IOptionsMonitor<PlatformConfig>`, these controllers re-parse `config.json` from disk on every request via `PlatformConfigLoader.LoadAsync()`. This bypasses the options pipeline completely.

**Classification:** RUNTIME — must migrate to `IOptionsMonitor<PlatformConfig>`.

### GAP-3: GatewayAuthManager has its own file I/O for auth.json

**File:** `GatewayAuthManager.cs:187-233`

Reads/writes `auth.json` via raw `File.ReadAllText`/`File.WriteAllText`. This is a separate credential file, not part of `config.json`.

**Classification:** SEPARATE CONCERN — `auth.json` is credentials, not config. Containment is appropriate. No migration to IOptions needed, but writes should use `IFileSystem` (already does) and a dedicated writer with backup support.

### GAP-4: StaticOptionsMonitor bypasses reload for CompactionOptions

**Files:** `GatewayServiceCollectionExtensions.cs:88-91,269-272`

`CompactionOptions` is manually parsed and wrapped in `StaticOptionsMonitor<T>`, which freezes the value at startup. Changes to `gateway.compaction` in `config.json` won't hot-reload.

**Classification:** RUNTIME — migrate to `services.Configure<CompactionOptions>(config.GetSection("gateway:compaction"))`.

### GAP-5: Startup double-loads config

**File:** `Program.cs:59-61`

`config.json` is added to `IConfiguration` via `AddJsonFile`, then immediately re-loaded via `PlatformConfigLoader.Load()` to get `startupPlatformConfig` for inline `Configure<CronOptions>` and other startup decisions.

**Classification:** BOOTSTRAP — the startup code needs some values before DI is built. Acceptable short-term; migrate CronOptions to `services.Configure<CronOptions>(config.GetSection("cron"))`.

### GAP-6: PlatformConfigPostConfigure reads raw file to work around JsonElement binding

**File:** `PlatformConfigPostConfigure.cs:19-21,133-165`

`IConfiguration` cannot bind `JsonElement` fields. PostConfigure re-reads the raw JSON file to populate `Metadata`, `IsolationOptions`, `Extensions` dictionaries. This is a known .NET limitation workaround.

**Classification:** TECHNICAL DEBT — acceptable workaround. Long-term, replace `JsonElement` fields with strongly-typed models to eliminate the re-read.

### GAP-7: CLI writes don't use PlatformConfigWriter

**Files:** `AgentCommands.cs:335`, `ConfigCommands.cs:190`, `LocationsCommand.cs:521`, `ProviderCommand.cs:348,379`

Each CLI command serializes the full `PlatformConfig` object and writes directly. No backup, no locking, no atomic writes.

**Classification:** OFFLINE — must migrate all CLI writes to use `PlatformConfigWriter`.

### GAP-8: CronOptions configured from snapshot, not bound to section

**File:** `Program.cs:84-110`

`CronOptions` is configured by reading `startupPlatformConfig.Cron` at startup, not via `Configure<CronOptions>(config.GetSection("cron"))`. Changes to cron config won't hot-reload.

**Classification:** RUNTIME — migrate to section binding.

---

## Target Architecture

### Reads — Three Tiers

| Tier | When | Pattern | Hot-reload |
|------|------|---------|------------|
| **Runtime** (gateway process) | Normal operation | `IOptionsMonitor<T>` | Yes |
| **Request** (API controllers) | HTTP handlers | `IOptionsMonitor<T>` injected | Yes |
| **Offline** (CLI commands) | No DI host | `PlatformConfigLoader.Load()` | No (acceptable) |

### Writes — Single Writer

All config writes MUST go through `PlatformConfigWriter` (or its agent-specific sibling `PlatformConfigAgentWriter`). These provide:
- File-level locking (`SemaphoreSlim`)
- Pre-write backup via `ConfigBackupService`
- Atomic temp-file-then-move (agent writer)
- `reloadOnChange: true` in `AddJsonFile` handles notification automatically

### Options Binding — All sections bound via IConfiguration

Every config section should be bound via `services.Configure<T>(config.GetSection("..."))`, not manually parsed. Eliminates `StaticOptionsMonitor`.

---

## Migration Rules

### For Bender (implementation)

1. **Remove `PlatformConfigLoader.LoadAsync` calls from API controllers.** Replace with `IOptionsMonitor<PlatformConfig>` injection.
   - `ConfigController.GetEffectiveAgentConfig` — inject `IOptionsMonitor<PlatformConfig>`
   - `ConfigController.Validate` — inject `IOptionsMonitor<PlatformConfig>`
   - `LocationsController.LoadConfigAsync` — inject `IOptionsMonitor<PlatformConfig>`

2. **Migrate CompactionOptions to section binding.** Remove both `StaticOptionsMonitor<CompactionOptions>` registrations in `GatewayServiceCollectionExtensions.cs`. Replace with:
   ```csharp
   services.Configure<CompactionOptions>(config.GetSection("gateway:compaction"));
   ```

3. **Migrate CronOptions to section binding.** Replace the inline `Configure<CronOptions>` lambda in `Program.cs` with:
   ```csharp
   services.Configure<CronOptions>(config.GetSection("cron"));
   ```
   This requires `CronOptions` properties to match the JSON structure or adding a `PostConfigure<CronOptions>` for mapping.

4. **Migrate CLI writes to PlatformConfigWriter.** In `AgentCommands`, `ConfigCommands`, `LocationsCommand`, `ProviderCommand`: construct a `PlatformConfigWriter` and use `UpdateSectionAsync` or `UpdateSectionEntryAsync` instead of `File.WriteAllTextAsync` on the full serialized config.

5. **Remove duplicate `StaticOptionsMonitor<T>` definitions.** There are two: one in `DefaultAgentCommunicator.cs:233` and one in `GatewayServiceCollectionExtensions.cs:332`. Consolidate to one shared utility if still needed (for tests), otherwise remove.

6. **Do NOT migrate CLI reads.** CLI commands run without a DI host. `PlatformConfigLoader.Load/LoadAsync` is the correct pattern for offline reads. Leave as-is.

7. **Do NOT migrate auth.json.** `GatewayAuthManager` manages a separate credential file. It already uses `IFileSystem`. Consider adding backup support later but it's out of scope.

### For Hermes (testing)

1. **Verify hot-reload works end-to-end.** Write an integration test that:
   - Starts gateway
   - Modifies `config.json` on disk
   - Asserts `IOptionsMonitor<PlatformConfig>.CurrentValue` reflects the change within 5 seconds

2. **Verify CompactionOptions reload.** After migration from `StaticOptionsMonitor`, assert that changing `gateway.compaction.preservedTurns` in config.json updates `IOptionsMonitor<CompactionOptions>.CurrentValue`.

3. **Verify CLI writes produce valid config.** After migrating CLI commands to `PlatformConfigWriter`, assert that backup files are created and the written JSON is parseable.

4. **Verify no manual notification needed.** After all migrations, confirm no code calls `IConfigurationRoot.Reload()` manually — `reloadOnChange: true` handles it.

5. **Test coverage for PlatformConfigWriter.** Ensure `UpdateSectionAsync`, `UpdateSectionEntryAsync`, and `RemoveSectionEntryAsync` have tests covering concurrent writes, missing files, and backup creation.

---

## Sequencing

| Phase | Scope | Risk |
|-------|-------|------|
| **Phase 1** | Bind `CompactionOptions` and `CronOptions` via section binding. Remove `StaticOptionsMonitor`. | Low — straightforward DI change |
| **Phase 2** | Migrate API controllers from `PlatformConfigLoader` to `IOptionsMonitor<PlatformConfig>` | Medium — must verify all controller codepaths |
| **Phase 3** | Migrate CLI writes to `PlatformConfigWriter` | Medium — CLI has no DI, writer must be constructed manually |
| **Phase 4** | Clean up: remove unused `PlatformConfigLoader` methods if no longer called at runtime, consolidate `StaticOptionsMonitor` | Low |

---

## What NOT to Change

- **`PlatformConfigLoader.Load/LoadAsync`** — still needed for CLI offline reads and startup bootstrap. Do not delete.
- **`PlatformConfigPostConfigure`** — still needed for `JsonElement` workaround. Do not delete.
- **`GatewayAuthManager` file I/O** — separate concern (credentials, not config). Do not merge into IOptions.
- **`FileAgentConfigurationSource`** — reads per-agent JSON files from agents directory. This is a supplementary config source, not the main `config.json` pipeline.
- **Extension manifest files (`botnexus-extension.json`)** — these are discovery metadata, not runtime config.

---

## Success Criteria

- [ ] Zero calls to `PlatformConfigLoader.LoadAsync` in API controller request paths
- [ ] Zero `StaticOptionsMonitor` in production DI registrations
- [ ] All CLI config writes go through `PlatformConfigWriter`
- [ ] `CompactionOptions` and `CronOptions` hot-reload when config.json changes
- [ ] No manual `IConfigurationRoot.Reload()` calls anywhere
- [ ] All existing tests pass
- [ ] New integration tests for hot-reload behavior
