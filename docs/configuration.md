# BotNexus Configuration Guide

BotNexus uses a hierarchical, dictionary-based configuration model with a unified home directory at `~/.botnexus/` (or `BOTNEXUS_HOME`).

## Table of Contents

1. [Quick Start](#quick-start)
2. [Configuration Hierarchy](#configuration-hierarchy)
3. [Primary Deployment: ~/.botnexus/](#primary-deployment-botnexus)
4. [Project Defaults: appsettings.json](#project-defaults-appsettingsjson)
5. [Configuration Sections](#configuration-sections)
6. [Prompt Templates](#prompt-templates)
7. [JSON Schema Validation](#json-schema-validation)
8. [Hot Reload](#hot-reload)
9. [Extension Configuration](#extension-configuration)
10. [Environment Variable Overrides](#environment-variable-overrides)
11. [Security Best Practices](#security-best-practices)
12. [Examples](#examples)

---

## Quick Start

### Using the CLI (Recommended)

Instead of editing JSON manually, use the `botnexus` CLI to manage configuration:

```powershell
# Initialize home directory
botnexus init

# Set up a provider (interactive wizard)
botnexus provider setup

# List agents
botnexus agent list

# Add an agent
botnexus agent add myagent --provider copilot --model gpt-4.1

# Update a setting
botnexus config set gateway.listenUrl http://localhost:8080

# Validate
botnexus validate
```

The `botnexus provider setup` wizard walks you through provider selection, authentication (OAuth for Copilot, API key for OpenAI/Anthropic), and default model selection.

See [CLI Reference](cli-reference.md) for all available commands.

### Manual Configuration (`~/.botnexus/config.json`)

On first run, BotNexus creates a minimal default config:

```json
{
  "$schema": "https://raw.githubusercontent.com/sytone/botnexus/main/docs/botnexus-config.schema.json",
  "version": 1,
  "providers": {},
  "agents": {},
  "channels": {}
}
```

To add your first provider, run the interactive setup wizard:

```powershell
botnexus provider setup
```

Or add it manually to the `providers` section.

### Canonical document shape and location {#canonical-shape}

These two facts govern **every** JSON sample on this page, and are verified against the
configuration-binding source rather than against another doc page:

| | Canonical form | Verified against |
|---|---|---|
| **Shape** | Flat top-level document, `camelCase` keys, **no** `BotNexus` wrapper | `AddPlatformConfiguration` binds `PlatformConfig` at the configuration **root** (`services.AddOptions<PlatformConfig>().Bind(configuration)`), and `PlatformConfigLoader.Load` deserializes the file straight into `PlatformConfig`. |
| **Location** | `~/.botnexus/config.json` | `BotNexusHome.ResolveHomePath` / `CliPaths.ResolveTarget` |

**A `BotNexus` wrapper is not an accepted alternative — it binds nothing.** `PlatformConfig` has no
`BotNexus` property, and the binder is rooted at the document top level, so keys nested under a
`"BotNexus": { ... }` object are silently ignored and every setting falls back to its default. This is
the silent-failure mode the shape rule exists to prevent: the file parses, the gateway starts, and
nothing you wrote takes effect.

Key **casing** is a different matter and is forgiving: binding is case-insensitive
(`PropertyNameCaseInsensitive = true`, plus `IConfiguration`'s own case-insensitive lookup), so
`"gateway"` and `"Gateway"` both bind. **`camelCase` is nonetheless the canonical and recommended
form** — it matches the CLI's dotted-key contract (`botnexus config get gateway.listenUrl`), the
generated JSON schema, and the document `botnexus init` writes. Use it.

> The one place the token `BotNexus` legitimately appears as a config key is `appsettings.json`'s
> `BotNexus:ConfigPath`, which is a host bootstrap **pointer to** `config.json`. It is not part of the
> `config.json` document, and it takes no other keys beneath it.

### Config path resolution order

The first match wins:

1. `--target <PATH>` (CLI commands) or `BotNexus:ConfigPath` in `appsettings.json` (gateway host)
2. `BOTNEXUS_HOME` environment variable → `$BOTNEXUS_HOME/config.json`
3. Default: `~/.botnexus/config.json` — i.e. `%USERPROFILE%\.botnexus\config.json` on Windows,
   `$HOME/.botnexus/config.json` on macOS/Linux

The path is the **same on every platform**; there is no Windows-specific `%LOCALAPPDATA%` config
location. (`%LOCALAPPDATA%\BotNexus` is where the release installer puts **binaries** — see
[Getting Started](getting-started.md) — not configuration.) `BOTNEXUS_DATA_DIR` separately relocates
writable runtime state (`sessions/`, `logs/`, `tokens/`, `agents/`, `extensions/`, `backups/`) away
from the config root, which is what makes a read-only config mount workable in containers.

An example of the canonical shape:

```json
{
  "version": 1,
  "providers": {
    "github-copilot": {
      "enabled": true,
      "apiKey": "auth:github-copilot",
      "defaultModel": "gpt-4o"
    },
    "openai": {
      "enabled": true,
      "apiKey": "sk-...",
      "defaultModel": "gpt-4o-mini"
    }
  }
}
```

**`ProviderConfig` fields:**

| Field | Type | Description |
|---|---|---|
| `enabled` | `bool` | Whether this provider is active. Disabled providers are hidden from the API. Defaults to `true`. |
| `apiKey` | `string?` | API key value, or `auth:<name>` to reference an OAuth entry in `auth.json`. |
| `baseUrl` | `string?` | Base URL override for OpenAI-compatible endpoints, or catalog file path for `integration-mock`. |
| `defaultModel` | `string?` | Default model id used when an agent does not specify one. |
| `models` | `string[]?` | Allowed model ids. `null` means all registered models; `[]` means none. |
| `input` | `string[]?` | Explicit input modalities (e.g. `["text","image"]`) for models registered from `models`. `null`/`[]` infers modalities from the model family; an explicit declaration always wins. Previously these models were hardcoded text-only, so a vision-capable local model silently discarded every image (#2485). |
| `api` | `string?` | Wire-contract identifier. One of `openai-completions` (default), `openai-responses`, `anthropic-messages`, `integration-mock`. Required when the provider speaks a non-OpenAI-completions contract. |

---

## Configuration Hierarchy

BotNexus follows a **defaults → overrides** pattern:

1. **Defaults** — Built-in constants in code (e.g., `Model = "gpt-4o"`)
2. **Configuration file** — `~/.botnexus/config.json` (or `${BOTNEXUS_HOME}/config.json` when set)
3. **Environment variables** — Override any setting (see [Environment Variable Overrides](#environment-variable-overrides))
4. **Named agent overrides** — Per-agent customization in `Agents.Named` dict

**Example:**
```text
Global Model (config.json) = "gpt-4o"
  ↓
Agent "planner" override (Agents.Named.planner.Model) = "gpt-4-turbo"
  ↓
Environment variable (BotNexus__Agents__Named__planner__Model) = "claude-3-5-sonnet"
  ↓
**Final result:** Claude 3.5 Sonnet for the "planner" agent
```

---

## Primary Deployment: ~/.botnexus/

BotNexus loads user configuration from:

- `~/.botnexus/config.json`
- or `${BOTNEXUS_HOME}/config.json` when `BOTNEXUS_HOME` is set.

On startup, BotNexus creates this structure if it does not already exist:

```text
~/.botnexus/
├── config.json
├── extensions/
│   ├── providers/
│   ├── channels/
│   └── tools/
├── tokens/
├── sessions.sqlite
└── logs/
```

## Project Defaults: appsettings.json

`src/gateway/BotNexus.Gateway.Api/appsettings.json` remains a default/fallback value. ASP.NET Core default config sources load first, then `~/.botnexus/config.json` is loaded and overrides those defaults.

### Configuration Binding

The `BotNexus` section is bound to the `BotNexusConfig` class at startup:

```csharp
// In Gateway/Api startup
var botNexusConfig = new BotNexusConfig();
configuration.GetSection(BotNexusConfig.SectionName).Bind(botNexusConfig);
services.AddSingleton(botNexusConfig);
```

## Configuration Sections

### Root: BotNexusConfig

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Version` | int | `1` | Configuration schema version for forward compatibility |
| `worldId` | string (GUID) | generated | Stable identity of this BotNexus world. Generated and persisted on first gateway start; see [World identity](#world-identity) |
| `ExtensionsPath` | string | `~/.botnexus/extensions` | Path to extension discovery folder (dynamic loading) |
| `Extensions` | ExtensionLoadingConfig | — | Extension loader behavior (signing, max assemblies) |
| `Agents` | AgentDefaults | — | Agent defaults and named agent configurations |
| `Providers` | ProvidersConfig | — | LLM provider registry (Copilot, OpenAI, Anthropic, Azure) |
| `Channels` | ChannelsConfig | — | Social channel integrations (Telegram, Discord, Slack) |
| `Gateway` | GatewayConfig | — | Gateway HTTP server settings |
| `Tools` | ToolsConfig | — | Tool/extension tool settings (exec, web search, MCP) |
| `Api` | ApiConfig | — | OpenAI-compatible REST API (optional) |
| `Cron` | CronConfig | — | Scheduled job execution (agent prompts, system actions, maintenance) |

---

### World identity {#world-identity}

Every BotNexus home carries a `worldId` — a GUID that identifies *this installation*.

```json
{
  "worldId": "9c3a7c1e-4d2b-4f18-9f0a-2c5b6d7e8f90"
}
```

- **Generated once, automatically.** If `config.json` has no `worldId`, the gateway generates one on
  start, persists it, and logs the creation at information level. A home that already has one is left
  untouched — the file is not rewritten at all.
- **Per home, not per machine.** Two gateways run against two different homes on the same box have two
  different world IDs. That is the entire point: it is what lets a store say *"you are not my world"*.
- **Do not copy it between installations.** Cloning a home and keeping its `worldId` makes two worlds
  indistinguishable again.
- **Resolved once and injected.** Runtime code takes the `WorldId` dependency; nothing re-reads the
  `worldId` key from configuration. An architecture fitness test enforces this, because two independent
  derivations of the same value can agree with each other while both being wrong.

To see which world a process believes it is in, run `botnexus doctor` — the **World identity** section
prints the resolved world ID alongside the resolved home path.

---

### Cron: scheduler concurrency

Each scheduler tick collects every due job and executes them concurrently. Because most schedules land on
the hour, many jobs come due on the same tick, and every concurrent `agent-prompt` job is a billed model
turn plus a provider HTTP connection. `CronOptions.MaxConcurrentJobs` bounds how many due jobs run at once (#2670).

> **Bound in code, not from `config.json`.** Only `enabled`, `tickIntervalSeconds` and `jobs` are
> copied out of the `cron` section into the scheduler's options. `maxConcurrentJobs` is a
> `CronOptions` value configured through the standard .NET options pipeline (environment variables
> or an `appsettings` provider), so adding `"maxConcurrentJobs"` to the `cron` block of
> `config.json` has no effect today. It is documented here because it governs observable
> scheduler behaviour.

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `CronOptions.MaxConcurrentJobs` | int | `5` | Maximum number of due jobs the scheduler executes concurrently on a single tick. Jobs beyond the cap **queue and run as slots free** - none are dropped. A value of `0` or less degrades to the default of `5` rather than to unbounded fan-out. |

**Tradeoff.** A low cap protects provider rate limits, cost and box CPU on a saturated tick, but it also
*delays* the later jobs on that tick: with 40 due jobs and a cap of 5, the last job does not start until
seven earlier batches have finished. If your jobs are long-running and latency-sensitive, raise the cap;
if you see provider `429`s or box contention around synchronised schedule boundaries, lower it.

This aggregate cap is independent of the scheduler's per-job lock, which separately prevents two runs of
the *same* job from overlapping. Raising `MaxConcurrentJobs` never allows a single job to run twice
concurrently.

---

### Feature flags (`FeatureManagement`)

Feature flags live in the `FeatureManagement` section of `config.json` and bind through
`Microsoft.FeatureManagement`. Note the section name is **PascalCase**, unlike the rest of the
file - that is the section name the binder reads.

`FeatureManagement` is a modelled property on `PlatformConfig`, so it appears in the generated
[schema](botnexus-config.schema.json) and is accepted by start-up validation. This matters because
the schema root is `additionalProperties: false`: an *unrecognised* top-level key is fatal, not
ignored. The PascalCase name is pinned with an explicit `[JsonPropertyName]` and is deliberately
exempted from the camelCase normalisation applied to every other key - camelCasing it produced
`NoAdditionalPropertiesAllowed: #/featureManagement` and a gateway that would not start (#3036,
fixed in 0.44.0). The flag names *inside* the section are likewise preserved verbatim, because
`Microsoft.FeatureManagement` matches them exactly; rewriting `ConfigStoreShadowMigration` to
`configStoreShadowMigration` would silently unbind the flag while leaving the file looking correct.

Prefer `botnexus config set` or the `FeatureManagement__<Flag>` environment-variable form over
hand-editing: both produce the correct section name and casing, and the environment form is not
subject to `config.json` schema validation at all.

Every flag the platform declares is listed in one place in source (`FeatureFlags.All`), which is
what lets the tooling tell an unrecognised flag from a real one. The current inventory:

| Flag | Default | Effect |
|------|---------|--------|
| `GatewayDevOriginEnforcement` | off | Enforces the browser `Origin` header on keyless (dev-mode) requests, protecting the auto-granted `gateway-dev` admin identity from DNS-rebind and CSRF. See [Dev-Mode Origin Guard](./features/dev-origin-guard.md). |

#### Reading and writing flags

```bash
botnexus config get FeatureManagement.GatewayDevOriginEnforcement
botnexus config set FeatureManagement.GatewayDevOriginEnforcement true
```

#### Absence is an unstated decision, not a setting

A flag missing from `config.json` is not distinguishable, by reading the file, from one that was
deliberately turned off - both evaluate identically at runtime. `botnexus doctor config` therefore
reports every declared flag that is absent and can seed it with its documented default:

```bash
botnexus doctor config          # report absent flags
botnexus doctor config --yes    # seed them explicitly
```

Seeding is behaviour-preserving: the value written is the default that was already being applied
implicitly. An existing value - including a deliberate `false`, and including the richer
`EnabledFor` filter form - is never overwritten.

`doctor config` also reports any key under `FeatureManagement` that matches no declared flag. This
is usually a typo: a misspelled `GatewayDevOriginEnforcment` evaluates as *absent*, so the feature
silently stays at its default while the setting appears to have been applied. Such keys are
reported but never removed automatically, since one may belong to an extension.

When a declared flag is absent at evaluation time, the gateway logs a warning once at startup
naming the flag and the default applied, so the effective state is observable without reading
source.

---

### Configuration store (shadow migration)

A SQLite-backed configuration store is being introduced alongside `config.json` (issues #2646, #2766).
It is **off by default and inert**: with both flags below unset, no new code path runs, nothing is
written, and the gateway serves configuration exactly as it does today.

The rollout is deliberately split across **two independent flags** so that verifying the migration and
depending on it are separate decisions. A single flag would mean the first evidence the migration is
faithful arrives only once the platform already depends on it.

| Flag | Default | Effect |
|------|---------|--------|
| `ConfigStoreShadowMigration` | off | On start, migrate `config.json` into the store and diff the round-trip against the source. **Report only** - this flag can never change which configuration the gateway serves. |
| `ConfigStoreAuthoritative` | off | Read configuration from the store instead of JSON. The shadow diff continues to run as a regression guard. |

There are three valid states, in order:

1. **Both off** - today's behaviour, zero new code paths active. This is the default.
2. **Shadow on, authoritative off** - the migration runs and is verified on every start; JSON remains authoritative.
3. **Both on** - the store is authoritative.

The flags bind through `Microsoft.FeatureManagement` on the same `IConfiguration` that loads
`config.json`, so they are set in the `FeatureManagement` section described
[above](#feature-flags-featuremanagement) - the same mechanism as the
[Dev-Mode Origin Guard](./features/dev-origin-guard.md). Mind the PascalCase section name and the
verbatim flag names; `botnexus config set FeatureManagement.ConfigStoreShadowMigration true` or
`FeatureManagement__ConfigStoreShadowMigration=true` avoid both hazards:

```jsonc
{
  "FeatureManagement": {
    // Verify the migration first. Leave authoritative off until diffs are clean
    // across restarts, config edits, agent additions and extension installs.
    "ConfigStoreShadowMigration": true,
    "ConfigStoreAuthoritative": false
  }
}
```

**Authoritative-without-shadow is refused at startup**, loudly, rather than being silently upgraded:
enabling the store as the read path without the verification path having run would put a migration
nothing has ever checked in front of every configuration read. Note this is deliberately stricter than
the shadow path's own failure policy - a shadow migration that throws must *never* fail startup,
because it is diagnostic; an operator asking for an unverified authoritative store is a configuration
error, which is exactly what startup validation exists to catch.

The store lives at `config.db` in the same directory as `config.json`.

---

### Agents: agent definitions and `agents.defaults`

The `agents` section is a dictionary keyed by **agent id**. One reserved key, `defaults`, holds the
world-level defaults that are field-merged into every agent; every other key defines a named agent.

```json
{
  "agents": {
    "defaults": {
      "toolIds": ["read", "write", "shell"],
      "toolTimeoutSeconds": 300
    },
    "assistant": {
      "displayName": "Assistant",
      "provider": "copilot",
      "model": "gpt-4.1",
      "systemPromptFiles": ["SOUL.md", "IDENTITY.md"],
      "toolIds": ["read", "write", "web_search"],
      "enabled": true
    }
  }
}
```

#### `agents.defaults` properties

Backed by `AgentDefaultsConfig`. Only these four keys exist - a default that is not listed here is not
merged, because there is no property to merge it into.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `toolIds` | array | `null` | Default tool ids inherited by agents that do not set their own `toolIds` |
| `toolTimeoutSeconds` | int? | `300` | Default per-tool timeout in seconds. Five minutes accommodates tools that wait on another agent while still bounding genuinely stuck calls |
| `memory` | object | `null` | Default memory configuration inherited by agents |
| `heartbeat` | object | `null` | Default heartbeat configuration inherited by agents |
| `fileAccess` | object | `null` | Default file access policy inherited by agents |

#### Per-agent properties

Backed by `AgentDefinitionConfig`. Every key below is a real, bound property; a per-agent value
overrides the corresponding `agents.defaults` value.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `provider` | string | `null` | Provider name (for example `copilot`) |
| `displayName` | string | `null` | Human-readable display name shown for this agent in clients |
| `emoji` | string | `null` | Optional emoji shown alongside the agent name |
| `description` | string | `null` | Description of the agent's purpose |
| `model` | string | `null` | Model identifier (for example `gpt-4.1`) |
| `allowedModels` | array | `null` | Model ids this agent may use. Null means unrestricted within the provider allowlist |
| `systemPromptFiles` | array | `null` | Ordered list of files to load as the system prompt. Empty means the default order |
| `systemPromptFile` | string | `null` | Single system prompt file path (legacy - prefer `systemPromptFiles`) |
| `toolIds` | array | `null` | Tool identifiers this agent has access to |
| `toolTimeoutSeconds` | int? | inherits | Per-tool timeout in seconds for this agent |
| `subAgents` | array | `null` | Agent ids this agent can call as sub-agents |
| `subAgentRoles` | array | `null` | Role names this agent can converse with (role-based grants for `agent_converse`) |
| `isolationStrategy` | string | `null` | Isolation strategy name (for example `in-process`) |
| `isolationOptions` | object | `null` | Strategy-specific isolation options |
| `cacheRetention` | string | `null` | Prompt-caching retention policy. Null means the provider default (short) |
| `thinking` | string? | `null` | Agent-level default reasoning level (`minimal`, `low`, `medium`, `high`, `xhigh`, `max`). Agent layer of the three-layer model/thinking/context override stack; `null` inherits the model default. An unsupported value causes the agent to be skipped at config load with a warning |
| `contextWindow` | int? | `null` | Agent-level default context-window size in tokens. Same override stack; validated against the model's advertised sizes |
| `maxConcurrentSessions` | int? | `null` | Maximum concurrent sessions for this agent |
| `metadata` | object | `null` | Agent-level metadata |
| `enabled` | bool | `true` | Whether this agent is enabled and available for routing |
| `kind` | string | `Named` | Only `Named` is accepted from config. `SubAgent` is rejected - sub-agents are runtime-only |
| `memory` | object | `null` | Memory system configuration for this agent |
| `soul` | object | `null` | Soul session lifecycle configuration |
| `heartbeat` | object | `null` | Heartbeat polling configuration |
| `dateTimeInjection` | object | `null` | Datetime injection override for this agent |
| `sessionAccess` | object | `null` | Session access configuration for this agent's session tool |
| `conversationAccess` | object | `null` | Conversation access configuration for this agent's conversation tool |
| `fileAccess` | object | `null` | File access policy for this agent's file tools |
| `shellCommand` | array | `null` | Custom shell command array overriding the gateway-level shell. Element `[0]` is the executable; the agent's command string is appended last |
| `extensions` | object | `null` | Extension-specific configuration keyed by extension id (for example `botnexus-skills`) |
| `toolPolicy` | object | `null` | Tool policy overrides for this agent |

See [Agent Settings Reference](./user-guide/configuration.md#agent-settings-reference) in the user guide
for the nested `memory.*`, `soul.*`, `sessionAccess.*`, `toolPolicy.*` and `fileAccess.*` keys.

> **No `named` sub-dictionary, and no iteration limits.** Agents are keyed directly under `agents`;
> there is no `agents.named` wrapper. There is likewise no `maxToolIterations`, `maxRepeatedToolCalls`,
> `contextWindowTokens`, `enableMemory`, `disallowedTools`, `skills`, `disabledSkills`, `systemPrompt`,
> `workspace`, `timezone` or `cronJobs` key on an agent - none of those bind to anything. Use
> `toolIds` to grant tools (an omitted tool is simply not granted), `toolPolicy.denied` to block one
> outright, `agents.<id>.extensions.botnexus-skills` for skills, `soul.timezone` for the agent's
> timezone, `memory.enabled` for memory, `contextWindow` for the window size, and the top-level `cron`
> section for scheduled jobs.

#### Agent loop safety limits

Per-turn tool execution is bounded by the per-tool timeout above (`toolTimeoutSeconds`, default 300 s),
which is the only agent-loop limit exposed through `config.json`. Cross-agent runaway protection is
separate and lives under the agent-exchange budget (turn budgets, daily caps and a loop-detection
cooldown), not under `agents`.

---

### Providers: ProvidersConfig

Dictionary mapping provider names to provider configurations. Keys are case-insensitive and match extension folder names under `extensions/providers/{name}/`.

```json
{
  "providers": {
    "copilot": { ... },
    "openai": { ... },
    "anthropic": { ... },
    "azure-openai": { ... }
  }
}
```

#### ProviderConfig: Common Properties

All providers inherit these properties:

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Auth` | string | `"apikey"` | Authentication type: `"apikey"` or `"oauth"` |
| `ApiKey` | string | `""` | API key (used if Auth="apikey"; ignored for OAuth) |
| `ApiBase` | string | null | Custom API base URL (useful for proxies, Azure endpoints) |
| `DefaultModel` | string | null | Default model for this provider (e.g., gpt-4o, gpt-4-turbo) |
| `TimeoutSeconds` | int | 120 | Request timeout in seconds |
| `MaxRetries` | int | 3 | Number of retries on transient failure |

#### Copilot Provider: Supported Models

The Copilot provider exposes **26 registered models** organized by API format:

##### Claude Models (Anthropic Messages API)
| Model ID | Name | Reasoning | Context | Max Output | Input Types |
|---|---|---|---|---|---|
| `claude-haiku-4.5` | Claude Haiku 4.5 | No | 200K | 8K | text, image |
| `claude-opus-4.5` | Claude Opus 4.5 | No | 200K | 16K | text, image |
| `claude-opus-4.6` | Claude Opus 4.6 | **Yes** | 1M | 64K | text, image |
| `claude-sonnet-4` | Claude Sonnet 4 | No | 200K | 8K | text, image |
| `claude-sonnet-4.5` | Claude Sonnet 4.5 | No | 200K | 8K | text, image |
| `claude-sonnet-4.6` | Claude Sonnet 4.6 | **Yes** | 200K | 8K | text, image |

##### GPT Models (OpenAI Completions API)
| Model ID | Name | Reasoning | Context | Max Output | Input Types |
|---|---|---|---|---|---|
| `gpt-4o` | GPT-4o | No | 128K | 16K | text, image |
| `gpt-4o-mini` | GPT-4o mini | No | 128K | 16K | text, image |
| `gpt-4.1` | GPT-4.1 | No | 128K | 16K | text |
| `o1` | o1 | **Yes** | 200K | 100K | text, image |
| `o1-mini` | o1-mini | **Yes** | 128K | 65K | text |
| `o3` | o3 | **Yes** | 200K | 100K | text |
| `o3-mini` | o3-mini | **Yes** | 200K | 100K | text |
| `o4-mini` | o4-mini | **Yes** | 200K | 100K | text |

##### GPT-5 Models (OpenAI Responses API)
| Model ID | Name | Reasoning | Context | Max Output | Input Types |
|---|---|---|---|---|---|
| `gpt-5` | GPT-5 | No | 200K | 100K | text |
| `gpt-5-mini` | GPT-5 mini | No | 200K | 100K | text |
| `gpt-5.1` | GPT-5.1 | No | 200K | 100K | text |
| `gpt-5.2` | GPT-5.2 | No | 200K | 100K | text |
| `gpt-5.2-codex` | GPT-5.2-Codex | No | 200K | 100K | text |
| `gpt-5.4` | GPT-5.4 | No | 200K | 100K | text |
| `gpt-5.4-mini` | GPT-5.4 mini | No | 200K | 100K | text |

##### Gemini Models (OpenAI Completions API)
| Model ID | Name | Reasoning | Context | Max Output | Input Types |
|---|---|---|---|---|---|
| `gemini-2.5-pro` | Gemini 2.5 Pro | No | 1M | 8K | text, image |
| `gemini-3-flash-preview` | Gemini 3 Flash Preview | No | 1M | 8K | text, image |
| `gemini-3-pro-preview` | Gemini 3 Pro Preview | No | 2M | 8K | text, image |
| `gemini-3.1-pro-preview` | Gemini 3.1 Pro Preview | No | 2M | 8K | text, image |

##### Grok Models (OpenAI Completions API)
| Model ID | Name | Reasoning | Context | Max Output | Input Types |
|---|---|---|---|---|---|
| `grok-code-fast-1` | Grok Code Fast 1 | No | 131K | 32K | text |

**Configuration Example:**

```json
{
  "providers": {
    "copilot": {
      "auth": "oauth",
      "defaultModel": "claude-opus-4.6",
      "timeoutSeconds": 120,
      "maxRetries": 3
    }
  },
  "agents": {
    "analyst": {
      "model": "gpt-4o",
      "provider": "copilot"
    },
    "researcher": {
      "model": "gpt-5.4",
      "provider": "copilot"
    },
    "coder": {
      "model": "claude-opus-4.6",
      "provider": "copilot"
    }
  }
}
```

**Key Points:**
- **Single Provider**: All Copilot models route through the Copilot provider
- **Model-Aware Routing**: Request model determines which API format handler is used
- **Headers Applied Automatically**: Each model definition includes Copilot-specific headers
- **API Format**: Claude models use Anthropic Messages API; GPT models use OpenAI Completions or Responses API
- **Reasoning Support**: Models marked **Yes** support extended thinking/reasoning modes

---

#### Copilot Provider (OAuth Device Code Flow)

**Folder:** `extensions/providers/copilot/`  
**Auth:** OAuth (no API key required)

```json
{
  "providers": {
    "copilot": {
      "auth": "oauth",
      "defaultModel": "gpt-4o",
      "apiBase": "https://api.individual.githubcopilot.com",
      "oAuthClientId": "Iv1.b507a08c87ecfe98",
      "timeoutSeconds": 120,
      "maxRetries": 3
    }
  }
}
```

**How it works:**
1. On first use, agent prompts user to visit `https://github.com/login/device` and enter a code
2. Token cached at `~/.botnexus/tokens/copilot.json` (encrypted on supported platforms)
3. On subsequent runs, cached token is reused automatically
4. Token is automatically refreshed if expired

**Properties:**
- `Auth`: Must be `"oauth"` (required)
- `DefaultModel`: Any supported Copilot model ID (e.g., `claude-opus-4.6`, `gpt-5.4`, `gpt-4o`)
- `ApiBase`: GitHub Copilot endpoint (fixed: `https://api.individual.githubcopilot.com`)
- `OAuthClientId`: GitHub app client ID (defaults to official BotNexus client ID)

**Copilot API Headers:**

The Copilot API requires specific headers that are automatically applied:

```text
User-Agent: GitHubCopilotChat/0.35.0
Editor-Version: vscode/1.107.0
Editor-Plugin-Version: copilot-chat/0.35.0
Copilot-Integration-Id: vscode-chat
```

These headers identify the client to the Copilot API and enable proper rate limiting and routing.

---

#### OpenAI Provider

**Folder:** `extensions/providers/openai/`  
**Auth:** API Key

```json
{
  "providers": {
    "openai": {
      "auth": "apikey",
      "apiKey": "sk-...",
      "defaultModel": "gpt-4-turbo",
      "apiBase": "https://api.openai.com/v1",
      "timeoutSeconds": 120,
      "maxRetries": 3
    }
  }
}
```

#### Anthropic Provider

**Folder:** `extensions/providers/anthropic/`  
**Auth:** API Key

```json
{
  "providers": {
    "anthropic": {
      "auth": "apikey",
      "apiKey": "sk-ant-...",
      "defaultModel": "claude-3-5-sonnet-20241022",
      "apiBase": "https://api.anthropic.com",
      "timeoutSeconds": 120,
      "maxRetries": 3
    }
  }
}
```

#### Azure OpenAI Provider

**Folder:** `extensions/providers/azure-openai/`  
**Auth:** API Key

```json
{
  "providers": {
    "azure-openai": {
      "auth": "apikey",
      "apiKey": "your-azure-key",
      "defaultModel": "deployment-name",
      "apiBase": "https://your-resource.openai.azure.com/openai/deployments/deployment-name",
      "timeoutSeconds": 120,
      "maxRetries": 3
    }
  }
}
```

---

### Channels: ChannelsConfig

Social channel integrations. Keys in `Instances` dict are case-insensitive and match extension folder names.

```json
{
  "channels": {
    "sendProgress": true,
    "sendToolHints": false,
    "sendMaxRetries": 3,
    "instances": {
      "telegram": { ... },
      "discord": { ... },
      "slack": { ... }
    }
  }
}
```

#### ChannelsConfig Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `SendProgress` | bool | true | Include intermediate agent steps in messages |
| `SendToolHints` | bool | false | Include tool usage hints in responses |
| `SendMaxRetries` | int | 3 | Retry failed message sends up to N times |
| `Instances` | dict | — | Per-channel configuration (Telegram, Discord, Slack, etc.) |

#### ChannelConfig: Individual Channel Settings

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Enabled` | bool | false | Enable this channel (if false, not loaded by dynamic loader) |
| `BotToken` | string | `""` | Bot token/API key for the channel |
| `SigningSecret` | string | `""` | Signing secret (Slack only, for webhook validation) |
| `AllowFrom` | list | `[]` | Whitelist of user/chat IDs allowed to use this channel (empty=all) |

#### Telegram Channel

**Folder:** `extensions/channels/telegram/`

The Telegram adapter binds directly from the `channels:telegram` section (it does **not** use the generic `Channels.Instances` shape above). The real option names come from `TelegramGatewayOptions`:

```json
{
  "channels": {
    "telegram": {
      "botToken": "123456789:ABCdefGHijKlmnoPQRstuvWXYZ",
      "agentId": "my-agent",
      "allowedUserIds": [12345, 67890],
      "allowedChatIds": []
    }
  }
}
```

#### Agent 365 Channel

**Folder:** `extensions/channels/agent365/`

The Agent 365 adapter bridges the Microsoft 365 Agents SDK `Activity` protocol to BotNexus (Register
tier). It binds directly from the `channels:agent365` section (it does **not** use the generic
`Channels.Instances` shape). The real option names come from `Agent365GatewayOptions`. See
[docs/extensions/agent365.md](extensions/agent365.md) for the full surface and the Microsoft.Agents.*
package / Microsoft.Extensions.* pin design note.

```json
{
  "channels": {
    "agent365": {
      "clientId": "${AGENT365_CLIENT_ID}",
      "clientSecret": "${AGENT365_CLIENT_SECRET}",
      "tenantId": "${AGENT365_TENANT_ID}",
      "channelServiceEndpoint": "https://smba.trafficmanager.net/amer/",
      "agentId": "my-agent",
      "inboundRoute": "/agent365/messages"
    }
  }
}
```

| Key | Required | Description |
|-----|----------|-------------|
| `clientId` | yes | Entra application (client) ID of the registered Agent 365 app. |
| `clientSecret` | yes | Entra app client secret (sensitive) for outbound Activity replies. |
| `tenantId` | no | Entra tenant ID. Omit for multi-tenant apps. |
| `channelServiceEndpoint` | no | Base URL outbound activities post to. Defaults to the inbound activity's `serviceUrl`. |
| `agentId` | yes | BotNexus agent ID inbound messages route to. |
| `inboundRoute` | no | HTTP route the message endpoint is hosted on. Defaults to `/agent365/messages`. |

#### Discord Channel

**Folder:** `extensions/channels/discord/`

```json
{
  "channels": {
    "instances": {
      "discord": {
        "enabled": true,
        "botToken": "MzA5NTkyMzAzMTgyNzIzODQw.C_DUbA.Tz3u1NBoI7K-xypwWD",
        "allowFrom": []
      }
    }
  }
}
```

#### Slack Channel

**Folder:** `extensions/channels/slack/`

```json
{
  "channels": {
    "instances": {
      "slack": {
        "enabled": true,
        "botToken": "xoxb-1234567890-1234567890-xxxxxxxxxxxxx",
        "signingSecret": "8f742231b91ee1522d552420d224e9aa",
        "allowFrom": []
      }
    }
  }
}
```

---

### Gateway: GatewayConfig

Gateway HTTP server settings.

```json
{
  "gateway": {
    "host": "0.0.0.0",
    "port": 5005,
    "apiKey": "secret-gateway-key",
    "defaultAgent": "default",
    "broadcastWhenAgentUnspecified": false,
    "heartbeat": {
      "enabled": true,
      "intervalSeconds": 1800
    }
  }
}
```

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Host` | string | `0.0.0.0` | Bind address (0.0.0.0 = all interfaces, 127.0.0.1 = localhost) |
| `Port` | int | 5005 | Listen port for gateway server |
| `ApiKey` | string | null | Optional API key for authentication (recommended for production) |
| `DefaultAgent` | string | null | Default agent name if message has no agent metadata |
| `BroadcastWhenAgentUnspecified` | bool | false | If true, route to all agents when agent not specified |
| `Heartbeat.Enabled` | bool | true | Enable heartbeat/keepalive messages |
| `Heartbeat.IntervalSeconds` | int | 1800 | Heartbeat interval (30 minutes) |
| `WalCheckpointIntervalMinutes` | int | 30 | Minutes between periodic PASSIVE SQLite WAL checkpoints. A TRUNCATE checkpoint also runs on graceful shutdown. Keeps the write-ahead log from growing unbounded on long-lived stores. |
| `TranscriptExport.RedactSecrets` | bool | false | When true, exported session transcripts are passed through the transcript secret redactor so recognised credential shapes are replaced with a placeholder before the transcript leaves the process. Off by default so export output stays byte-identical to historical behaviour unless an operator opts in. Render-time only — never changes what is persisted to the session store. |
| `RateLimit.RequestsPerMinute` | int | 60 | Maximum requests per client per window |
| `RateLimit.WindowSeconds` | int | 60 | Window size in seconds for request counting |
| `RateLimit.MaxEntries` | int | 10000 | Maximum distinct client windows tracked in memory. Bounds the per-client dictionary so a flood of distinct client keys cannot exhaust gateway memory. When full, stale entries are pruned then a non-actively-limiting window is evicted; if none can be freed the request is rejected with 429. Actively rate-limited windows are never evicted (a flood cannot clear an attacker's own throttle). A non-positive value disables the cap. |
| `SignalR.MaximumReceiveMessageSizeBytes` | long | 10485760 (10 MB) | Maximum size of a single inbound SignalR hub frame. Non-positive values fall back to the default. |
| `SignalR.MaximumParallelInvocationsPerClient` | int | 10 | Maximum hub method invocations a single connection may run in parallel. Non-positive values fall back to the default. |
| `SignalR.StreamBufferCapacity` | int | 10 | Maximum items buffered for client upload streams before processing blocks. Non-positive values fall back to the default. |
| `SecretRedaction.Patterns` | string[] | _(none)_ | Additional operator-supplied .NET regular expressions whose matches are replaced with `[REDACTED]`. Applied **in addition to** the built-in credential patterns — never instead of them. Validated at startup (issue #2727). |
| `SecretRedaction.MatchTimeoutMilliseconds` | int | 100 | Per-pattern match timeout for operator patterns, so a catastrophic-backtracking expression cannot hang the logging path. Must be greater than zero. |
| `EnableProviderRequestLogging` | bool | false | When true, every provider HTTP request and response is logged at **Debug** level for observability (issue #453). Auth headers (`x-api-key`, `Authorization`, `Proxy-Authorization`) are always redacted by name, and request/response bodies are additionally passed through the shared `SecretRedactor` so leaked keys/tokens are scrubbed. Non-streamed responses also log a best-effort token `usage` summary and elapsed ms. Streaming (`text/event-stream`) responses log status + headers + duration only — the body is never buffered, so streaming is never broken. Off by default; enable only for debugging unexpected provider responses (never at Info in production). |


#### Trusted per-parent sub-agent budgets

`gateway.subAgents` defines global spawn defaults and ceilings. `parentOverrides` may grant a
specific trusted spawning parent a different timeout policy and optional turn/concurrency limits.
Keys are agent IDs and are matched case-insensitively. Policy selection uses the authenticated
`ParentAgentId` carried by the spawn request; a task, display name, archetype, or mirrored target
cannot select another parent's policy.

```json
{
  "gateway": {
    "subAgents": {
      "defaultTimeoutSeconds": 1800,
      "maxTimeoutSeconds": 1800,
      "defaultMaxTurns": 30,
      "maxTurnsCeiling": 30,
      "maxConcurrentPerSession": 5,
      "parentOverrides": {
        "farnsworth": {
          "defaultTimeoutSeconds": 3600,
          "maxTimeoutSeconds": 3600,
          "defaultMaxTurns": 60,
          "maxTurnsCeiling": 90,
          "maxConcurrentPerSession": 8
        }
      }
    }
  }
}
```

Omitted override fields inherit the global value. Unknown parents use the complete global policy.
Configuration reloads apply to subsequent spawns; running sub-agents retain the policy snapshot
selected when they started. Clamp warning logs include `ParentAgentId` and `PolicyTier` (`global`
or `parent-override`) so operators can audit which authorization tier applied.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `subAgents.defaultTimeoutSeconds` | int | 600 | Timeout used when a spawn omits or supplies a non-positive timeout. |
| `subAgents.maxTimeoutSeconds` | int | 1800 | Global timeout ceiling. |
| `subAgents.defaultMaxTurns` | int | 30 | Turn budget used when omitted. |
| `subAgents.maxTurnsCeiling` | int | 30 | Global turn ceiling. |
| `subAgents.maxConcurrentPerSession` | int | 5 | Global running-child limit per parent session. |
| `subAgents.parentOverrides.<parentAgentId>` | object | none | Trusted partial override of the five budget fields above. |
| `subAgents.workspaceRoot` | string | ` ` (empty) | Temporary root directory under which each sub-agent's isolated workspace is created and later reclaimed. Empty preserves the historical default of `<OS temp>/botnexus-subagent-workspaces`. Supports `~` and environment-variable expansion and is normalized to an absolute path. The gateway (`FileAgentWorkspaceManager`) and the CLI (`botnexus subagent workspace list|prune` plus `doctor`) resolve this through the same shared resolver, so they can never target different directories. |

#### SignalR Hub Limits

The gateway registers its SignalR hub with **explicit** transport limits rather than relying on the framework's implicit defaults (32 KB frame size, 1 parallel invocation, 10 stream-buffer items). The defaults below are intentionally bounded: the inbound frame cap is generous enough to carry base64-encoded inline media via `SendMessageWithMedia` (base64 inflates payloads by ~33%) while still preventing a single frame from exhausting server memory, and the parallel-invocation bound limits how much concurrent work one connection can force on the server.

```json
{
  "gateway": {
    "signalR": {
      "maximumReceiveMessageSizeBytes": 10485760,
      "maximumParallelInvocationsPerClient": 10,
      "streamBufferCapacity": 10
    }
  }
}
```

The `signalR` section is optional — when absent, the secure defaults are applied automatically. Any non-positive override is ignored in favour of the default so a misconfiguration can never disable the bound.


#### Session Cleanup

The gateway runs a periodic `SessionCleanupService` that prunes stale sessions. Beyond the base TTL and closed-session retention, it prunes **near-empty cron "noop wake" sessions**: scheduled cron wakes frequently produce a session with only a wake message (and an optional `NO_REPLY`), which accumulate rapidly and bloat `sessions.db` with rows that are never read again. A cron session is treated as a noop when its id is in the `cron:` namespace and it has at most two persisted messages; such sessions are persisted-then-pruned once their `UpdatedAt` is older than `cronNoopRetention`. This never changes wake or persist behaviour — it only deletes stale near-empty cron sessions after the fact.

```json
{
  "gateway": {
    "sessionCleanup": {
      "sessionTtl": "1.00:00:00",
      "closedSessionRetention": null,
      "cronNoopRetention": "7.00:00:00"
    }
  }
}
```

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `sessionCleanup.sessionTtl` | TimeSpan | `1.00:00:00` (24h) | Age after which an idle session is eligible for cleanup. |
| `sessionCleanup.closedSessionRetention` | TimeSpan? | null | If set, sealed sessions older than this window are pruned. |
| `sessionCleanup.cronNoopRetention` | TimeSpan? | `7.00:00:00` (7 days) | Retention window for near-empty cron noop sessions (`cron:` id, ≤ 2 messages). Sessions older than this are pruned. Set to `null` or a non-positive value to disable noop pruning. Configurable via `gateway:sessionCleanup:cronNoopRetention`. |

The section is optional — when absent, the 7-day cron noop retention default applies. Because the predicate requires ≤ 2 messages, any cron session that did real work (multiple turns or tool calls, which add history rows) is never pruned by this branch.

#### Session Consistency Monitor

The gateway runs a periodic `SessionConsistencyHostedService` that detects and safely repairs persisted session/conversation lifecycle discrepancies through the supported store APIs, never raw SQL (issue #2046). It runs a first pass a short delay after startup (so agent registration, store hydration, and interrupted-turn recovery settle first) and then repeats at a bounded cadence.

Detected invariants:

- **active-session-missing** - a conversation's `ActiveSessionId` references a session that no longer exists. Repair: clear the dangling pointer so the router mints a fresh session on the next inbound.
- **active-session-cron-poison** - a non-cron (human/agent) conversation points at a `cron:` session while a more-recent non-cron session exists in the same conversation. Repair: re-point to the latest non-cron session. Guarded by the live-turn tracker (#2030): if a turn is genuinely executing on the poisoned pointer, the discrepancy is reported but not repaired.
- **stale-active-cron** - a `cron:` session left `Active` past a conservative threshold with no live turn. Repair: seal it via the session store.

Each pass is idempotent and bounded; running it repeatedly on an already-consistent world detects nothing and mutates nothing. Every discrepancy is emitted as a structured log line (invariant name, entity ids, previous state, chosen disposition) so operators can inspect detected and repaired discrepancies without querying SQLite directly.

```json
{
  "gateway": {
    "sessionConsistency": {
      "enabled": true,
      "dryRun": false,
      "checkInterval": "00:30:00",
      "startupDelay": "00:01:00",
      "staleActiveCronThreshold": "06:00:00",
      "maxConversationsPerRun": 5000
    }
  }
}
```

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `sessionConsistency.enabled` | bool | `true` | Master switch. When `false` the monitor does not run and no checks or repairs occur. |
| `sessionConsistency.dryRun` | bool | `false` | Report discrepancies without mutating anything. Use to validate detection before enabling auto-heal. |
| `sessionConsistency.checkInterval` | TimeSpan | `00:30:00` (30m) | How often the periodic consistency check runs. |
| `sessionConsistency.startupDelay` | TimeSpan | `00:01:00` (1m) | Delay after host startup before the first check, allowing registration and recovery to settle. |
| `sessionConsistency.staleActiveCronThreshold` | TimeSpan | `06:00:00` (6h) | Age after which a cron session still marked `Active` with no live turn is sealed. |
| `sessionConsistency.maxConversationsPerRun` | int | `5000` | Upper bound on conversations examined per pass. Non-positive disables the cap. |

The section is optional - when absent, the defaults above apply.

#### Sub-agent workspace sweep

Ephemeral sub-agent workers occasionally leave workspace husks under the **persistent** agents root (`<BotNexus home>/agents/<parent>--subagent--<archetype>--<guid>`). Unlike the temp-root pruning and the manual `doctor` reconciliation of top-level registered agents, these husks have no time-based lifecycle and grow without bound. The gateway runs a periodic `SubAgentWorkspaceSweepHostedService` that automatically reclaims them by age.

The sweep only ever considers directories whose name contains the `--subagent--` marker, so **top-level registered agent workspaces are never touched**. Directories modified within the grace window are always skipped so a live / in-flight worker is never yanked, deletion is confined to the resolved agents root, and reparse points (symlinks / junctions) are never followed or deleted through.

```json
{
  "gateway": {
    "subAgentWorkspace": {
      "enabled": true,
      "retentionHours": 24,
      "graceMinutes": 60,
      "checkInterval": "01:00:00"
    }
  }
}
```

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `subAgentWorkspace.enabled` | bool | `true` | Master switch for the automatic age-based sweep. When false, no sub-agent workspace directories are removed automatically. |
| `subAgentWorkspace.retentionHours` | int | `24` | Idle hours after which a `*--subagent--*` workspace (by last-write time) is eligible for removal. Zero or negative disables removal. |
| `subAgentWorkspace.graceMinutes` | int | `60` | Safety window; a directory modified within this many minutes is always skipped, protecting live workers. |
| `subAgentWorkspace.checkInterval` | TimeSpan | `01:00:00` (1h) | How often the sweep scans the agents root. The sweep also runs once shortly after gateway startup. |

Each sweep pass emits a single log line with the number of directories removed, bytes reclaimed, and directories skipped as recent/unexpired. Configurable via `gateway:subAgentWorkspace:*`.


#### Webhook conversation retention

Conversations created by webhook-triggered automation are provenance-tagged and age out on a faster, dedicated schedule than ordinary human conversations. This policy is **independent** of both the world-level conversation auto-archive gate and webhook *run* retention — the three settings govern different data with their own thresholds. It is opt-in: with `enabled` left `false`, existing deployments see no behaviour change until the policy is explicitly turned on. A periodic sweep archives eligible webhook conversations using two rules: the canonical conversation of a deleted or disabled registration ages out after `disabledRegistrationInactivityDays`, while an orphan conversation (whose registration no longer exists and which is not the registration's pinned conversation) ages out faster after `orphanInactivityDays`.

```json
{
  "gateway": {
    "webhooks": {
      "conversationRetention": {
        "enabled": false,
        "disabledRegistrationInactivityDays": 7,
        "orphanInactivityDays": 1,
        "checkInterval": "01:00:00"
      }
    }
  }
}
```

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `webhooks.conversationRetention.enabled` | bool | `false` | Master switch. Opt-in; when `false` no webhook conversations are archived by this policy. |
| `webhooks.conversationRetention.disabledRegistrationInactivityDays` | int | `7` | Days of inactivity after which the canonical conversation of a deleted or disabled registration becomes eligible for archival. Zero or negative disables this rule. |
| `webhooks.conversationRetention.orphanInactivityDays` | int | `1` | Days of inactivity after which an orphan/race webhook conversation (registration gone, not the pinned conversation) becomes eligible for archival. Zero or negative disables this rule. |
| `webhooks.conversationRetention.checkInterval` | TimeSpan | `01:00:00` (1h) | How often the retention sweep runs. |

Configurable via `gateway:webhooks:conversationRetention:*`.


#### Tool Result Persistence

Large tool results (for example a recursive directory listing or a session-history dump) are otherwise written into `session_history` at full size and re-sent to the model on every subsequent turn — consuming context budget with no ongoing value. The `toolResultPersistence` section caps the size of an individual tool result **at write time**: a result whose UTF-8 byte size exceeds `maxBytes` is truncated on a rune boundary (never splitting a surrogate pair or a multi-byte UTF-8 sequence) and an explicit `[truncated N bytes]` marker is appended before the entry is persisted, so the oversized blob never lands in history nor reaches the next turn's context window.

```json
{
  "gateway": {
    "toolResultPersistence": {
      "enabled": true,
      "maxBytes": 16384
    }
  }
}
```

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `toolResultPersistence.enabled` | bool | `true` | Enables the write-time tool-result size cap. |
| `toolResultPersistence.maxBytes` | int | 16384 (16 KiB) | Maximum UTF-8 byte size of a single persisted tool result. Larger results are truncated with a `[truncated N bytes]` marker. A value of 0 or less disables truncation even when `enabled` is true. |

The section is optional - when absent, the default 16 KiB cap is applied. This is independent of (and complementary to) session compaction: the cap prevents oversized results from ever being persisted, while compaction summarises accumulated context over time.


#### Session Compaction

The `compaction` section tunes when and how a session's history is summarised to stay within the model's context window. Compaction is triggered when **either** of two signals trips (whichever fires first):

- **Token-count threshold** — the estimated LLM-visible token total exceeds `contextWindowTokens × tokenThresholdRatio`.
- **Bloat-aware (largest-entry) threshold** — a *single* visible history entry is at or above `largestEntryBytesThreshold` UTF-8 bytes. A session can accumulate a small number of enormous low-value entries (for example a raw transcript dump or a directory listing) whose total still sits under the token threshold while the visible tail is dominated by dead weight; this signal makes such a session eligible for compaction so the oversized entry gets summarised away instead of being re-sent on every turn.

```json
{
  "gateway": {
    "compaction": {
      "preservedTurns": 3,
      "tokenThresholdRatio": 0.6,
      "contextWindowTokens": 128000,
      "largestEntryBytesThreshold": 65536
    }
  }
}
```

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `preservedTurns` | int | 3 | Number of most recent user turns preserved verbatim (never summarised). |
| `maxSummaryChars` | int | 16000 | Maximum characters for the compaction summary. |
| `tokenThresholdRatio` | double | 0.6 | Fraction of the context window (0.0–1.0) at which the token-count trigger fires. |
| `contextWindowTokens` | int | 128000 | Approximate context window size (tokens) used for the token-count trigger. |
| `largestEntryBytesThreshold` | int | 65536 (64 KiB) | A single visible entry at or above this UTF-8 byte size triggers compaction on its own, independently of the token-count threshold. Measured in bytes (not characters) so multibyte payloads are accounted for accurately. A value of 0 or less disables the bloat-aware trigger, leaving only the token-count threshold. |
| `circuitBreakerCooldownSeconds` | int | 600 | How long the per-session compaction circuit breaker stays open after repeated failures before retrying. |
| `cronLlmIdleTimeoutMs` | int | 60000 (60s) | Stream-setup idle cap (milliseconds) applied to the compaction summarization model call when the resolved candidate is a **cloud** provider. Wires the first-token watchdog so a cloud model that stalls mid-stream (or never emits a first token) is aborted early, well inside the per-attempt `timeoutSeconds` window, so the model fallback chain can still try the next candidate. Intentionally **not** applied to local/self-hosted providers (localhost / 127.0.0.1 - e.g. ollama, vllm, lmstudio, sglang), which are legitimately slow to warm up. A value of 0 or less disables the cap entirely. |

The section is optional — when absent, the defaults above apply. The bloat-aware trigger complements [Tool Result Persistence](#tool-result-persistence): write-time truncation prevents most oversized entries from ever being persisted, while the bloat-aware trigger ensures any already-accumulated (or un-capped) large entry still becomes eligible for summarisation.


#### Conversation Auto-Title

The gateway can auto-generate a short title for a conversation after its first user+assistant exchange, replacing the default `New conversation` label. Titling uses a cheap/fast auxiliary model and is best-effort: failures never affect the turn, and a conversation a user or agent has already titled is never overwritten.

```json
{
  "gateway": {
    "auxiliary": {
      "titling": {
        "enabled": true,
        "model": "gpt-5.6-luna",
        "timeoutSeconds": 30
      }
    }
  }
}
```

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `enabled` | bool | true | Master switch for auto-titling. When false, the gateway never schedules a title-generation call and conversations keep the default title until renamed. |
| `model` | string? | `gpt-5.6-luna` | Auxiliary model ID for title generation (e.g. `gpt-5.6-luna`, `gpt-4o-mini`, `claude-haiku-3-5`). Defaults to a fast non-reasoning model. When null/empty the first registered model is used, which is unsafe with a reasoning model (its completion carries a thinking block and no text, producing an empty title). A persisted `null` from an older install self-heals to the default on the next restart. |
| `timeoutSeconds` | int | 30 | Per-call timeout for the best-effort titling request. A non-positive value falls back to 30. |

The section is optional — when absent, titling is enabled with the defaults above. For backward compatibility `titling` may also be a bare string, which is treated as the model ID.


#### Claim Auditor (anti-fabrication)

The claim auditor is a post-turn verification control that inverts the trust model for side-effecting claims. After a run settles, it scans the agent's final user-facing message for **artifact-shaped claims** — "filed issue #N", "opened PR #N", a GitHub issue/PR URL, "wrote `path`", "sent", "deployed", "ran the audit … all checks passed" — and cross-checks each against the set of tools that were actually invoked during the run. A claim with no plausible backing tool call (for example narrating "I filed issue #1234" when no `shell`/`exec`/`github` tool ran that turn) is reported as an **unbacked claim**.

This exists because every other anti-fabrication guardrail lives in the system prompt — the exact layer that is bypassed when a model drifts. The auditor *verifies* instead of trusting narration, so it catches fabrication even when the prompt instructions were ignored. Detection is deliberately conservative to keep false positives near zero: because GitHub work in BotNexus runs through the generic `shell`/`exec` tools, a genuine `gh issue create` is never flagged; a pure narration with no side-effecting tool always is.

When an unbacked claim is detected, the auditor emits a structured `claimAudit` stream event (observable to clients and logs, not just a prose log line). In `warn` mode the turn is unaffected; in `block` mode the event additionally marks the turn as one that should be blocked.

```json
{
  "gateway": {
    "claimAudit": {
      "enabled": true,
      "mode": "warn"
    }
  }
}
```

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `claimAudit.enabled` | bool | `true` | Enables the post-turn claim auditor. |
| `claimAudit.mode` | string | `"warn"` | Reaction on detecting an unbacked claim: `"warn"` (emit the `claimAudit` signal only) or `"block"` (also mark the turn for blocking). Unrecognised values fall back to `"warn"`. |

The section is optional — when absent, the auditor runs in `warn` mode. Setting `claimAudit.enabled` to `false` turns it off entirely (no scan).


#### Shell Execution Settings

Gateway-level shell settings control the default shell behavior for all agents. Individual agents can override these with per-agent `shellCommand` (see [Agent Configuration](#agentconfig-per-agent-customization)).

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `ShellPreference` | string | `"auto"` | Shell selection mode: `"auto"` (prefer bash, fall back to pwsh), `"pwsh"` (always PowerShell Core), or `"bash"` (always bash). Ignored when `ShellCommand` is set. |
| `ShellCommand` | string[] | `null` | Custom shell command array. Element 0 is the executable, elements 1..N are base arguments passed before the agent command. Overrides `ShellPreference` when set. Must have at least 2 elements to be valid. |

**Configuration example:**

```json
{
  "gateway": {
    "host": "0.0.0.0",
    "port": 5005,
    "shellPreference": "pwsh",
    "shellCommand": ["pwsh", "-NoLogo", "-NoProfile", "-NonInteractive", "-Command"]
  }
}
```

**Resolution order:** Per-agent `shellCommand` > Gateway `ShellCommand` > Gateway `ShellPreference` > Auto detection.

See [Shell Execution Feature Guide](./features/shell-execution.md) for the full technical details including the ArgumentList execution model and troubleshooting.

**API Key Authentication:**
If `ApiKey` is set, clients must include it in request headers:
```text
Authorization: Bearer YOUR-GATEWAY-KEY
```

---

### Tools: ToolsConfig

Tool and extension tool settings.

```json
{
  "tools": {
    "restrictToWorkspace": false,
    "exec": {
      "enable": true,
      "timeout": 60
    },
    "web": {
      "search": {
        "provider": "brave",
        "apiKey": "...",
        "maxResults": 5
      }
    },
    "extensions": {},
    "mcpServers": {
      "filesystem": { ... },
      "github-mcp": { ... }
    }
  }
}
```

#### ToolsConfig Properties

| Property | Type | Description |
|----------|------|-------------|
| `RestrictToWorkspace` | bool | If true, file tools limited to workspace directory |
| `Exec` | ExecConfig | Shell execution tool settings |
| `Web` | WebConfig | Web search and HTTP tools |
| `Extensions` | dict | Per-extension configuration (arbitrary key/value pairs) |
| `McpServers` | dict | MCP server configurations (see below) |

#### Internal Tools (Auto-Registered)

Every agent automatically gets access to these internal tools:

| Tool | Purpose | Default Status | Can Disable |
|------|---------|-----------------|------------|
| `filesystem` | Read/write files on disk | Enabled | Yes |
| `web_fetch` | Fetch content from URLs | Enabled | Yes |
| `send_message` | Send messages via channels | Enabled | Yes |
| `cron` | Schedule periodic tasks | Enabled | Yes |
| `get_datetime` | Get current UTC and timezone-aware local date/time | Enabled | Yes |
| `shell` | Execute shell commands | Enabled if `Exec.Enable=true` | Yes |

**Disabling Tools per Agent:**

There is no `disallowedTools` key. A tool is withheld either by omitting it from the agent's
`toolIds` grant, or - when it must be blocked outright regardless of what grants it - by listing it in
`toolPolicy.denied`:

```json
{
  "agents": {
    "secure-agent": {
      "toolIds": ["read", "web_search"],
      "toolPolicy": {
        "denied": ["shell", "exec"]
      }
    }
  }
}
```

**Tool Logging and Visibility:**

- Tool calls are logged per provider call with the actual model used
- WebUI shows/hides tool calls via the **🔧 Tools** toggle (collapsible summaries)
- Tool arguments are previewed (truncated to 80 chars) with click-to-expand modal

#### ExecConfig

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Enable` | bool | true | Enable shell execution tool |
| `Timeout` | int | 60 | Command timeout in seconds |

#### WebConfig

| Property | Type | Description |
|----------|------|-------------|
| `Search.Provider` | string | Web search provider (currently: "brave") |
| `Search.ApiKey` | string | API key for search provider |
| `Search.MaxResults` | int | Max search results per query (default: 5) |

#### MCP Servers

MCP (Model Context Protocol) servers provide tools to agents. Supports local processes (stdio) and remote HTTP endpoints.

```json
{
  "tools": {
    "mcpServers": {
      "filesystem": {
        "type": "Stdio",
        "command": "npx",
        "args": ["-y", "@modelcontextprotocol/server-filesystem", "/tmp"],
        "toolTimeout": 30,
        "enabledTools": ["*"]
      },
      "github-mcp": {
        "type": "Sse",
        "url": "http://localhost:3001/sse",
        "headers": {
          "Authorization": "Bearer YOUR_TOKEN"
        },
        "toolTimeout": 30,
        "enabledTools": ["*"]
      },
      "trello": {
        "type": "Stdio",
        "command": "node",
        "args": ["./mcp-servers/trello-server.js"],
        "env": {
          "TRELLO_API_KEY": "xxx",
          "TRELLO_API_TOKEN": "yyy"
        },
        "toolTimeout": 30,
        "enabledTools": ["*"]
      }
    }
  }
}
```

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Name` | string | — | Server identifier (matches dict key) |
| `Type` | string | auto | Transport type: `Stdio` (local process), `Sse` (HTTP SSE), `StreamableHttp` (HTTP streaming). Auto-detected from Command/Url. |
| `Command` | string | `""` | Executable or script for local process (Stdio transport) |
| `Args` | list | `[]` | Arguments to pass to Command |
| `Env` | dict | `{}` | Environment variables for the process |
| `Url` | string | `""` | HTTP endpoint for remote MCP server (SSE or streaming) |
| `Headers` | dict | `{}` | HTTP headers (e.g., Authorization) |
| `ToolTimeout` | int | 30 | Timeout for tool calls in seconds |
| `EnabledTools` | list | `["*"]` | Tool names to enable; "*" = all tools |

**Transport Selection:**
- If `Command` is set → Stdio (local process)
- If `Url` is set → SSE or HTTP streaming
- Explicit `Type` field overrides auto-detection

---

### Api: ApiConfig

Optional OpenAI-compatible REST API (for external clients).

```json
{
  "api": {
    "host": "127.0.0.1",
    "port": 8900,
    "timeout": 120.0,
    "enabled": false
  }
}
```

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Host` | string | `127.0.0.1` | Bind address (localhost by default for security) |
| `Port` | int | 8900 | Listen port |
| `Timeout` | double | 120.0 | Request timeout in seconds |
| `Enabled` | bool | false | Enable the API server |

**Note:** The API provides OpenAI-compatible `/v1/chat/completions` endpoint. Keep `Host` as `127.0.0.1` unless behind a proxy or firewall.

---

### CodingAgent: CodingAgentConfig

Configuration for the CodingAgent component (used when running BotNexus as a coding assistant). These settings are loaded from project-local `.botnexus-agent/config.json` or the global user config.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `DefaultShellTimeoutSeconds` | int? | 600 | Default timeout in seconds for shell command execution. Per-call `timeout` argument overrides this. Set to `null` for no timeout. |
| `AllowedCommands` | list | `[]` | Allowlist of shell commands (empty = all allowed) |
| `BlockedPaths` | list | `[]` | Paths the agent cannot read/write |

**Shell timeout example:**

```json
{
  "defaultShellTimeoutSeconds": 300,
  "allowedCommands": [],
  "blockedPaths": []
}
```

**Note:** The `DefaultShellTimeoutSeconds` controls the CodingAgent's `bash` tool timeout. This is separate from `Tools.Exec.Timeout` which controls the Gateway's built-in shell tool. Set to `null` to allow unlimited execution time (process runs until the agent cancels it).

### Telemetry: TelemetryConfig

The optional `telemetry` section controls the in-process OpenTelemetry metrics/tracing plane.

```json
{
  "telemetry": {
    "enabled": true
  }
}
```

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `enabled` | bool | `true` | When `true`, wires the OpenTelemetry `MeterProvider`/`TracerProvider` for the canonical `"BotNexus"` meter scope. When `false`, the `IMetrics` facade is still registered (so call sites resolve) but no OpenTelemetry providers are attached. |

When `enabled` is `true` the in-process metrics plane also exposes a **read endpoint** for local inspection and portal consumption (see below). Remote exporter (OTLP) configuration is available via the off-by-default `exporter` section (see below); by default there is no network egress.

#### Metrics read endpoint

`GET /api/telemetry/metrics` returns a JSON snapshot of the current values of every instrument on the canonical `"BotNexus"` meter scope (the PBI3 hot-path metrics: `botnexus.turns.total`, `botnexus.turn.duration`, `botnexus.tool.calls`, `botnexus.provider.requests`, `botnexus.provider.tokens`, `botnexus.cron.runs`, `botnexus.channel.messages`, `botnexus.sessions.active`, `botnexus.host.starts`, etc.). This lets operators and the portal read metrics locally without standing up an external OpenTelemetry collector.

The snapshot is produced by an in-process `MeterListener` (`MetricsSnapshotCollector`) that accumulates measurements per instrument and per bounded tag-set:

- **Counters / up-down counters** report a running `value` (sum of deltas).
- **Histograms** additionally report `count`, `sum`, `min`, and `max`.
- **Observable gauges** (e.g. `botnexus.sessions.active`) are sampled on demand at request time and report their latest `value`.

Example response:

```json
{
  "generatedAt": "2026-07-11T02:00:00Z",
  "scope": "BotNexus",
  "instruments": [
    {
      "name": "botnexus.turns.total",
      "kind": "counter",
      "unit": "{turn}",
      "description": "Total agent turns processed, tagged by agent, channel, and outcome.",
      "measurements": [
        { "tags": { "agent": "farnsworth", "channel": "signalr", "outcome": "success" }, "value": 12 }
      ]
    }
  ]
}
```

When `telemetry.enabled` is `false` the endpoint still resolves and returns a well-formed empty snapshot.

#### Remote export: exporter (OTLP)

The optional `telemetry.exporter` section ships metrics/traces to an external OpenTelemetry collector. It is **off by default** - a fresh install produces **zero network egress** and no OTLP connection is ever attempted until an operator explicitly sets `type` to `otlp` and provides an `endpoint`. No default endpoint is shipped.

```json
{
  "telemetry": {
    "enabled": true,
    "exporter": {
      "type": "otlp",
      "endpoint": "http://collector.internal:4317",
      "protocol": "grpc",
      "headers": {
        "Authorization": "Bearer <collector-token>"
      },
      "resource": {
        "serviceName": "botnexus",
        "serviceInstanceId": "host-a-1",
        "deploymentEnvironment": "production"
      }
    }
  }
}
```

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `exporter.type` | string | `none` | `none` (no egress, default), `otlp` (export to collector), or `console` (debug: write to process console). |
| `exporter.endpoint` | string | _(unset)_ | OTLP collector endpoint. Required when `type` is `otlp`. Use `http://host:4317` for grpc, `http://host:4318` for http/protobuf. No default is shipped. |
| `exporter.protocol` | string | `grpc` | OTLP wire format: `grpc` or `http/protobuf`. |
| `exporter.headers` | object | `{}` | OTLP request headers (e.g. a collector auth token). **Treated as secrets** - values are redacted wherever config is logged or dumped. |
| `exporter.resource.serviceName` | string | `botnexus` | `service.name` resource attribute. |
| `exporter.resource.serviceInstanceId` | string | _(auto)_ | `service.instance.id` resource attribute. When unset, a stable per-process id is generated so an aggregator can distinguish concurrent instances. Set it explicitly to keep ids stable across restarts. |
| `exporter.resource.deploymentEnvironment` | string | _(unset)_ | `deployment.environment` resource attribute (e.g. `production`, `staging`). Omitted from the resource when unset. |

**Secrets:** `exporter.headers` values are collector credentials. They are never emitted in logs or config dumps - the redacting describer replaces every header value with `[REDACTED]` while preserving header keys.

**Serilog  OTel logs routing** (via `Serilog.Sinks.OpenTelemetry`) is intentionally **deferred** to keep this change scoped to metrics/trace export. Only the OTLP metrics/trace exporter is wired here.

##### Remote-collection quickstart

Point BotNexus at any OTLP-compatible collector (OpenTelemetry Collector, Grafana Alloy, a vendor gateway, etc.):

1. Run a collector with an OTLP receiver. Minimal `otel-collector-config.yaml`:

   ```yaml
   receivers:
     otlp:
       protocols:
         grpc:
           endpoint: 0.0.0.0:4317
   exporters:
     debug:
       verbosity: detailed
   service:
     pipelines:
       metrics:
         receivers: [otlp]
         exporters: [debug]
       traces:
         receivers: [otlp]
         exporters: [debug]
   ```

   ```shell
   docker run --rm -p 4317:4317 -v ${PWD}/otel-collector-config.yaml:/etc/otelcol/config.yaml otel/opentelemetry-collector:latest
   ```

2. Configure BotNexus to export to it (in `~/.botnexus/config.json`):

   ```json
   {
     "telemetry": {
       "exporter": {
         "type": "otlp",
         "endpoint": "http://localhost:4317",
         "protocol": "grpc",
         "resource": { "deploymentEnvironment": "dev" }
       }
     }
   }
   ```

3. Restart the gateway. The canonical `botnexus.*` instruments now flow to the collector, tagged with the `service.name`/`service.instance.id`/`deployment.environment` resource attributes so a downstream aggregator can attribute data per instance.

To disable export again, set `exporter.type` back to `none` (or remove the section) - egress stops immediately.

#### Agent 365 observability export: agent365

The optional `telemetry.agent365` section routes BotNexus OpenTelemetry **spans** (turn / tool-call / provider-invocation, plus their child spans such as sub-agent spawns) directly to the [Microsoft Agent 365 observability](https://learn.microsoft.com/en-us/microsoft-agent-365/developer/observability) ingestion endpoint over raw **OTLP/HTTP**. It is **off by default** - a fresh install never routes any telemetry to Agent 365 until an operator explicitly sets `enabled` to `true` and provides an `endpoint`.

This is a **direct OTLP** integration: BotNexus takes **no dependency** on any `Microsoft.Agents.A365.Observability` SDK. The exporter is attached as an **additional** target alongside (not instead of) the generic `exporter` above, so you can fan telemetry out to both a private collector and Agent 365 at once. The A365 exporter is always sent over `http/protobuf` because the Agent 365 traces route is HTTP-only.

```json
{
  "telemetry": {
    "enabled": true,
    "agent365": {
      "enabled": true,
      "endpoint": "https://agent365.svc.cloud.microsoft/observabilityService/tenants/<tenantId>/otlp/agents/<agentId>/traces?api-version=1",
      "authHeaderValue": "Bearer <access-token>",
      "headers": {
        "x-custom-header": "value"
      },
      "resource": {
        "serviceName": "my-agent",
        "deploymentEnvironment": "production"
      }
    }
  }
}
```

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `agent365.enabled` | bool | `false` | When `true` (and an `endpoint` is set), spans are shipped to Agent 365 over OTLP/HTTP. Off by default: zero egress to Agent 365 until enabled. |
| `agent365.endpoint` | string | _(unset)_ | The Agent 365 observability OTLP/HTTP **traces** endpoint - the fully-qualified `.../otlp/agents/{agentId}/traces?api-version=1` URL. Required when `enabled` is `true`. No default is shipped. Use the S2S route (`/observabilityService/...`) for app-only tokens or the delegated route (`/observability/...`) for user-delegated tokens. |
| `agent365.authHeaderValue` | string | _(unset)_ | Convenience for the `Authorization` request header, e.g. `Bearer <token>`. **Treated as a secret** - redacted wherever config is logged. Acquire the token via MSAL/`Microsoft.Identity.Web` out of band (scope `9b975845-388f-4429-889e-eab1ef63949c/.default` for S2S). |
| `agent365.headers` | object | `{}` | Additional OTLP request headers beyond `Authorization`. **Treated as secrets** - values are redacted wherever config is logged. An explicit `Authorization` key here wins over `authHeaderValue`. |
| `agent365.resource.serviceName` | string | `botnexus` | `service.name` resource attribute reported to Agent 365. |
| `agent365.resource.serviceInstanceId` | string | _(auto)_ | `service.instance.id` resource attribute. Auto-generated per process when unset. |
| `agent365.resource.deploymentEnvironment` | string | _(unset)_ | `deployment.environment` resource attribute. Omitted when unset. |

**Prerequisites (tenant-side):** Agent 365 ingestion requires a licensed tenant, an assigned Microsoft 365 E7 / Microsoft Agent 365 license, tenant consent, and the `Agent365.Observability.OtelWrite` app role/scope granted to your agent app. Without these, ingestion returns `200 OK` but silently drops the data. See the [direct OTel integration guide](https://learn.microsoft.com/en-us/microsoft-agent-365/developer/direct-open-telemetry-integration) for the full auth recipes and verification steps.

**Delegation visibility:** sub-agent spawns surface as **child spans** of the parent turn (standard OTel parent linkage via the ambient `Activity.Current`), so a full agent-to-agent delegation run appears as a single trace tree in Agent 365.

To disable Agent 365 export again, set `agent365.enabled` back to `false` (or remove the section) - egress to Agent 365 stops immediately.


---

## JSON Schema Validation

BotNexus provides a JSON schema for `config.json` at [`docs/botnexus-config.schema.json`](botnexus-config.schema.json). The schema covers all top-level sections (`gateway`, `agents`, `providers`, `channels`, `extensions`, `apiKeys`, `cors`, etc.) and their nested properties.

### Using the Schema

**In your editor:** Add a `$schema` reference to the top of your `config.json` for autocomplete and inline validation:

```json
{
  "$schema": "./docs/botnexus-config.schema.json",
  "gateway": { ... }
}
```

**Generating the schema:** Use the CLI to regenerate the schema from the current `PlatformConfig` model:

```powershell
botnexus config schema
# Output: docs\botnexus-config.schema.json
```

**Validating at the gateway:** Use the `POST /api/config/validate` endpoint (or `botnexus validate --remote`) to validate against the running gateway.

### Error severity: survivability, not scope

Startup validation classifies each error by whether the gateway can run with the configuration that was actually bound (issue #3037):

| Error class | Severity | Why |
|---|---|---|
| Unknown / unmodelled property (`NoAdditionalPropertiesAllowed`) | **Warning, startup continues** | `IConfiguration` binding ignores unmapped keys, so the bound `PlatformConfig` is identical whether the key is present or absent. Refusing to start would change nothing about the configuration in effect. |
| A bound value that is wrong — unparseable enum, out-of-range number, missing required field, failed cross-field rule | **Fatal** | These change or invalidate the object the gateway will actually use. |
| Anything scoped to `agents.defaults` | **Fatal** | Those values seed every agent. |
| Anything scoped to a single named agent (`agents.<id>.*`) | **Quarantined** | The bad descriptor is skipped with a warning; the remaining agents load. |

An unmodelled key is never silently ignored: the gateway emits exactly one warning naming the offending property path, so an operator learns the key is doing nothing. This also gives forward compatibility — a `config.json` written by a newer BotNexus degrades to "these keys mean nothing to me" rather than preventing an older gateway from starting.

---

## Hot Reload

BotNexus monitors `~/.botnexus/config.json` for changes and applies most configuration updates **live** — no Gateway restart required. The `ConfigReloadOrchestrator` (a background service) uses `IOptionsMonitor` to detect changes, debounces rapid edits (500 ms), and applies updates to the appropriate subsystems.

### What reloads automatically

| Setting | Effect |
|---------|--------|
| `Agents.Named.*` | Agent runners are rebuilt with new model, temperature, prompt, etc. |
| `Agents` defaults (Model, MaxTokens, Temperature, etc.) | All agents inherit updated defaults |
| `Providers.*` | Provider registry is refreshed; new/changed provider configs take effect |
| `Cron.*` | Cron jobs are reloaded (schedules, new jobs, removed jobs) |
| `Gateway.ApiKey` | API key middleware uses the new key immediately |
| `Agents.Named.*.fileAccess` | The agent is re-registered and its path validator is rebuilt from the new allow/deny lists |

Agent re-registration compares the whole effective descriptor - identity, model, prompts, tools, metadata, isolation options, extension config, memory, soul, heartbeat, datetime injection, conversation retention and `fileAccess` - via a single stable fingerprint, so any per-agent field you edit takes effect on the next reload.
| `Agents.Named.*.fileAccess` | The agent is re-registered and its path validator is rebuilt from the new allow/deny lists |

Agent re-registration compares the whole effective descriptor - identity, model, prompts, tools,
metadata, isolation options, extension config, memory, soul, heartbeat, datetime injection,
conversation retention and `fileAccess` - via a single stable fingerprint, so any per-agent field
you edit takes effect on the next reload.

### Nullable Parameters (Provider Defaults)

When `MaxTokens` or `Temperature` are not specified (null), providers use their own defaults:

```json
{
  "agents": {
    "model": "gpt-4o",
    "maxTokens": null,      // Provider uses OpenAI default (e.g., 4096)
    "temperature": null     // Provider uses OpenAI default (e.g., 0.7)
  }
}
```

**Fallback Order:**
1. Agent-specific config (if set)
2. Default agent config (if set)
3. Provider's built-in default

**Benefits:**
- Keeps config minimal (only override when needed)
- Providers can optimize defaults per model
- Easy to test different models without reconfig

**Example:**
```json
{
  "agents": {
      "fast-agent": {
        "model": "gpt-4o",
        "temperature": 0.5     // Set explicitly
        // MaxTokens not set → use OpenAI default
      },
      "creative-agent": {
        "model": "claude-3-5-sonnet",
        // Both MaxTokens and Temperature use Anthropic defaults
      }
    }
  }
}
```

### What requires a restart

| Setting | Reason |
|---------|--------|
| `Gateway.Host` / `Gateway.Port` | Kestrel bind address is set at startup |
| `ExtensionsPath` | Extension assemblies are loaded once at startup |

### Activity Stream Notification

On reload, the Gateway publishes a `gateway.config.reloaded` activity event listing which subsystems were updated. Portal and SignalR clients receive this event in real time.

### CLI Config Commands

The CLI tool provides commands for managing configuration:

```bash
botnexus config validate   # Validate config.json syntax and binding
botnexus config show       # Show resolved config (defaults merged with overrides)
botnexus config init       # Create default config.json interactively
```

---

## Extension Configuration

Extensions are dynamically loaded from the `extensions/` directory. Each extension type (providers, channels, tools) has its own folder structure.

### Folder Structure

```text
extensions/
├── providers/
│   ├── copilot/              # BotNexus.Agent.Providers.Copilot.dll
│   ├── openai/               # BotNexus.Agent.Providers.OpenAI.dll
│   ├── anthropic/            # BotNexus.Agent.Providers.Anthropic.dll
│   └── azure-openai/         # Custom provider
├── channels/
│   ├── telegram/             # BotNexus.Extensions.Channels.Telegram.dll
│   ├── discord/              # BotNexus.Extensions.Channels.Discord.dll
│   └── slack/                # BotNexus.Extensions.Channels.Slack.dll
└── tools/
    ├── github/               # BotNexus.Tools.GitHub.dll
    └── custom-tool/          # Custom tool extension
```

### Extension Registration

Each extension assembly can implement `IExtensionRegistrar` to register types in the DI container. (LLM providers are not currently extension-loaded — they ship as `src/agent/BotNexus.Agent.Providers.*/` projects and are wired in `Program.cs`. The snippet below illustrates the historical extension shape only.)

```csharp
public class CopilotExtensionRegistrar : IExtensionRegistrar
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        // Historical example only — providers are not currently extension-loaded.
        services.AddSingleton<IApiProvider>(sp =>
        {
            var tokenStore = new FileOAuthTokenStore();
            // ... create and return CopilotProvider
        });
        services.AddSingleton<IOAuthTokenStore>(new FileOAuthTokenStore());
    }
}
```

### Extension Config Keys

Configuration keys in `Providers`, `Channels`, `Tools.Extensions`, and `Tools.McpServers` must match the folder name under `extensions/{type}/{name}/`.

**Examples:**
- Config key `"copilot"` → loads from `extensions/providers/copilot/`
- Config key `"telegram"` → loads from `extensions/channels/telegram/`
- Config key `"github"` → loads from `extensions/tools/github/`

Keys are **case-insensitive** for matching but should use kebab-case by convention.

### Extension-Specific Configuration

Extension-specific config is placed in `Tools.Extensions`:

```json
{
  "tools": {
    "extensions": {
      "github": {
        "token": "ghp_...",
        "defaultOwner": "microsoft",
        "apiBase": "https://api.github.com"
      }
    }
  }
}
```

Extensions access their config from the DI container or from the main `BotNexusConfig`.

---

## Environment Variable Overrides

Configuration values can be supplied via environment variables using the standard .NET Configuration
pattern, with double underscores (`__`) separating nested levels:

```text
<top-level-key>__<nested>__<property>=value
```

**There is no `BotNexus__` prefix.** This follows directly from the
[canonical shape](#canonical-shape): `PlatformConfig` binds at the configuration root, so the
environment path starts at a top-level `config.json` key (`providers`, `agents`, `channels`,
`gateway`, ...). Names are case-insensitive, matching the rest of the binder.

### Examples

```bash
# Override Copilot API base
export providers__copilot__apiBase=https://api.githubcopilot.com

# Override the gateway listen URL
export gateway__listenUrl=http://0.0.0.0:9000

# Override Telegram bot token
export channels__telegram__botToken=123456789:ABCdef...

# Override the planner agent's model
export agents__planner__model=gpt-4-turbo
```

### Precedence

Configuration sources are loaded in order - later sources override earlier ones:

1. **Code defaults** (built-in fallbacks, lowest priority)
2. **appsettings.json** (host defaults, including the `BotNexus:ConfigPath` pointer)
3. **appsettings.{Environment}.json** (environment-specific overrides)
4. **Environment variables**
5. **`~/.botnexus/config.json`** (user configuration, highest priority)

> Note the ordering: `config.json` is added to the pipeline **after** the host's default sources, so
> where a key is present in **both**, `config.json` wins over the environment variable. To let an
> environment variable take effect, leave the corresponding key out of `config.json` (or set it to
> empty), as in [Example 4](#example-4-using-environment-variables-for-secrets). The dedicated
> `BOTNEXUS_HOME` / `BOTNEXUS_DATA_DIR` / `BOTNEXUS_API_KEY` variables are read directly from the
> environment and are not subject to this ordering.

---

## Security Best Practices

### 1. Protect Secrets

**Never commit API keys or tokens to source control.**

Options:
- **User secrets** (.NET Core dev only):
  ```bash
  dotnet user-secrets set "providers:openai:apiKey" "sk-..."
  ```
- **Environment variables** (production):
  ```bash
  export BotNexus__providers__openai__apiKey=sk-...
  ```
- **Secret management** (Azure Key Vault, HashiCorp Vault, etc.)

#### Deployment-specific redaction patterns

The gateway scrubs secret-shaped values from transcripts, logs, and cron external delivery using a
built-in set of credential regexes (OpenAI, Anthropic, GitHub, GitLab, AWS, Google, Slack, Stripe,
Telegram, `Authorization` headers, generic `api_key=`). Every deployment also has secret shapes the
platform cannot know about — internal service tokens, deployment identifiers, bespoke API key
formats. Declare those in `~/.botnexus/config.json`:

```json
{
  "gateway": {
    "secretRedaction": {
      "patterns": [
        "deployment-secret-[a-z-]+",
        "cust-[0-9]{6}"
      ],
      "matchTimeoutMilliseconds": 100
    }
  }
}
```

Behaviour and guarantees:

- **Additive only.** Operator patterns are applied *after* the built-in set. Configuring them can
  never disable, replace, or weaken a built-in pattern.
- **Validated at startup, loudly.** A malformed regex, an empty entry, or a pattern that matches the
  empty string (`.*`, `a?` — which would redact all text) is a configuration **error** that names the
  offending pattern and its index. The gateway fails to start rather than running with redaction
  silently degraded.
- **Bounded.** Each operator pattern runs under `matchTimeoutMilliseconds` (default 100ms), so a
  catastrophic-backtracking expression cannot hang the logging path. If a pattern does time out at
  redaction time it is skipped for that string — the built-in patterns have already been applied —
  rather than throwing and taking down the write.
- **Duplicates are harmless.** Exact repeats are de-duplicated; a pattern duplicating a built-in
  simply matches text that was already redacted.

Write patterns that require at least one character and anchor where you can. Prefer
`deployment-secret-[a-z-]+` over `deployment-secret.*`.

### 2. API Key Authentication

For production Gateway deployments, always set `Gateway.ApiKey`:

```json
{
  "gateway": {
    "apiKey": "random-secret-key"
  }
}
```

Clients must provide this key:
```text
Authorization: Bearer random-secret-key
```

### 3. OAuth Token Storage

OAuth tokens are stored encrypted (on supported platforms) at:
```text
~/.botnexus/tokens/{provider-name}.json
```

Ensure the `~/.botnexus/` directory has restrictive permissions:
```bash
chmod 700 ~/.botnexus
```

### 4. File Tool Restrictions

Enable workspace restriction to limit file access:

```json
{
  "tools": {
    "restrictToWorkspace": true
  }
}
```

### 5. Channel Allow Lists

Use `AllowFrom` to whitelist specific users/chats:

```json
{
  "channels": {
    "instances": {
      "telegram": {
        "enabled": true,
        "botToken": "...",
        "allowFrom": ["12345", "67890"]
      }
    }
  }
}
```

### 6. Sensitive Config in Development

Keep local dev secrets out of source control. Prefer the default `~/.botnexus/config.json` (which is
never committed), or supply them via environment variables — see
[Environment Variable Overrides](#environment-variable-overrides):

```json
{
  "providers": {
    "openai": {
      "apiKey": "sk-local-dev-key-here"
    }
  }
}
```

### 7. Streaming Tool-Argument Byte Budget

The provider stream layer is the trust boundary against the model/proxy. Streamed tool-call
argument fragments (`arguments` on OpenAI-compatible APIs, `partial_json` on Anthropic and Copilot
Messages) are accumulated per tool call under a **cumulative UTF-8 byte budget**, so a malicious or
malfunctioning stream cannot grow the process heap without limit (issue #2902).

- Default budget: **1 MiB per tool call**.
- Measured in UTF-8 bytes appended, not UTF-16 characters, so multi-byte payloads cannot exceed the
  intended memory ceiling.
- On overflow, accumulation for that tool call is terminated, a single warning is logged (with
  provider, model, and observed size), and the turn fails with a distinguishable error. A truncated
  argument blob is never emitted as if it were complete.

Override the budget with an environment variable (an absent, unparseable, or non-positive value
falls back to the default - the guard cannot be switched off):

```bash
export BOTNEXUS_STREAM_TOOL_ARGUMENT_MAX_BYTES=2097152   # 2 MiB per tool call
```

This is distinct from `ToolInvocationRecord.MaxArgumentBytes` (16 KiB), which is a display/record
time sanitiser applied after the fact; both use UTF-8 bytes as their unit.

---

## Prompt Templates

Prompt templates are reusable, parameterized prompts stored in configuration or as files. They support `{{parameter}}` placeholders for dynamic substitution and are used by:

- **CLI** — `botnexus prompt render` and `botnexus prompt run` commands
- **Cron jobs** — `agent-prompt` jobs that render templates before execution

### Storage Locations

Templates can be defined in three ways:

1. **Configuration-based** — In `config.json` under `promptTemplates` key
2. **File-based** — In `~/.botnexus/prompts/` directory:
   - **`.prompt.md`** (recommended for multi-line, human-authored prompts) — YAML front matter + Markdown body for readable content
   - **`.prompt.json`** (supported for compatibility and machine-generated templates) — Single-file JSON format

The CLI merges all sources when listing or rendering templates. When both `foo.prompt.md` and `foo.prompt.json` exist with the same name, `.prompt.md` takes precedence.

**Sample Templates:** The CLI ships bundled sample prompt files. Run `botnexus prompt create samples` to copy them into `~/.botnexus/prompts/`, then modify them for your workflow.

### Configuration Structure

```json
{
  "promptTemplates": {
    "template-name": {
      "prompt": "Template body with {{parameter}} placeholders",
      "description": "Optional human-friendly description",
      "defaults": {
        "parameter": "default value"
      },
      "parameters": {
        "parameter": {
          "description": "Optional parameter description",
          "default": "default value",
          "required": false
        }
      }
    }
  }
}
```

### Properties

| Property | Type | Description |
|----------|------|-------------|
| `prompt` | string | Template body. Use `{{name}}` for placeholders (required). |
| `description` | string | Human-friendly description for the CLI `--verbose` output (optional). |
| `defaults` | dict | Simple parameter defaults as key-value pairs (optional). |
| `parameters` | dict | Advanced per-parameter metadata with description, default, and required flag (optional). |

### File-Based Templates: `.prompt.md` Format

For multi-line prompts with headings, bullet lists, numbered lists, and paragraphs, use the **`.prompt.md`** format. This format stores metadata in YAML front matter and prompt content as readable Markdown:

**Format:**

```markdown
---
name: template-name
description: Human-friendly description
parameters:
  parameter_name:
    description: What this parameter is for
    required: true
    default: optional-default-value
---
# Prompt Title

Your multi-line prompt content here.

Use {{parameter}} placeholders for dynamic substitution.

## Sections

- **Bullet lists** are preserved
- **Numbered lists** work too:
  1. First item
  2. Second item

Whitespace, formatting, and structure are maintained as-is in the rendered prompt.
```

**Properties:**

| Property | Type | Description |
|----------|------|-------------|
| `name` | string | Template identifier (optional; defaults to filename stem). |
| `description` | string | Human-friendly description for `--verbose` output (optional). |
| `parameters` | dict | Per-parameter metadata (description, required, default). Same schema as JSON format (optional). |
| _(body)_ | markdown | Everything after the closing `---` is the prompt body. Whitespace and formatting preserved. Use `{{parameter}}` placeholders. |

**Advantages:**

- **Readable** — No escaped newlines or JSON noise
- **Editable** — Author and review multi-line content naturally
- **Structured** — YAML front matter clearly separates metadata from content
- **Maintainable** — Works with version control diffs and code review

### File-Based Templates: `.prompt.json` Format (Compatibility)

The `.prompt.json` format remains fully supported for:

- **Simple, single-line prompts** — No need for multiline markup
- **Machine-generated templates** — Programmatic creation and manipulation
- **Backward compatibility** — Existing templates continue to work without migration

**Format:**

```json
{
  "name": "template-name",
  "description": "Human-friendly description",
  "prompt": "Single-line prompt with {{parameter}} placeholders",
  "parameters": {
    "parameter_name": {
      "description": "What this parameter is for",
      "required": true,
      "default": "optional-default"
    }
  }
}
```

### Parameters

Parameters are declared using `{{name}}` placeholders in the template body. The renderer:

1. **Collects required parameters** from placeholders in the template
2. **Merges values** in priority order:
   - Caller-provided values (CLI `--param` or cron `templateParameters`)
   - Defaults from `parameters[name].default` or `defaults[name]`
3. **Validates** that all required parameters are supplied
4. **Substitutes** placeholders with final values

**Parameter Declaration (Advanced):**

```json
"parameters": {
  "project": {
    "description": "Project name or identifier",
    "default": "BotNexus",
    "required": false
  },
  "owner": {
    "description": "Team or person responsible",
    "default": null,
    "required": true
  }
}
```

- `required: true` — Parameter must be supplied by caller if no default is set
- `required: false` — Parameter is optional; renders as empty string if missing
- `default` — Fallback value when caller does not supply it

### Examples

#### Example 1: Simple Configuration Template

```json
{
  "promptTemplates": {
    "daily-standup": {
      "prompt": "Provide a brief status update for {{project}}. Owner: {{owner}}. Focus areas: {{focus}}",
      "description": "Daily team status template",
      "defaults": {
        "project": "BotNexus",
        "owner": "Development Team",
        "focus": "Feature delivery and quality"
      }
    }
  }
}
```

**CLI usage:**

```powershell
# List templates
botnexus prompt list

# Render with defaults
botnexus prompt render daily-standup

# Render with override
botnexus prompt render daily-standup --param owner="Leela" --param focus="Bug fixes"
```

#### Example 2: Template with Required Parameters

```json
{
  "promptTemplates": {
    "code-review-summary": {
      "prompt": "Summarize the code review for PR #{{prNumber}} in {{repo}}. Reviewer: {{reviewer}}. Focus on: {{focusArea}}",
      "description": "Code review summary for pull requests",
      "parameters": {
        "prNumber": {
          "description": "Pull request number",
          "required": true
        },
        "repo": {
          "description": "Repository name",
          "default": "botnexus",
          "required": false
        },
        "reviewer": {
          "description": "Code reviewer name",
          "required": true
        },
        "focusArea": {
          "description": "Aspect to focus on (architecture, performance, tests, etc.)",
          "default": "architecture and testability",
          "required": false
        }
      }
    }
  }
}
```

**CLI usage:**

```powershell
# Missing required parameters — will fail
botnexus prompt render code-review-summary --param prNumber=242
# Error: Missing required template parameters: reviewer.

# Successful render
botnexus prompt render code-review-summary `
  --param prNumber=242 `
  --param reviewer="Hermes" `
  --param focusArea="performance optimization"
```

#### Example 3: File-based Template (JSON Format)

Create `~/.botnexus/prompts/bug-report.prompt.json`:

```json
{
  "name": "bug-report",
  "prompt": "Analyze the bug report: {{bugDescription}}. Severity: {{severity}}. Affected module: {{module}}.",
  "description": "Bug analysis and triage template"
}
```

List available templates:

```powershell
botnexus prompt list
# Output includes:
# - daily-standup (from config.json)
# - code-review-summary (from config.json)
# - bug-report (from ~/.botnexus/prompts/bug-report.prompt.json)
```

#### Example 4: Multi-line Template with Markdown Format (.prompt.md)

Create `~/.botnexus/prompts/sprint-retrospective.prompt.md`:

```markdown
---
name: sprint-retrospective
description: Sprint retrospective template with structured sections
parameters:
  sprint:
    description: Sprint identifier (e.g., "Sprint 42")
    required: true
  team:
    description: Team name
    default: Development Team
    required: false
  duration:
    description: Sprint duration in days
    default: "2 weeks"
    required: false
---
# Sprint {{sprint}} Retrospective

## Team: {{team}}

Generate a comprehensive retrospective for the completed sprint ({{duration}}).

## Sections to Address

- **What went well?**
  - Highlight successes and team achievements
  - Identify practices to continue

- **What could be improved?**
  - Pain points and bottlenecks
  - Opportunities for process optimization

- **Action Items for Next Sprint**
  1. Specific, measurable improvements
  2. Owner and due date for each item

## Metrics to Include

- Velocity comparison
- Burn-down chart analysis
- Team morale and engagement feedback

Focus on constructive feedback and continuous improvement.
```

Render and use:

```powershell
# List templates (both .prompt.md and .prompt.json are discovered)
botnexus prompt list

# Render with parameters
botnexus prompt render sprint-retrospective `
  --param sprint="Sprint 42" `
  --param team="Backend Squad"

# Execute through gateway (agent processes the rendered prompt)
botnexus prompt run sprint-retrospective `
  --param sprint="Sprint 42" `
  --param team="Backend Squad" `
  --agent analyst
```

**Why use `.prompt.md` for this template?**

- Multi-line content with natural structure (headings, bullets, numbered lists)
- Readable in version control and code review
- Easy to maintain and extend with new sections
- Preserves formatting without JSON escaping

#### Example 4: Template with Cron Job

Schedule a template-based agent prompt:

```json
{
  "cron": {
    "enabled": true,
    "jobs": {
      "morning-briefing": {
        "enabled": true,
        "schedule": "0 9 * * MON-FRI",
        "actionType": "agent-prompt",
        "agentId": "analyst",
        "templateName": "daily-standup",
        "templateParameters": {
          "project": "Infrastructure",
          "owner": "Platform Team"
        }
      }
    }
  },
  "promptTemplates": {
    "daily-standup": {
      "prompt": "Daily standup for {{project}} ({{owner}}). What are the top 3 items?"
    }
  }
}
```

When the cron job runs (9 AM Mon–Fri), the renderer substitutes parameters and sends the expanded prompt to the agent.

### Limitations

- Template names are case-insensitive when stored but matched case-sensitively in CLI commands
- Placeholder syntax `{{name}}` is rigid — no nested placeholders, filters, or conditions
- Maximum template size is limited by JSON parser and agent context window
- File-based templates (`.prompt.json`) are read-only — edit directly in `config.json` for primary control

---

## Examples

### Example 1: Basic Setup with Copilot

```json
{
  "version": 1,
  "agents": {
    "assistant": {
      "provider": "copilot",
      "model": "gpt-4o"
    }
  },
  "providers": {
    "copilot": {
      "auth": "oauth",
      "defaultModel": "gpt-4o",
      "apiBase": "https://api.githubcopilot.com"
    }
  },
  "channels": {
    "telegram": {
      "enabled": false
    }
  },
  "gateway": {
    "listenUrl": "http://127.0.0.1:5005",
    "defaultAgentId": "assistant"
  }
}
```

### Example 2: Multi-Agent Team with OpenAI Fallback

```json
{
  "version": 1,
  "agents": {
    "planner": {
      "provider": "openai",
      "model": "gpt-4-turbo",
      "systemPromptFiles": ["planner-soul.md"]
    },
    "writer": {
      "provider": "openai",
      "model": "gpt-4o",
      "systemPromptFiles": ["writer-soul.md"]
    }
  },
  "providers": {
    "openai": {
      "auth": "apikey",
      "apiKey": "sk-...",
      "defaultModel": "gpt-4-turbo"
    }
  },
  "gateway": {
    "defaultAgentId": "planner"
  }
}
```

### Example 3: Full Stack with Multiple Channels and MCP Servers

```json
{
  "version": 1,
  "agents": {
    "researcher": {
      "provider": "copilot",
      "model": "gpt-4o",
      "extensions": {
        "botnexus-mcp": {
          "servers": { "filesystem": { "command": "npx" } }
        }
      }
    }
  },
  "providers": {
    "copilot": {
      "auth": "oauth",
      "defaultModel": "gpt-4o",
      "apiBase": "https://api.githubcopilot.com"
    }
  },
  "channels": {
    "telegram": {
      "botToken": "123456789:ABCdef...",
      "agentId": "researcher",
      "allowedUserIds": [12345]
    },
    "discord": {
      "enabled": true,
      "settings": {
        "botToken": "xoxp-..."
      }
    },
    "slack": {
      "enabled": true,
      "settings": {
        "botToken": "xoxb-...",
        "signingSecret": "8f742231b91ee1522d..."
      }
    }
  },
  "gateway": {
    "listenUrl": "http://0.0.0.0:5005",
    "defaultAgentId": "researcher"
  },
  "apiKey": "gateway-secret-key"
}
```

### Example 4: Using Environment Variables for Secrets

Leave the secret empty in `config.json` and supply it from the environment. The gateway binds
`config.json` at the configuration root, so the `__` (double-underscore) environment path starts at
the **top-level key** — there is no `BotNexus__` prefix:

```json
{
  "providers": {
    "openai": {
      "auth": "apikey",
      "apiKey": ""
    }
  }
}
```

Environment setup:
```bash
#!/bin/bash
export providers__openai__apiKey="sk-$(openssl rand -hex 32)"
export BOTNEXUS_API_KEY="$(openssl rand -hex 32)"
./BotNexus.Gateway
```

---

## Troubleshooting

### Agent Not Responding

1. Check `DefaultAgent` is set and matches a named agent (if using named agents)
2. Verify provider config: `Auth`, `ApiKey` (for apikey auth), or OAuth token (for oauth)
3. Check the channel is configured under the right section (Telegram binds `channels:telegram` with a valid `botToken` and `agentId` - it has no `Enabled` flag; Discord/Slack use the `Channels.Instances` shape)
4. View logs for provider initialization errors

### Token Expiration Errors

1. For OAuth providers (Copilot): Token is cached at `~/.botnexus/tokens/{provider}.json`. Delete it to re-authenticate.
2. For API key providers: Verify key is active and has sufficient quota

### MCP Server Connection Failures

1. For Stdio MCP: Verify `Command` exists and is executable
2. For HTTP MCP: Verify `Url` is reachable and server is running
3. Check `ToolTimeout` is sufficient for MCP operations
4. View logs for detailed error messages

### Extension Not Loading

1. Verify folder structure matches config key: `extensions/{type}/{name}/`
2. Check file extension matches platform (.dll on Windows, .so on Linux)
3. Verify `Enabled` flag in config (especially for channels)
4. Check `BotNexusConfig.ExtensionsPath` points to correct directory

---

## See Also

- [BotNexus README](https://github.com/sytone/botnexus/blob/main/README.md) — Project overview
- [Architecture Guide](./architecture/overview.md) — System design and component overview
- [Cron and Scheduling Guide](./cron-and-scheduling.md) — Scheduled jobs and automation
- [API Reference](./api-reference.md) — Gateway REST API and SignalR hub docs
- [Extension Development](./extension-development.md) — Building custom channels, providers, and tools
