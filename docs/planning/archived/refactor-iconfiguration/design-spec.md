---
status: proposed
owner: ai
author: Leela
---

# IConfiguration migration design spec

## Summary

BotNexus currently maintains a custom configuration stack around `PlatformConfigLoader`, `PlatformConfigChangeTokenSource`, the static `PlatformConfigLoader.ConfigChanged` event, and multiple ad-hoc JSON parsing steps for `config.json`. Most of that work duplicates what `Microsoft.Extensions.Configuration` and `Microsoft.Extensions.Options` already provide:

- JSON file loading
- typed POCO binding
- `reloadOnChange` file watching
- `IOptionsMonitor<T>` change notifications
- section-based binding for extension options

The migration goal is **not** to change `config.json`. The JSON shape stays the same. The change is the **reader/runtime plumbing**, not the user-facing format.

This migration also fixes the Telegram extension bug: channel adapters are loaded after the original DI registration pass, so `IOptions<TelegramGatewayOptions>` is never populated from `channels.telegram`. With `IConfiguration`, the extension can bind its own section whenever it registers services.

## Current problems

### 1. BotNexus reimplements the configuration system

Today the gateway has custom code for:

- loading JSON from disk
- deserializing into `PlatformConfig`
- validating config
- extracting `agents.defaults`
- capturing raw agent JSON for presence-aware merge
- migrating legacy root-level gateway settings into `gateway`
- watching `config.json`
- debouncing change events
- bridging those events into `IOptionsMonitor<PlatformConfig>`

That logic lives mostly in:

- `PlatformConfigLoader.cs`
- `PlatformConfigChangeTokenSource.cs`
- `GatewayServiceCollectionExtensions.AddPlatformConfiguration()`

### 2. Extensions cannot bind their own options correctly

The root Telegram bug exists because the current system loads a typed `PlatformConfig` once, but extension services are registered later via dynamic assembly loading. By then there is no general-purpose configuration tree available for the extension to bind from.

Result:

- `IOptions<TelegramGatewayOptions>` exists
- but `channels.telegram` is never bound into it
- so the adapter sees default/empty values

### 3. Hot reload is more complex than it needs to be

The current pipeline uses:

- `FileSystemWatcher`
- custom debounce timer
- static `ConfigChanged` event
- custom `IOptionsChangeTokenSource<PlatformConfig>`

All of this is built into the ASP.NET Core configuration stack already.

---

## 1. What gets deleted

These files/classes should go away entirely by the end of the migration:

### Delete completely

1. `src/gateway/BotNexus.Gateway/Configuration/PlatformConfigChangeTokenSource.cs`
   - delete `PlatformConfigChangeTokenSource`

2. `PlatformConfigLoader.PlatformConfigWatcher` nested type in:
   - `src/gateway/BotNexus.Gateway/Configuration/PlatformConfigLoader.cs`

3. `PlatformConfigLoader.ConfigChanged` static event in:
   - `src/gateway/BotNexus.Gateway/Configuration/PlatformConfigLoader.cs`

4. `PlatformConfigLoader.Watch(...)` in:
   - `src/gateway/BotNexus.Gateway/Configuration/PlatformConfigLoader.cs`

5. custom `IOptionsChangeTokenSource<PlatformConfig>` registration inside:
   - `src/gateway/BotNexus.Gateway/Extensions/GatewayServiceCollectionExtensions.cs`

### Delete after final cleanup

6. all remaining manual config-loading code in `PlatformConfigLoader` that is only there because there is no `IConfiguration` root:
   - `Load(...)`
   - `LoadAsync(...)`
   - most raw file reading / deserialization plumbing

Those methods may remain temporarily during the migration, but the end state should not require them for normal gateway startup.

### Likely net deletion estimate

Expected deletion at steady state:

- `PlatformConfigChangeTokenSource.cs`: ~60 lines
- watcher/event/debounce code inside `PlatformConfigLoader.cs`: ~120 lines
- manual load/deserialization path in `PlatformConfigLoader.cs`: ~130-180 lines
- glue code in `GatewayServiceCollectionExtensions.AddPlatformConfiguration()`: ~70-120 lines
- event subscription glue in `PlatformConfigAgentSource`: ~15-25 lines

**Estimated net deletion:** roughly **400-500 lines**, with a larger gross simplification if the validation/migration helpers are also consolidated.

If the team later removes more compatibility helpers from `PlatformConfigLoader`, the total reduction could approach **600+ lines**.

---

## 2. What gets simplified

These files should shrink significantly, even if they do not disappear.

### `src/gateway/BotNexus.Gateway/Configuration/PlatformConfigLoader.cs`

Keep only the parts that still add BotNexus-specific value:

- validation (`Validate`, `ValidateWarnings`)
- legacy root-level gateway migration helper
- `agents.defaults` extraction helper
- path helpers if still useful

Remove:

- file reading
- `JsonSerializer.Deserialize` startup path for normal runtime usage
- custom watcher
- static events
- debounce timer

This file should become a **config normalization + validation helper**, not the primary configuration runtime.

### `src/gateway/BotNexus.Gateway/Extensions/GatewayServiceCollectionExtensions.cs`

`AddPlatformConfiguration()` simplifies from “build an options monitor manually” to “bind from `IConfiguration` and register a few post-configure / validator pieces”.

It should stop:

- loading config with `PlatformConfigLoader.Load(...)`
- creating `PlatformConfigChangeTokenSource`
- starting a separate watcher
- replacing `PlatformConfig` singleton from custom monitor state

It should start:

- binding `PlatformConfig` from `IConfiguration`
- registering `PostConfigure<PlatformConfig>` for normalization
- registering validation through `IValidateOptions<PlatformConfig>` or `OptionsBuilder.Validate(...)`
- using `IOptionsMonitor<PlatformConfig>` directly for reload-aware services

### `src/gateway/BotNexus.Gateway.Api/Program.cs`

Program startup becomes cleaner:

- the JSON file should be added to `builder.Configuration`
- startup should read config from the configuration root, not through a separate loader path
- CORS / cron / startup wiring should read bound options or typed snapshots from DI

### `src/gateway/BotNexus.Gateway/Configuration/PlatformConfigAgentSource.cs`

This should stop listening to the static `PlatformConfigLoader.ConfigChanged` event and instead use `IOptionsMonitor<PlatformConfig>.OnChange(...)`.

The core agent descriptor projection stays, but the watch path becomes much smaller and more idiomatic.

### `src/extensions/BotNexus.Extensions.Channels.Telegram/TelegramChannelAdapter.cs`

No major code deletion inside the adapter itself, but the adapter can finally rely on properly bound `IOptions<TelegramGatewayOptions>` / `IOptionsMonitor<TelegramGatewayOptions>`.

---

## 3. What stays

These pieces are still valuable and should remain.

### Keep

1. `src/gateway/BotNexus.Gateway/Configuration/PlatformConfig.cs`
   - The POCO model still has value as the canonical typed representation.
   - It may gain small adjustments for binding, but it should remain.

2. `src/gateway/BotNexus.Gateway/Configuration/PlatformConfigAgentWriter.cs`
   - `IConfiguration` reads config; it does not provide BotNexus-specific write/update semantics.
   - The CLI/API still need targeted JSON editing for agent entries.

3. `src/gateway/BotNexus.Gateway/Configuration/PlatformConfigWriter.cs`
   - Still needed for atomic writes to `config.json`.
   - Still needed by CLI/API workflows that modify sections directly.

4. `ConfigBackupService`
   - `IConfiguration` has no opinion on backups.
   - This remains BotNexus-specific operational value.

5. BotNexus-specific validators
   - `PlatformConfigSchema.ValidateObject(config)`
   - `PlatformConfigLoader.Validate(config)`
   - agent validation and location validation

   These validations are domain rules, not framework duplication.

6. `AgentConfigMerger`
   - The framework can bind objects, but it does not understand BotNexus semantics like:
     - `agents.defaults`
     - presence-aware field-level merges
     - world-level file access merged with agent-level overrides

7. CLI config mutation logic
   - The CLI writes `config.json` directly and should keep doing that.

8. `PlatformConfigAgentSource`
   - keep the class, but simplify its reload subscription mechanism
   - it still provides domain-specific projection from config POCOs to `AgentDescriptor`

---

## 4. Migration plan

This must be incremental. Each PR should build, pass tests, and keep the gateway working.

### PR 1: Put `config.json` into the real configuration pipeline

**Goal:** make `builder.Configuration` own `config.json`.

Changes:

1. Add the BotNexus config file to the host configuration pipeline in `Program.cs`:
   - resolve the same config path currently used by `PlatformConfigLoader`
   - call `builder.Configuration.AddJsonFile(path, optional: true, reloadOnChange: true)`

2. Keep `PlatformConfigLoader` in place for now.

3. Do not change downstream consumers yet.

Why this is safe:

- no behavior change for services yet
- introduces the standard reload-capable configuration root early
- creates the foundation for extension binding

### PR 2: Bind `PlatformConfig` from `IConfiguration`

**Goal:** replace manual `Load + ChangeTokenSource + Watch` with normal options binding.

Changes:

1. In `AddPlatformConfiguration()`, replace custom loading with:
   - `services.AddOptions<PlatformConfig>()`
   - `.Bind(configuration)` or bind from the root section as appropriate

2. Add `PostConfigure<PlatformConfig>` to perform BotNexus normalization:
   - migrate legacy root-level gateway settings into `Gateway`
   - extract `agents.defaults`
   - populate `AgentRawElements`

3. Add validation using options validation:
   - schema validation
   - BotNexus custom validation

4. Replace the singleton `PlatformConfig` registration with the bound current options value.

5. Remove `PlatformConfigChangeTokenSource` registration and remove `PlatformConfigLoader.Watch(...)` startup.

Why this is safe:

- same POCO shape
- same validation rules
- same config file
- reload now comes from the framework instead of the custom watcher

### PR 3: Switch config watchers to `IOptionsMonitor.OnChange`

**Goal:** remove the last dependence on `PlatformConfigLoader.ConfigChanged`.

Changes:

1. Update `PlatformConfigAgentSource.Watch(...)` to subscribe with `configOptions.OnChange(...)`
2. Remove all static event usage
3. Delete `PlatformConfigChangeTokenSource.cs`
4. Delete `PlatformConfigLoader.ConfigChanged`
5. Delete `PlatformConfigLoader.Watch(...)`
6. Delete nested watcher implementation

Why this is safe:

- agent reload behavior is preserved
- only the event source changes

### PR 4: Bind extension/channel options from `IConfiguration`

**Goal:** fix Telegram and establish the pattern for all extensions.

Changes:

1. Expose `IConfiguration` to extension registration paths
2. In Telegram extension registration, bind:
   - `services.Configure<TelegramGatewayOptions>(configuration.GetSection("channels:telegram"))`

3. Consider moving the adapter from `IOptions<T>` to `IOptionsMonitor<T>` if live reload of Telegram config is desired

Why this is safe:

- does not change JSON
- only enables proper section binding for extension-owned option types

### PR 5: Simplify startup consumers

**Goal:** stop manually loading startup config in `Program.cs`.

Changes:

1. Remove `startupPlatformConfig = PlatformConfigLoader.Load(...)`
2. Read bound config through DI / configuration
3. Update CORS / cron / startup logging to consume typed options cleanly

Why this is safe:

- by this point binding already works end-to-end
- startup logic stops forking around the configuration system

### PR 6: Final cleanup of `PlatformConfigLoader`

**Goal:** leave only normalization/validation helpers or rename the file to reflect its new role.

Changes:

1. Remove obsolete load helpers
2. Keep only validation / normalization helpers
3. Optionally rename the class to something like:
   - `PlatformConfigNormalizer`
   - `PlatformConfigValidation`

Why this is safe:

- pure cleanup after the runtime is already migrated

---

## 5. Config JSON shape

## Decision

**`config.json` does not need to change.**

That is a hard requirement and this design preserves it.

### Same JSON stays valid

This includes:

- `gateway` object
- `providers`
- `channels`
- `cron`
- `agents`
- `agents.defaults`
- legacy root-level gateway fields still accepted during migration compatibility

### Why this works

`IConfiguration` is just a reader over the same JSON file. The file format is not coupled to the current custom loader.

The only special handling required is for BotNexus-specific semantics that are not plain one-to-one binding:

- `agents.defaults`
- raw agent JSON capture for presence-aware merge
- legacy root-level gateway keys

Those are handled in normalization/post-configure steps, not by changing the file format.

---

## 6. IConfiguration binding

## Base binding model

Use standard configuration binding for the main POCO:

```csharp
services.AddOptions<PlatformConfig>()
    .Bind(configuration)
    .PostConfigure(PlatformConfigPostConfigure.Normalize)
    .Validate(...);
```

The binding source should be the full BotNexus config root, because `PlatformConfig` matches the root JSON shape.

## `PlatformConfig` POCO binding

These sections bind naturally:

- `gateway` -> `PlatformConfig.Gateway`
- `providers` -> `PlatformConfig.Providers`
- `channels` -> `PlatformConfig.Channels`
- `cron` -> `PlatformConfig.Cron`
- `apiKey` -> `PlatformConfig.ApiKey`
- `agents.<agentId>` -> `PlatformConfig.Agents[agentId]`

## Non-standard shape: `agents.defaults`

This is the biggest special case.

### Problem

`PlatformConfig.Agents` is a dictionary of real agents, but the JSON also contains a reserved pseudo-agent key:

- `agents.defaults`

That key is not a real agent and should not become an `AgentDefinitionConfig` entry used by the runtime.

### Design

After normal binding:

1. read `configuration.GetSection("agents:defaults")`
2. bind it to `AgentDefaultsConfig`
3. store it in `PlatformConfig.AgentDefaults`
4. remove `defaults` from `PlatformConfig.Agents`

This preserves the current runtime semantics.

## Non-standard shape: `AgentRawElements`

### Problem

`AgentConfigMerger.Merge(...)` needs to know whether fields were explicitly present so it can do presence-aware field-level merges.

`IConfiguration` alone does not keep the original JSON element tree in the bound POCO.

### Design options

Use one of these two approaches:

#### Preferred: parse raw JSON once in post-configure

During `PostConfigure<PlatformConfig>`:

1. open the config file path
2. parse the JSON into `JsonDocument`
3. enumerate `agents`
4. clone each non-`defaults` child `JsonElement`
5. populate `PlatformConfig.AgentRawElements`

Why preferred:

- preserves current merge semantics exactly
- isolates raw JSON handling to the one place that truly needs it
- avoids building a custom configuration provider just for raw element capture

#### Alternative: derive presence from `IConfigurationSection`

This is possible but more fragile because it loses exact JSON token handling and is more work for nested/complex nodes.

Recommendation: **do not do this**.

## Merged agent configs

The bind step should not try to apply defaults. Binding should stay literal.

The existing runtime layering remains:

1. bind raw `PlatformConfig`
2. normalize it (`AgentDefaults`, `AgentRawElements`, legacy migration)
3. when agents are projected into `AgentDescriptor`, call:
   - `AgentConfigMerger.Merge(agentDefaults, agentConfig, rawElement)`

That keeps the semantics where they already exist and reduces migration risk.

## Legacy root-level gateway settings

Current loader logic accepts historical root-level fields like:

- `listenUrl`
- `defaultAgentId`
- `agentsDirectory`
- `sessionsDirectory`
- `logLevel`
- `apiKeys`
- `sessionStore`
- `compaction`
- `cors`
- `rateLimit`
- `extensions`
- `locations`
- `crossWorld`

That compatibility should remain during migration.

### Design

Run the equivalent of `MigrateLegacyGatewaySettings(...)` during `PostConfigure<PlatformConfig>`:

- if `gateway.<field>` is unset
- and root-level `<field>` exists in raw JSON
- copy it into `PlatformConfig.Gateway`

This keeps older configs working without changing the file format.

---

## 7. Hot reload

## Desired end state

Use the built-in JSON provider with reload support:

```csharp
builder.Configuration.AddJsonFile(configPath, optional: true, reloadOnChange: true);
```

Then consume updates through:

- `IOptionsMonitor<PlatformConfig>`
- `IOptionsMonitor<TelegramGatewayOptions>`
- `IConfiguration.GetReloadToken()` only if a lower-level hook is really needed

## What replaces the custom watcher

### Delete

- `FileSystemWatcher`
- debounce timer
- static `ConfigChanged` event
- custom `PlatformConfigChangeTokenSource`

### Replace with

- JSON configuration provider reload notifications
- `IOptionsMonitor<T>.OnChange(...)`

## Agent hot reload behavior

Current requirement: watch `config.json`, re-register agents without restart.

That still works if `PlatformConfigAgentSource` changes from:

- listening to `PlatformConfigLoader.ConfigChanged`

To:

- listening to `IOptionsMonitor<PlatformConfig>.OnChange(...)`

The agent hosted service should continue to receive updated descriptors and refresh the registry/supervisor state exactly as today.

## Important nuance

The built-in provider may raise multiple reload notifications for a save depending on filesystem/editor behavior. In many apps that is fine, but if agent re-registration is expensive, add debounce at the **consumer** boundary rather than keeping a parallel watcher stack.

Recommended rule:

- first try without debounce
- only add lightweight debounce inside the agent reload hosted service if tests show duplicate reload churn

Do **not** reintroduce a second watcher system.

---

## 8. Extension channel options

This is the root functional win of the migration.

## Current failure mode

- startup loads `PlatformConfig`
- extension assemblies are loaded later via `AssemblyLoadContext`
- Telegram adapter asks for `IOptions<TelegramGatewayOptions>`
- nothing has ever bound `channels.telegram` into that type
- resulting options object is empty/defaulted

## Correct design with IConfiguration

When the Telegram extension registers services, bind its own section:

```csharp
services.Configure<TelegramGatewayOptions>(configuration.GetSection("channels:telegram"));
```

That works regardless of when the extension registration occurs, as long as it receives the host `IConfiguration`.

## Required change to extension loading

The extension registration contract needs access to `IConfiguration`.

At minimum, extension startup should be able to do one of these:

- accept `IConfiguration` directly in a registration method
- accept `IServiceCollection` plus a service-registration context that includes `IConfiguration`
- resolve `IConfiguration` through host-provided extension bootstrap APIs

## JSON binding for Telegram

No JSON change needed.

Example existing shape:

```json
{
  "channels": {
    "telegram": {
      "botToken": "...",
      "agentId": "rusty",
      "pollingTimeoutSeconds": 30,
      "bots": {
        "prod": {
          "botToken": "...",
          "agentId": "rusty"
        }
      }
    }
  }
}
```

That should bind naturally to `TelegramGatewayOptions`.

## Reload behavior for Telegram

If Telegram settings should hot-reload, the adapter should depend on `IOptionsMonitor<TelegramGatewayOptions>` instead of capturing `optionsAccessor.Value` once in the constructor.

Recommendation:

- **minimum fix:** keep `IOptions<T>` if only startup binding is needed
- **better design:** switch to `IOptionsMonitor<T>` and react to changes explicitly if live reconfiguration is supported

---

## 9. CLI

## Current reality

The CLI writes `config.json` directly through writer classes such as:

- `PlatformConfigWriter`
- `PlatformConfigAgentWriter`

That should continue.

## Interaction with IConfiguration

This is a good fit:

1. CLI performs an atomic write to `config.json`
2. JSON configuration provider detects the file change
3. `reloadOnChange: true` reloads configuration
4. `IOptionsMonitor<T>` consumers observe the updated values
5. agent/channel reload logic reacts

So the CLI remains the **writer**, and `IConfiguration` becomes the **reader/reload mechanism**.

## Requirements for safe interaction

1. Writes must stay atomic
   - already handled by the writer classes

2. Partial writes must not be observed
   - writer classes should keep temp-file + move/replace behavior where possible
   - `PlatformConfigWriter` currently writes directly; it should be reviewed to align with the safer atomic pattern already used in `PlatformConfigAgentWriter`

3. Backup behavior remains
   - `ConfigBackupService` still applies

## Recommendation

As part of this migration or immediately after it:

- make `PlatformConfigWriter` use the same temp-file atomic replacement strategy as `PlatformConfigAgentWriter`

That reduces reload-time race risk and makes `reloadOnChange` more reliable.

---

## 10. Risks and gotchas

### 1. `agents.defaults` can accidentally leak into runtime agents

If binding happens but post-normalization does not remove `defaults` from `PlatformConfig.Agents`, the runtime may try to load it as a real agent.

Mitigation:

- keep the existing safety guard in `PlatformConfigAgentSource`
- remove `defaults` during post-configure
- add tests

### 2. Presence-aware merge can regress silently

If `AgentRawElements` is not populated exactly enough, `AgentConfigMerger` may start treating omitted fields as explicit null/empty values or vice versa.

Mitigation:

- preserve raw JSON parsing for agent entries
- add regression tests specifically around defaults + override merge behavior

### 3. Legacy root-level gateway settings may stop working

If startup relies only on direct POCO binding, historical configs that still place gateway fields at the root will regress.

Mitigation:

- keep legacy migration in post-configure
- add tests using old config shapes

### 4. Multiple reload notifications may cause duplicate agent reloads

The built-in provider may fire more than once per save on some systems.

Mitigation:

- test real save flows
- debounce at the consumer only if needed
- do not reintroduce a second file watcher stack

### 5. Singleton startup code may freeze old config values

Some services in `Program.cs` currently read config once at startup and project it into singleton state.

Examples:

- cron options setup
- CORS policy building
- model registry bootstrap behavior

Not all of these can become fully live-reloadable automatically.

Mitigation:

- distinguish startup-only config from live config
- keep startup-only behavior explicit where acceptable
- only promise hot reload for the parts that already support it, especially agent reload

### 6. `IOptions<T>` vs `IOptionsMonitor<T>` confusion in extensions

Binding Telegram correctly fixes startup, but it does not automatically make the adapter react to later config changes if the adapter only reads `options.Value` once in the constructor.

Mitigation:

- decide whether Telegram reload is supported
- if yes, use `IOptionsMonitor<T>` and implement runtime update behavior deliberately

### 7. `PlatformConfigWriter` direct-write path may produce transient invalid reads

Unlike `PlatformConfigAgentWriter`, it currently writes straight to the target file.

Mitigation:

- switch to temp-file + atomic replace

### 8. Validation timing changes

With the old loader, validation happened when loading the file manually. With options binding, validation runs through the options pipeline.

Mitigation:

- ensure invalid config still fails fast where required
- ensure reload errors are logged clearly and do not corrupt the last good runtime state unexpectedly

### 9. Dynamic extension registration contract may need a small API change

To bind `channels.telegram`, extensions need access to host `IConfiguration` during registration.

Mitigation:

- add configuration to the extension registration context rather than inventing per-extension hacks

---

## Recommended implementation shape

## Add a post-configure normalizer

Introduce a dedicated class, for example:

- `PlatformConfigPostConfigure : IPostConfigureOptions<PlatformConfig>`

Responsibilities:

1. apply legacy gateway migration
2. extract `AgentDefaults`
3. populate `AgentRawElements`
4. strip `agents.defaults` from runtime agent dictionary

This is the cleanest replacement for the hidden work currently done inside `PlatformConfigLoader.Load(...)`.

## Add an options validator

Introduce a validator class, for example:

- `PlatformConfigOptionsValidator : IValidateOptions<PlatformConfig>`

Responsibilities:

1. run `PlatformConfigSchema.ValidateObject(config)`
2. run `PlatformConfigLoader.Validate(config)`
3. return framework-native validation results

This lets validation stay domain-specific without keeping a custom loading runtime.

---

## Test plan

Minimum regression coverage needed before deleting the old path:

1. **binds config root into `PlatformConfig`**
2. **extracts `agents.defaults` into `PlatformConfig.AgentDefaults`**
3. **removes `defaults` from `PlatformConfig.Agents` runtime dictionary**
4. **captures raw agent JSON into `AgentRawElements`**
5. **preserves merged agent behavior with omitted vs explicit fields**
6. **preserves legacy root-level gateway field compatibility**
7. **reload updates `IOptionsMonitor<PlatformConfig>` after file write**
8. **agent configuration source reloads via `IOptionsMonitor.OnChange(...)`**
9. **Telegram options bind from `channels.telegram`**
10. **CLI write triggers config reload without gateway restart**

---

## Final recommendation

Do the migration in the following order:

1. add `config.json` to the host `IConfiguration` pipeline
2. bind `PlatformConfig` from `IConfiguration`
3. move BotNexus-specific normalization into post-configure
4. move BotNexus-specific validation into options validation
5. replace static config events with `IOptionsMonitor.OnChange(...)`
6. bind extension options from their own config sections
7. clean up the old loader infrastructure

This fixes the Telegram issue early, preserves `config.json`, keeps agent hot reload, and removes a substantial amount of custom infrastructure without requiring a risky big-bang rewrite.
