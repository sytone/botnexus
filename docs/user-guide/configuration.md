# Configuration Reference

This guide documents all configuration options for BotNexus. Configuration is stored in `~/.botnexus/config.json` and supports hot reload for most settings.

## Table of Contents

1. [Configuration Hierarchy](#configuration-hierarchy)
2. [Gateway Settings](#gateway-settings)
3. [Agent Configuration](#agent-configuration)
4. [Provider Configuration](#provider-configuration)
5. [Channel Configuration](#channel-configuration)
6. [Extension Configuration](#extension-configuration)
7. [Cron & Scheduling](#cron--scheduling)
8. [Session Management](#session-management)
9. [Security & Authentication](#security--authentication)
10. [Backups & Restore](#backups--restore)
11. [Complete Example](#complete-example)

---

## Configuration Hierarchy

BotNexus uses a layered configuration model:

1. **Code defaults** — Built-in constants in the codebase
2. **`appsettings.json`** — Project-level defaults (in `src/gateway/BotNexus.Gateway.Api/`)
3. **`~/.botnexus/config.json`** — User configuration (primary)
4. **Environment variables** — Override any setting via `BotNexus__Section__Key` format

**Environment Variable Override Example:**
```bash
# Override the listen URL
export BotNexus__Gateway__ListenUrl="http://localhost:8080"

# Override an agent's model
export BotNexus__Agents__assistant__Model="claude-opus-4.6"
```

**Custom Home Directory:**
```bash
# Use a different config directory
export BOTNEXUS_HOME=/opt/botnexus
```

---

## World identity

Each BotNexus home has a `worldId` at the root of `config.json`:

```json
{
  "worldId": "9c3a7c1e-4d2b-4f18-9f0a-2c5b6d7e8f90"
}
```

You do not set this by hand. The gateway generates it on first start against a home that has none and
persists it; a home that already has one is never modified. It is **per home, not per machine** — a dev
home, a test home and the live home on the same machine each get their own — which is what makes it
possible to tell which world a process is operating in. Do not copy it between installations.

`botnexus doctor` prints the resolved world ID next to the resolved home path.

---

## Gateway Settings

Gateway-level settings control the HTTP server, routing, and runtime behavior.

```json
{
  "gateway": {
    "listenUrl": "http://localhost:5005",
    "defaultAgentId": "assistant",
    "agentsDirectory": "~/.botnexus/agents",
    "sessionStore": {
      "type": "Sqlite",
      "connectionString": "Data Source=~/.botnexus/sessions.sqlite"
    },
    "compaction": {
      "maxMessagesBeforeCompaction": 100,
      "retainLastMessages": 20
    },
    "cors": {
      "allowedOrigins": ["http://localhost:3000", "https://app.example.com"]
    },
    "rateLimit": {
      "requestsPerMinute": 60,
      "windowSeconds": 60
    },
    "logLevel": "Information",
    "extensions": {
      "path": "~/.botnexus/extensions",
      "enabled": true
    },
    "world": {
      "id": "local-gateway",
      "displayName": "My BotNexus Gateway"
    }
  }
}
```

### Gateway Settings Reference

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `listenUrl` | string | `http://localhost:5005` | HTTP listen URL for REST API and WebUI. Loopback by default (#2798) — see [Remote and mesh access](#remote-and-mesh-access) before widening it. |
| `defaultAgentId` | string | `null` | Agent to route to when none specified |
| `agentsDirectory` | string | `~/.botnexus/agents` | Directory containing agent descriptor JSON files |
| `defaultTimezone` | string | `null` | Server-wide default IANA timezone ID (e.g. `America/Los_Angeles`) used when an agent has no `soul.timezone` of its own. Blank or invalid falls back to UTC |
| `sessionStore.type` | string | _(inferred)_ | Session store backend: `InMemory`, `File`, or `Sqlite`. When unset it resolves to `File` if `sessionsDirectory` is set, otherwise `Sqlite`. `InMemory` loses all data on restart. |
| `sessionStore.filePath` | string | `null` | Directory for the `File` store. Required when `type` is `File`; a relative path resolves against the writable data directory. |
| `sessionStore.connectionString` | string | `null` | SQLite connection string (when type=Sqlite). Defaults to `sessions.sqlite` in the writable data directory. |
| `compaction.maxMessagesBeforeCompaction` | int | `100` | Trigger compaction after this many messages |
| `compaction.retainLastMessages` | int | `20` | Keep this many recent messages after compaction |
| `auxiliary.titling.enabled` | bool | `true` | Enable conversation auto-titling after the first exchange; false keeps the default title until renamed |
| `auxiliary.titling.model` | string | `gpt-5.6-luna` | Auxiliary model ID for title generation; defaults to a fast non-reasoning model. Null uses the first registered model (unsafe with a reasoning model — it yields an empty title) |
| `auxiliary.titling.timeoutSeconds` | int | `30` | Per-call titling timeout; non-positive falls back to 30 |
| `cors.allowedOrigins` | array | `[]` | Allowed CORS origins for browser clients |
| `rateLimit.requestsPerMinute` | int | `60` | Max requests per client per minute |
| `rateLimit.windowSeconds` | int | `60` | Rate limit window size in seconds |
| `rateLimit.maxEntries` | int | `10000` | Max distinct client windows tracked in memory; bounds the per-client dictionary so a flood of distinct clients cannot exhaust gateway memory. Actively rate-limited windows are never evicted. Non-positive disables the cap. |
| `logLevel` | string | `Information` | Logging level: `Trace`, `Debug`, `Information`, `Warning`, `Error`, `Critical` |
| `maxCallChainDepth` | int | `10` | Max depth for cross-agent and sub-agent call chains; a deeper chain is refused, not extended |
| `crossAgentTimeoutSeconds` | int | `120` | Timeout for a cross-agent prompt call |
| `agentConversationMaxDepth` | int | `3` | Max depth for `agent_converse` call chains; zero or less falls back to the default rather than disabling the guard |
| `autoReplayInterruptedTurns` | bool | `false` | Re-dispatch the last user message from interactive sessions interrupted by an unclean restart. Off by default; when off the session is notified instead |
| `maxAutoReplayAttempts` | int | `2` | Replay attempts per interrupted session before falling back to notification-only. Counted in session metadata so a crashing message cannot loop |
| `sessionWarmup.enabled` | bool | `true` | Enable session pre-warming and multi-session subscription |
| `sessionWarmup.maxSessionsPerAgent` | int | `10` | Max sessions pre-warmed per agent; the bound on resident sessions |
| `sessionWarmup.retentionWindowHours` | int | `24` | Window within which a recently-active session is eligible for pre-warming |
| `sessionWarmup.collapseChannelContinuations` | bool | `true` | Collapse continuation sessions from the same channel into one during warmup |
| `delayTool.maxDelaySeconds` | int | `1800` | Max delay an agent may request; longer requests are clamped, not rejected |
| `delayTool.defaultDelaySeconds` | int | `60` | Delay used when a request omits a duration |
| `fileWatcherTool.maxTimeoutSeconds` | int | `1800` | Max file-watch wait; longer requests are clamped |
| `fileWatcherTool.defaultTimeoutSeconds` | int | `300` | File-watch timeout used when a request omits one |
| `fileWatcherTool.debounceMilliseconds` | int | `500` | Debounce coalescing rapid filesystem events into a single wake |
| `conversations.autoArchiveEnabled` | bool | `false` | World-level conversation auto-archive. Opt-in; per-agent `conversationRetention` inherits this default |
| `conversations.autoArchiveAfterDays` | int | `30` | Days of inactivity before auto-archive. Zero or negative disables archiving |
| `conversations.checkInterval` | TimeSpan | `01:00:00` | How often the retention service scans for conversations to archive |
| `extensions.path` | string | `~/.botnexus/extensions` | Root directory for extension assemblies |
| `extensions.enabled` | bool | `true` | Enable/disable dynamic extension loading |
| `world.id` | string | `local-gateway` | Unique identifier for this Gateway instance |
| `world.displayName` | string | `BotNexus Gateway` | Human-readable Gateway name |
| `fileAccess.allowedReadPaths` | array | `[]` | Default read paths for all agents (world-level) |
| `fileAccess.allowedWritePaths` | array | `[]` | Default write paths for all agents (world-level) |
| `fileAccess.deniedPaths` | array | `[]` | Default denied paths for all agents (world-level) |
| `rateLimit.enabled` | bool | `false` | Enable per-client rate limiting (opt-in) |
| `secretRedaction.patterns` | array | `[]` | Extra operator-supplied .NET regexes whose matches are replaced with `[REDACTED]`. Applied **in addition to** the built-in credential patterns, never instead of them. Validated at startup |
| `secretRedaction.matchTimeoutMilliseconds` | int | `100` | Per-pattern match timeout, so a catastrophic-backtracking regex cannot hang the logging path. Must be greater than zero |
| `toolOutputBudget.enabled` | bool | `true` | Enables the central tool-output backstop that bounds every tool result before it reaches the model |
| `toolOutputBudget.maxBytes` | int | `262144` (256 KiB) | Max UTF-8 byte size of a single tool result returned to the model. `0` or less disables the backstop even when `enabled` is true |
| `readTool.largeReadThresholdBytes` | int | `20480` (20 KiB) | UTF-8 byte size above which a `read` result gets an appended size indicator naming `offset` and `limit`. Content is never truncated. `0` or less disables the indicator |
| `readTool.elideUnchangedRereads` | bool | `true` | Whether re-reading an **unchanged** slice in the same session returns a short marker instead of the full body. The file is still read and hashed on every call, so a changed file can never take the cheap path |
| `workspace.maxReportFileSizeBytes` | int | `524288` (512 KB) | Max bytes read from a report file for portal preview; larger files are truncated and flagged. `0` means no limit |
| `autoUpdate.enabled` | bool | `false` | Enable background GitHub polling and the gateway self-update endpoint |
| `autoUpdate.checkIntervalMinutes` | int | `60` | How often to poll GitHub for a new commit. Minimum `5` |
| `autoUpdate.branch` | string | `main` | Branch tracked for new commits (owner/name default to `sytone`/`botnexus`) |
| `autoUpdate.cliPath` | string | `null` | Absolute path to the CLI that performs the update. Required when `enabled` is true, or `update/start` returns `412` |
| `autoUpdate.sourcePath` | string | `null` | Absolute path to the source tree, passed to the CLI as `--source`. Required when `enabled` is true |
| `crossWorld.peers.<key>.endpoint` | string | `null` | Peer gateway base URL used for outbound cross-world relay |
| `crossWorld.peers.<key>.apiKey` | string | `null` | Shared key presented on outbound relay calls to that peer. Secret — redacted by the config API |
| `crossWorld.inbound.enabled` | bool | `true` | Whether the inbound cross-world relay endpoint accepts traffic |
| `crossWorld.inbound.allowedWorlds` | array | `[]` | Source world IDs permitted to relay in. Empty allows **no** source world — it is an allow-list |
| `crossWorld.inbound.apiKeys` | map | `{}` | Shared keys keyed by source world ID. Secret — redacted by the config API |
| `toolResultPersistence.enabled` | bool | `true` | Enables the write-time cap on tool results stored in session history |
| `toolResultPersistence.maxBytes` | int | `16384` (16 KiB) | Max UTF-8 bytes of a single **persisted** tool result; larger results get a `[truncated N bytes]` marker at write time. `0` or less disables truncation. Distinct from `toolOutputBudget`, which bounds what reaches the model in the first place |
| `claimAudit.enabled` | bool | `true` | Whether the post-turn claim auditor runs |
| `claimAudit.mode` | string | `warn` | Reaction to an unbacked claim: `warn` emits an observable signal only; `block` also marks the turn as one that should be blocked. Unrecognised values fall back to `warn` |
| `memoryEmbeddings.backend` | string | `none` | Embedding backend: `none` (lexical-only), `local` (on-box inference) or `provider`. An unrecognised token degrades to `none` rather than failing startup |
| `memoryEmbeddings.enabled` | bool | `false` | Legacy toggle consulted only when `backend` is unset. Enabling embedding sends memory content to the configured endpoint |
| `memoryEmbeddings.provider` | string | `null` | Provider key whose embeddings endpoint supplies vectors (e.g. `ollama`, `openai`) |
| `memoryEmbeddings.model` | string | `null` | Embedding model identifier as the endpoint expects it |
| `memoryEmbeddings.dimensions` | int | `0` | Vector width the model emits. Declared, not discovered — a response of a different width is discarded and that entry falls back to lexical-only |
| `memoryEmbeddings.baseUrl` | string | `null` | Base URL of the OpenAI-compatible endpoint; `/embeddings` is appended |
| `memoryEmbeddings.apiKey` | string | `null` | Bearer token for the embeddings endpoint. Optional for a local endpoint. Secret — stored and shown masked |
| `dateTimeInjection.enabled` | bool | `false` | Prepend the current datetime to every user message so the agent has reliable temporal context |
| `dateTimeInjection.timezone` | string | `null` | IANA timezone used to format the injected datetime. Falls back to the gateway default timezone, then UTC |
| `dateTimeInjection.format` | string | `iso8601` | Output format. Only `iso8601` is supported today |
| `agentExchange.accessPolicy` | string | `open` | Which agents may start an `agent_converse` exchange: `open` (any registered agent) or `whitelist` (initiator must have the target in `subAgentIds` or a matching `subAgentRoles` grant) |
| `agentExchange.maxTurnsCeiling` | int | `30` | Upper bound on the `maxTurns` of a single `agent_converse` call, whatever the agent requests. Values below 1 are treated as 1 |
| `agentExchange.maxInboundQueueDepth` | int | `8` | How many exchanges may queue for one busy agent before further callers are refused with backpressure. The in-flight exchange does not count. Values below 1 are treated as 1 |

### Remote and mesh access

`botnexus init` writes a **loopback** listen URL (`http://localhost:5005`). A fresh install is therefore
reachable only from the machine it runs on. This is deliberate (issue #2798): binding every interface is
an operator decision, not an out-of-box state.

To expose the gateway to a NetBird/Tailscale overlay, a reverse proxy, or a LAN, opt in explicitly:

```bash
# at install time
botnexus init --listen-all-interfaces

# or on an existing install
botnexus config set gateway.listenUrl http://0.0.0.0:5005
```

**What a wildcard bind exposes.** `listenUrl` serves the portal UI, the SignalR hub, the agent REST API
**and the gateway admin endpoints** on the same address. Binding `0.0.0.0` (or `*`, `+`, `::`) publishes
all of them to every network this host can reach. The admin endpoints do not yet enforce an authorization
scope check (issue #506), so treat a wildcard bind as an administrative exposure and put an authenticated
reverse proxy or a private overlay network in front of it.

`botnexus doctor config` reports a wildcard bind as an advisory finding, naming the exposed surface. It is
**never changed automatically** — a wildcard bind can be a deliberate choice, so the tool reports it and
leaves the decision to you.

---

## Agent Configuration

Agents are defined in the `agents` section, keyed by agent ID. One key is reserved: `defaults` holds
world-level values that are merged into every agent, so it is not itself an agent.

```json
{
  "agents": {
    "defaults": {
      "toolTimeoutSeconds": 300,
      "toolIds": ["read_file", "write_file"]
    },
    "assistant": {
      "displayName": "Assistant",
      "description": "General-purpose AI assistant",
      "provider": "copilot",
      "model": "gpt-4.1",
      "allowedModels": ["gpt-4.1", "gpt-4o", "claude-sonnet-4-20250514"],
      "systemPromptFiles": ["SOUL.md", "IDENTITY.md", "TOOLS.md"],
      "toolIds": ["read_file", "write_file", "web_search"],
      "subAgents": ["specialist", "reviewer"],
      "isolationStrategy": "in-process",
      "maxConcurrentSessions": 5,
      "enabled": true,
      "memory": {
        "enabled": true,
        "path": "memory"
      },
      "soul": {
        "enabled": true,
        "idleTimeoutMinutes": 30
      },
      "sessionAccess": {
        "level": "own",
        "allowedAgents": []
      },
      "conversationAccess": {
        "level": "own",
        "allowedAgents": []
      },
      "toolPolicy": {
        "alwaysApprove": [],
        "neverApprove": [],
        "denied": []
      },
      "extensions": {
        "botnexus-skills": {
          "enabled": true,
          "autoLoad": ["git-workflow", "coding-standards"],
          "disabled": [],
          "maxLoadedSkills": 20
        },
        "botnexus-mcp": {
          "toolPrefix": true,
          "servers": {
            "filesystem": {
              "command": "npx",
              "args": ["-y", "@modelcontextprotocol/server-filesystem", "/workspace"],
              "env": {},
              "inheritEnv": false
            }
          }
        }
      }
    }
  }
}
```

### Sub-agent Workspace Root

Each background sub-agent runs in an isolated temporary workspace. By default these are created under `<OS temp>/botnexus-subagent-workspaces`. Set `gateway.subAgents.workspaceRoot` to relocate them - for example to a larger or faster volume:

```json
{
  "gateway": {
    "subAgents": {
      "workspaceRoot": "~/botnexus-scratch/subagents"
    }
  }
}
```

The value supports `~` (home directory) and environment-variable expansion and is normalized to an absolute path. Leave it empty (the default) to keep the historical temp-root location. The gateway and the `botnexus subagent workspace list|prune` / `doctor` commands resolve this setting through a single shared resolver, so they always target the same directory.

### Agent Settings Reference

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `displayName` | string | (required) | Human-readable agent name shown in UI |
| `description` | string | `null` | Optional description of agent's purpose |
| `provider` | string | (required) | Provider key (e.g., `copilot`, `anthropic`, `openai`) |
| `model` | string | (required) | Default model ID for this agent |
| `allowedModels` | array | `[]` | Models this agent can use. Empty = unrestricted within provider |
| `thinking` | string | `null` | Agent-level default reasoning level (`minimal`, `low`, `medium`, `high`, `xhigh`, `max`). Agent layer of the 3-layer model/thinking/context override stack (model default -> agent -> conversation); `null` falls through to the model default. Rejected at registration if the selected model does not support it |
| `contextWindow` | int | `null` | Agent-level default context-window size in tokens. Agent layer of the override stack; `null` falls through to the model default. Only sizes the model advertises as supported are accepted |
| `systemPromptFiles` | array | `[]` | System prompt files to load (in order). Empty = default order: `AGENTS.md`, `SOUL.md`, `TOOLS.md`, `BOOTSTRAP.md`, `IDENTITY.md`, `USER.md` |
| `systemPromptFile` | string | `null` | (Legacy) Single system prompt file path |
| `toolIds` | array | `[]` | Tool identifiers this agent can use |
| `subAgents` | array | `[]` | Agent IDs this agent can call as sub-agents |
| `isolationStrategy` | string | `in-process` | Execution isolation: `in-process` or `sandbox` |
| `maxConcurrentSessions` | int | `0` | Max concurrent sessions (0 = unlimited) |
| `enabled` | bool | `true` | Enable/disable this agent |
| `memory.enabled` | bool | `false` | Enable memory system for this agent |
| `memory.path` | string | `memory` | Memory root directory relative to agent workspace. Daily notes (`YYYY-MM-DD.md`) are written here |
| `memory.indexing` | string | `auto` | Memory indexing mode |
| `memory.search.defaultTopK` | int | `10` | Default number of search results |
| `memory.search.maxTopK` | int | `100` | Upper bound for the `memory_search` `topK` argument. Caller-supplied values above this are clamped (floored at `defaultTopK`) to bound the search fan-out and tool-result size |
| `memory.search.maxLimit` | int | `100` | Upper bound for the `memory_get` session-listing `limit` argument. Caller-supplied values above this are clamped |
| `soul.enabled` | bool | `false` | Enable soul session (persistent agent identity) |
| `soul.idleTimeoutMinutes` | int | `30` | Idle timeout for soul sessions |
| `sessionAccess.level` | string | `own` | Session access: `own`, `allowlist`, or `all` |
| `sessionAccess.allowedAgents` | array | `[]` | Agent IDs to allow (when level=`allowlist`) |
| `conversationAccess.level` | string | `sessionAccess.level` | Conversation tool access: `own`, `allowlist`, or `all` |
| `conversationAccess.allowedAgents` | array | `sessionAccess.allowedAgents` | Agent IDs to allow for conversation access (when level=`allowlist`) |
| `toolPolicy.alwaysApprove` | array | `[]` | Tools requiring approval before execution |
| `toolPolicy.neverApprove` | array | `[]` | Trusted tools that skip approval |
| `toolPolicy.denied` | array | `[]` | Tools completely blocked for this agent |
| `toolPolicy.askFallback` | string | `allow` | Posture when approval is required but no approval workflow can service it: `allow` (execute, audited) or `deny` (fail closed with `ask-fallback-deny`). Leave unset for unattended agents |
| `toolPolicy.askFallbackAllow` | array | `[]` | Tools exempted from `askFallback: "deny"` |
| `fileAccess.allowedReadPaths` | array | `[]` | Paths the agent can read (exact or glob). Workspace always readable |
| `fileAccess.allowedWritePaths` | array | `[]` | Paths the agent can write (exact or glob). Workspace always writable |
| `fileAccess.deniedPaths` | array | `[]` | Paths explicitly denied even if otherwise allowed |

### `agents.defaults`

The reserved `defaults` key holds world-level values merged into every agent. Only these five keys
exist - a default that is not listed here is not merged, because there is no property to merge it
into. See the [comprehensive configuration reference](../configuration#agents-defaults-properties)
for the per-property inheritance policies.

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `toolIds` | array | `null` | Default tool ids for agents that do not set their own `toolIds` |
| `toolTimeoutSeconds` | int | `300` | Default per-tool timeout in seconds |
| `memory` | object | `null` | Default memory configuration inherited by agents |
| `heartbeat` | object | `null` | Default heartbeat configuration inherited by agents |
| `fileAccess` | object | `null` | Default file access policy inherited by agents |

### Shell Execution

Per-agent shell execution settings allow overriding the gateway-level shell configuration for individual agents.

```json
{
  "agents": {
    "my-agent": {
      "shellCommand": ["pwsh", "-NoLogo", "-NoProfile", "-Command"]
    }
  }
}
```

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `shellCommand` | string[] | `null` | Custom shell command array. Element 0 is the executable, elements 1..N are base arguments. The agent's command is appended as the final argument. |

**Override behavior:**
- Per-agent `shellCommand` takes priority over gateway-level `shellCommand` and `shellPreference`
- If not set, the agent inherits the gateway-level shell configuration
- The array must have at least 2 elements (executable + at least one arg) to be valid; otherwise it is ignored

**Common configurations:**

```json
{
  "agents": {
    "ps-agent": {
      "shellCommand": ["pwsh", "-NoLogo", "-NoProfile", "-NonInteractive", "-Command"]
    },
    "bash-agent": {
      "shellCommand": ["/bin/bash", "-l", "-c"]
    },
    "nu-agent": {
      "shellCommand": ["nu", "-c"]
    }
  }
}
```

See [Shell Execution](/features/shell-execution) for the full configuration hierarchy, ArgumentList execution model, and troubleshooting.

### File Access Policy

Controls which file paths agents can access via file tools (`read`, `write`, `edit`, `grep`, `glob`, `ls`). Shell tools (`bash`, `exec`) are not restricted by this policy.

**World-level default** — set under `gateway.fileAccess` to apply to all agents:

```json
{
  "gateway": {
    "fileAccess": {
      "allowedReadPaths": ["Q:/repos/botnexus", "~/Documents"],
      "allowedWritePaths": ["Q:/repos/botnexus/docs"],
      "deniedPaths": ["**/.env", "**/secrets/**"]
    }
  }
}
```

**Per-agent override** — set under the agent to replace the world default entirely:

```json
{
  "agents": {
    "my-agent": {
      "fileAccess": {
        "allowedReadPaths": ["Q:/repos/botnexus"],
        "allowedWritePaths": ["Q:/repos/botnexus/docs/planning"],
        "deniedPaths": ["Q:/repos/botnexus/.env"]
      }
    }
  }
}
```

**Path rules:**
- Exact paths allow the directory and all children
- Glob patterns (`*`, `**`, `?`) supported via `FileSystemName.MatchesSimpleExpression`
- `~` expands to user home directory
- `@location-name` references a named Location defined in `gateway.locations` (e.g., `@repo-botnexus`). This is the preferred form — it enables validation via `botnexus doctor locations`, UI management, and portability
- Workspace (`~/.botnexus/agents/{id}/workspace`) is always accessible
- Deny list takes priority over allow list
- Per-agent `fileAccess` replaces the world default (not merged)
- If no policy set (agent or world), workspace-only mode

### System Prompt Files

System prompt files are loaded from `~/.botnexus/agents/<agentId>/` directory. If `systemPromptFiles` is empty, BotNexus loads these files in order (if they exist):

1. `AGENTS.md` — Multi-agent coordination patterns
2. `SOUL.md` — Agent personality and values
3. `TOOLS.md` — Tool usage guidelines
4. `BOOTSTRAP.md` — Initialization instructions
5. `IDENTITY.md` — Agent role and expertise
6. `USER.md` — User preferences and context

Create a custom order by explicitly listing files:

```json
{
  "systemPromptFiles": ["IDENTITY.md", "SOUL.md", "custom-rules.md"]
}
```

---

## Provider Configuration

Providers connect BotNexus to LLM APIs. Each provider has its own configuration section.

```json
{
  "providers": {
    "copilot": {
      "enabled": true,
      "apiKey": "auth:copilot",
      "baseUrl": "https://api.githubcopilot.com",
      "defaultModel": "gpt-4.1",
      "models": [
        "gpt-4.1", "gpt-4o", "gpt-5.4", 
        "claude-sonnet-4-20250514", "claude-opus-4.6"
      ]
    },
    "anthropic": {
      "enabled": true,
      "apiKey": "${ANTHROPIC_API_KEY}",
      "baseUrl": "https://api.anthropic.com",
      "defaultModel": "claude-sonnet-4-20250514",
      "models": ["claude-sonnet-4-20250514", "claude-opus-4.6"]
    },
    "openai": {
      "enabled": true,
      "apiKey": "${OPENAI_API_KEY}",
      "baseUrl": "https://api.openai.com/v1",
      "defaultModel": "gpt-4o",
      "models": ["gpt-4o", "gpt-4-turbo", "gpt-3.5-turbo"]
    },
    "custom-openai-compat": {
      "enabled": true,
      "apiKey": "sk-custom-key",
      "baseUrl": "https://custom-llm-api.example.com/v1",
      "defaultModel": "llama-70b",
      "models": ["llama-70b", "mixtral-8x7b"]
    }
  }
}
```

### Provider Settings Reference

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `enabled` | bool | `true` | Enable/disable this provider |
| `apiKey` | string | (required) | API key, `auth:copilot` for OAuth, or `${ENV_VAR}` reference |
| `baseUrl` | string | (varies) | API base URL (provider-specific default if omitted) |
| `defaultModel` | string | (varies) | Default model for agents using this provider |
| `models` | array | `null` | Allowed models. `null` = all models, `[]` = none |
| `input` | array | `null` | Explicit input modalities (e.g. `["text","image"]`) for models registered from `models`. `null`/`[]` infers from the model family; an explicit declaration always wins |
| `api` | string | `openai-completions` | API format used when registering models from `models` for a custom provider |
| `reasoning` | bool | `null` | Dynamic-model capability: whether these models support a thinking override. `null` = infer from the model family |
| `supportsExtraHighThinking` | bool | `null` | Dynamic-model capability: whether these models support the `xhigh` / `max` thinking tiers. `null` = infer from the family |
| `supportsExtendedContextWindow` | bool | `null` | Dynamic-model capability: whether these models expose the extended (1M) context tier. `null` = infer from the family |
| `contextWindow` | int | `128000` | Dynamic-model capability: default context-window size (tokens) for these models |

### Dynamic Model Capabilities

Models you define under a provider's `models` list (or that an agent references on a
config-only provider) are **dynamic models** - they are not part of the hand-curated built-in
model table. So the agent and conversation pickers know which reasoning levels and context
sizes to offer, each dynamic model carries a capability set:

- **Supported thinking levels** - drives the reasoning picker. A non-reasoning model offers
  none; a reasoning model offers `minimal`..`high`, plus `xhigh` and `max` when it supports the
  extra-high tiers.
- **Supported context sizes** - drives the context-window picker. A standard model exposes its
  single window; an extended-context model additionally exposes the 1M tier.

When you omit the capability fields, BotNexus **infers sensible defaults from the model
family** (for example `claude-opus-4.6` and `gpt-5.2` are recognised as reasoning models with
the extra-high tiers; `claude-sonnet-4*` carries the extended context window). Declare the
fields explicitly when the family heuristic does not recognise your model id - for example a
local Ollama or LM Studio build:

```json
{
  "providers": {
    "local-llm": {
      "enabled": true,
      "baseUrl": "http://localhost:11434/v1",
      "api": "openai-completions",
      "models": ["my-custom-reasoner"],
      "reasoning": true,
      "supportsExtraHighThinking": true,
      "contextWindow": 262144
    }
  }
}
```

Because pickers read these capabilities, a dynamic model **never offers an invalid choice** -
the reasoning picker is empty for a non-reasoning model, and the context picker only lists sizes
the model can actually address. An explicit `supportsExtraHighThinking: true` on a model
declared `reasoning: false` is ignored (a non-reasoning model has no thinking tiers).

### Supported Providers

| Provider | Key | Default Base URL | Notes |
|----------|-----|------------------|-------|
| **GitHub Copilot** | `copilot` | `https://api.githubcopilot.com` | 26 models: Claude, GPT-4/5, Gemini, Grok. OAuth via `auth:copilot` |
| **Anthropic** | `anthropic` | `https://api.anthropic.com` | Claude models (Sonnet, Opus, Haiku) |
| **OpenAI** | `openai` | `https://api.openai.com/v1` | GPT-4, GPT-4o, GPT-3.5 |
| **OpenAI-Compatible** | (custom) | (custom) | Any OpenAI-compatible API |

### GitHub Copilot Provider

The Copilot provider supports **26 models** across multiple families:

**Claude (via Copilot):**
- `claude-sonnet-4-20250514`, `claude-opus-4.6`, `claude-haiku-4.5`

**GPT-4 & GPT-4o:**
- `gpt-4.1`, `gpt-4o`, `gpt-4-turbo`

**GPT-5 (Copilot exclusive):**
- `gpt-5.4`, `gpt-5.3-codex`, `gpt-5.2`, `gpt-5.1`, `gpt-5.4-mini`, `gpt-5-mini`

**Gemini & Grok:**
- `gemini-2.0-flash`, `gemini-1.5-pro`, `grok-2-turbo`

**Authentication:**
```json
{
  "copilot": {
    "apiKey": "auth:copilot"
  }
}
```

On first use, BotNexus prompts for GitHub OAuth and stores tokens in `~/.botnexus/tokens/copilot.json`.

---

## Model, Thinking, and Context: The 3-Layer Override Model

BotNexus resolves the effective model, thinking (reasoning) level, and context-window size for
every turn from a **three-layer precedence stack**. Each field is resolved independently and the
most specific layer that sets it wins:

```
model defaults  <  agent configuration  <  conversation override   (most specific wins)
```

| Layer | Where it is set | Scope | Falls through when unset |
|-------|-----------------|-------|--------------------------|
| **Model defaults** | The model's capability set (built-in table, or inferred/declared for a dynamic model) | Every agent using that model | (base layer) |
| **Agent configuration** | `agents.<id>.model` / `thinking` / `contextWindow` | All conversations for that agent | to the model defaults |
| **Conversation override** | Portal picker, `/model` and `/reasoning` commands, or the override REST API | One conversation | to the agent configuration |

Because each field falls through independently, overriding only the reasoning level for a
conversation still inherits the agent's model and context window.

### How capabilities gate each layer

A field can only be set to a value the underlying model actually supports. The **model layer** is
the source of truth for what is valid:

- **Built-in models** carry their capability set in the curated model table.
- **Dynamic models** (declared under a provider's `models` list, or referenced by an agent on a
  config-only provider) carry a capability set too - declared explicitly, or inferred from the
  model family when omitted (see [Dynamic Model Capabilities](#dynamic-model-capabilities)).

Every picker reads the same capability set, so only valid options are ever offered:

- The reasoning picker lists exactly the thinking levels the model supports (empty for a
  non-reasoning model, and `xhigh` / `max` appear only for models with the extra-high tiers).
- The context picker lists exactly the context sizes the model supports (its single window, plus
  the 1M tier only for extended-context models).

Both the **agent level** and the **conversation level** enforce this: an agent-level `thinking` or
`contextWindow` a model cannot express is rejected at registration, and a conversation override a
model cannot express is rejected with `400` and leaves the stored override untouched. A dynamic
model behaves identically to a built-in one here - it never surfaces or accepts an invalid choice.

### Worked example

```json
{
  "providers": {
    "local-llm": {
      "enabled": true,
      "baseUrl": "http://localhost:11434/v1",
      "api": "openai-completions",
      "models": ["my-custom-reasoner"],
      "reasoning": true,
      "supportsExtraHighThinking": true,
      "contextWindow": 262144
    }
  },
  "agents": {
    "researcher": {
      "provider": "local-llm",
      "model": "my-custom-reasoner",
      "thinking": "high",
      "contextWindow": 131072
    }
  }
}
```

1. **Model layer** - `my-custom-reasoner` is a dynamic model. Its declared capabilities make the
   reasoning picker offer `minimal`..`max` and the context picker default to a 262144 window.
2. **Agent layer** - the `researcher` agent defaults to `high` reasoning and a 131072 context
   window. Both are valid for the model, so registration succeeds.
3. **Conversation layer** - a single conversation can run `/reasoning max` to deepen reasoning for
   one thread; the model and context window fall through from the agent unchanged.

See also: [Agent Configuration](#agent-configuration) for the agent layer, and the
[Conversations guide](conversations.md#per-conversation-model-reasoning-and-context-override) for
the conversation layer.

### Confirming which window actually applied

The agent's hidden runtime-context block reports the effective settings that reached the run, so the
value the agent is told is the same one the resolver produced rather than an independently-derived
guess (issue #2796). When a context-window size is in force, the runtime line carries a
`context_window=<tokens>` field:

```text
Runtime: agent=researcher | session=... | provider=copilot | model=my-custom-reasoner | context_window=131072
```

The field is **omitted entirely** when the provider default applies - its absence means "no explicit
window was resolved", not "zero". This is the fastest way to confirm an agent-level or
conversation-level override actually took effect, rather than being silently rejected and fallen back.

---

## Channel Configuration

Channels route messages from external sources (Telegram, WebUI, TUI) to agents.

```json
{
  "channels": {
    "webui": {
      "type": "signalr",
      "enabled": true,
      "settings": {}
    },
    "telegram": {
      "botToken": "${TELEGRAM_BOT_TOKEN}",
      "agentId": "assistant",
      "allowedChatIds": [123456789],
      "pollingTimeoutSeconds": 30
    },
    "tui": {
      "type": "tui",
      "enabled": false,
      "settings": {
        "defaultAgentId": "assistant"
      }
    }
  }
}
```

### Channel Settings Reference

Channel configuration is extension-specific. The channel type is determined by the extension ID (the key under `channels`).

**SignalR (WebUI):**
- No additional settings required. WebUI is served at the Gateway root URL.

**Telegram:**
```json
{
  "channels": {
    "telegram": {
      "botToken": "${TELEGRAM_BOT_TOKEN}",
      "agentId": "assistant",
      "allowedChatIds": [123456789, 987654321],
      "pollingTimeoutSeconds": 30
    }
  }
}
```

Uses long polling by default (no public URL required). For webhook mode, add `"webhookUrl": "https://your-host/telegram/webhook"`.

**Multi-bot Telegram config:**
```json
{
  "channels": {
    "telegram": {
      "bots": {
        "my-bot": {
          "botToken": "111:AAA...",
          "agentId": "my-agent",
          "allowedChatIds": [123456789]
        },
        "assistant-bot": {
          "botToken": "222:BBB...",
          "agentId": "assistant"
        }
      }
    }
  }
}
```

**Agent 365 (Microsoft 365 Agents SDK):**
```json
{
  "channels": {
    "agent365": {
      "clientId": "${AGENT365_CLIENT_ID}",
      "clientSecret": "${AGENT365_CLIENT_SECRET}",
      "tenantId": "${AGENT365_TENANT_ID}",
      "agentId": "assistant",
      "inboundRoute": "/agent365/messages"
    }
  }
}
```

Bridges the Microsoft 365 Agents SDK `Activity` protocol to BotNexus (Register tier: message
round-trip only). Inbound activities arrive on `inboundRoute` (default `/agent365/messages`); replies
are sent through the SDK connector authenticated with the Entra app client credentials. BotNexus
remains the response engine. See the extension page `docs/extensions/agent365.md` for the full config
surface.

**TUI (Terminal UI):**
```json
{
  "settings": {
    "defaultAgentId": "assistant"
  }
}
```

---

## Extension Configuration

Extensions add tools, MCP servers, and skills to agents. Configuration is per-agent under `extensions`.

### MCP (Model Context Protocol)

```json
{
  "agents": {
    "assistant": {
      "extensions": {
        "botnexus-mcp": {
          "toolPrefix": true,
          "servers": {
            "filesystem": {
              "command": "npx",
              "args": ["-y", "@modelcontextprotocol/server-filesystem", "/workspace"],
              "env": {
                "NODE_ENV": "production"
              },
              "inheritEnv": false,
              "workingDirectory": "/workspace",
              "initTimeoutMs": 30000,
              "callTimeoutMs": 60000
            },
            "github": {
              "url": "https://mcp.example.com/github",
              "headers": {
                "Authorization": "Bearer ${GITHUB_TOKEN}"
              }
            }
          }
        }
      }
    }
  }
}
```

**MCP Settings:**
- `toolPrefix` (bool): Prefix tool names with server ID (e.g., `filesystem_read`)
- `servers` (object): MCP server definitions keyed by server ID

**MCP Server (stdio transport):**
- `command`: Executable path
- `args`: Command arguments
- `env`: Environment variables for the process
- `inheritEnv`: Inherit parent env vars (default: `true`, set `false` for security)
- `workingDirectory`: Working directory for the process
- `initTimeoutMs`: Initialization timeout (default: `30000`)
- `callTimeoutMs`: Tool call timeout (default: `60000`)

**MCP Server (HTTP/SSE transport):**
- `url`: MCP server HTTP endpoint
- `headers`: HTTP headers to include in requests

### Skills System

```json
{
  "agents": {
    "assistant": {
      "extensions": {
        "botnexus-skills": {
          "enabled": true,
          "autoLoad": ["git-workflow", "coding-standards"],
          "disabled": ["deprecated-skill"],
          "allowed": null,
          "maxLoadedSkills": 20,
          "maxSkillContentChars": 100000
        }
      }
    }
  }
}
```

**Skills Settings:**
- `enabled` (bool): Enable/disable skills for this agent
- `autoLoad` (array): Skills to load automatically at agent startup
- `disabled` (array): Skills to never load (blocklist)
- `allowed` (array): Allowed skills. `null` = all skills allowed
- `maxLoadedSkills` (int): Max skills loaded simultaneously (default: `20`)
- `maxSkillContentChars` (int): Max total skill content size (default: `100000`)

---

## Cron & Scheduling

Schedule recurring tasks like heartbeats, reports, or maintenance jobs.

```json
{
  "cron": {
    "enabled": true,
    "tickIntervalSeconds": 60,
    "jobs": {
      "daily-summary": {
        "name": "Daily Summary",
        "schedule": "0 9 * * *",
        "actionType": "agent-prompt",
        "agentId": "assistant",
        "message": "Generate a daily summary report",
        "enabled": true,
        "createdBy": "admin",
        "metadata": {
          "category": "reporting"
        }
      },
      "heartbeat": {
        "name": "Agent Heartbeat",
        "schedule": "*/15 * * * *",
        "actionType": "agent-prompt",
        "agentId": "monitor",
        "message": "Check system health",
        "enabled": true
      }
    }
  }
}
```

**Cron Settings:**
- `enabled` (bool): Enable/disable scheduler
- `tickIntervalSeconds` (int): Scheduler polling interval
- `jobs` (object): Job definitions keyed by job ID

**Cron Job Settings:**
- `name` (string): Display name
- `schedule` (string): Cron expression (5-field format)
- `actionType` (string): `agent-prompt`, `command`, `webhook`, `memory-dreaming`, `skill-review`, `agent-converse`, `plugin-update`, or `heartbeat` - see [Cron & Scheduling](../cron-and-scheduling#job-types)
- `agentId` (string): Target agent (for `agent-prompt` jobs)
- `message` (string): Prompt message (for `agent-prompt` jobs)
- `webhookUrl` (string): Webhook URL (for `webhook` jobs)
- `shellCommand` (string): Shell command (for `command` jobs)
- `enabled` (bool): Enable/disable this job

---

## Session Management

Sessions are persisted to a SQLite database by default.

```json
{
  "gateway": {
    "sessionStore": {
      "type": "Sqlite",
      "connectionString": "Data Source=~/.botnexus/sessions.sqlite"
    },
    "compaction": {
      "maxMessagesBeforeCompaction": 100,
      "retainLastMessages": 20
    }
  }
}
```

### Liveness Watchdog

The gateway warns after prolonged inactivity and verifies the runtime scheduler before escalating to a fatal alert.

```json
{
  "gateway": {
    "livenessWatchdog": {
      "checkInterval": "00:00:30",
      "warningThreshold": "00:15:00",
      "criticalThreshold": "00:30:00",
      "criticalProbeTimeout": "00:00:05"
    }
  }
}
```

- `checkInterval`: How often inactivity is evaluated (default `00:00:30`).
- `warningThreshold`: Idle duration before the first warning (default `00:15:00`).
- `criticalThreshold`: Idle duration that triggers a scheduler responsiveness probe (default `00:30:00`).
- `criticalProbeTimeout`: Maximum time for queued scheduler work to run before one fatal event is emitted for the inactivity episode (default `00:00:05`, i.e. 5 seconds). A successful probe logs a warning instead of a fatal event.

**Session Store Types:**

1. **Sqlite** (default): SQLite database
   ```json
   {
     "sessionStore": {
       "type": "Sqlite",
       "connectionString": "Data Source=~/.botnexus/sessions.sqlite"
     }
   }
   ```

2. **InMemory**: Ephemeral (lost on restart — for testing/dev only)
   ```json
   {
     "sessionStore": {
       "type": "InMemory"
     }
   }
   ```

**Compaction Settings:**
- `maxMessagesBeforeCompaction`: Trigger compaction after this many messages
- `retainLastMessages`: Keep this many recent messages after compaction

### Session Cleanup

A background cleanup service expires and prunes old sessions.

```json
{
  "gateway": {
    "sessionCleanup": {
      "checkInterval": "00:05:00",
      "sessionTtl": "1.00:00:00",
      "closedSessionRetention": "7.00:00:00",
      "cronNoopRetention": "7.00:00:00",
      "maxDiskBytes": null,
      "highWaterBytes": null,
      "diskBudgetMode": "Warn"
    }
  }
}
```

**Session Cleanup Settings:**
- `checkInterval`: How often the cleanup service runs (default `00:05:00`).
- `sessionTtl`: Time-to-live for active sessions before they are expired (default `1.00:00:00`, i.e. 24 hours).
- `closedSessionRetention`: Optional; auto-delete closed (sealed) sessions after this period. Omit or set `null` to keep forever.
- `cronNoopRetention`: Prune near-empty cron "noop wake" sessions (≤ 2 persisted messages) whose last update is older than this window. Defaults to `7.00:00:00` (7 days) and is user-configurable. Set to `null` or `0` to disable. This only prunes stale near-empty cron sessions after the fact - it does **not** change wake or persist behaviour.
- `maxDiskBytes`: Optional total disk budget, in bytes, for this agent's sessions. **`null`, `0`, or a negative value disables the budget and evicts nothing** — a zero budget is *disabled*, never "a zero-byte budget" that would wipe every session (the openclaw#119422 regression this contract exists to prevent). Defaults to `null`.
- `highWaterBytes`: The footprint eviction reclaims down to before stopping. Defaults to 80% of `maxDiskBytes`, so enforcement is not re-triggered on every cycle at exactly the budget line.
- `diskBudgetMode`: `Warn` (default) logs disk pressure and deletes nothing; `Enforce` evicts oldest-first — sealed sessions, then expired, then suspended. Active sessions and any session with an in-flight agent run are never evicted.

---

## Security & Authentication

### API Keys

```json
{
  "apiKey": "your-gateway-api-key",
  "gateway": {
    "apiKeys": {
      "client-1": {
        "apiKey": "key-abc123",
        "tenantId": "tenant-1",
        "callerId": "client-app",
        "displayName": "Client App",
        "allowedAgents": ["assistant"],
        "permissions": ["chat:send", "sessions:read"],
        "isAdmin": false
      }
    }
  }
}
```

**API Key Settings:**
- `apiKey` (root-level): Global API key (null = dev mode, no auth)
- `gateway.apiKeys`: Multi-tenant API key definitions

**Per-key Settings:**
- `apiKey`: Raw API key value
- `tenantId`: Tenant identifier for isolation
- `callerId`: Caller ID in audit logs
- `displayName`: Human-readable name
- `allowedAgents`: Agent IDs this key can access
- `permissions`: Permission list (e.g., `chat:send`, `sessions:read`)
- `isAdmin`: Administrative privileges

### CORS

```json
{
  "gateway": {
    "cors": {
      "allowedOrigins": [
        "http://localhost:3000",
        "https://app.example.com"
      ]
    }
  }
}
```

### Rate Limiting

```json
{
  "gateway": {
    "rateLimit": {
      "requestsPerMinute": 60,
      "windowSeconds": 60
    }
  }
}
```

---

## Backups & Restore

BotNexus copies `~/.botnexus/config.json` into `~/.botnexus/backups/` before every mutation, as
`config-{yyyyMMdd}-{HHmmss}-{reason}.json`. The newest 50 are kept and older ones are pruned.

List them, with a verdict saying whether each one still loads against the current schema:

```bash
botnexus config backups list
```

```
Id                                         Timestamp             Reason                  Size    Verdict
config-20260102-090000-before-provider...  2026-01-02 09:00:00   before-provider-update  4821 B  valid
config-20260101-101500-before-agent-cr...  2026-01-01 10:15:00   before-agent-create     4770 B  valid
```

Restore one. **The restore is a dry run unless you pass `--commit`:**

```bash
# Preview: validates the backup, writes nothing.
botnexus config restore config-20260101-101500-before-agent-create

# Perform it.
botnexus config restore config-20260101-101500-before-agent-create --commit
```

The restore validates and migrates the snapshot before writing, refuses a snapshot that would not
load, resolves any redacted `***` secrets back to the live values instead of overwriting them, and
backs up the pre-restore document first. Copying a backup over `config.json` by hand skips all of
that - use the command.

See the [Configuration Guide](../configuration.md#backups-and-restore) for the full detail.

---

## Complete Example

A production-ready configuration with multiple agents, providers, and extensions:

```json
{
  "$schema": "https://botnexus.dev/schemas/config.json",
  "version": 1,
  "gateway": {
    "listenUrl": "http://localhost:5005",
    "defaultAgentId": "assistant",
    "agentsDirectory": "~/.botnexus/agents",
    "sessionStore": {
      "type": "Sqlite",
      "connectionString": "Data Source=~/.botnexus/sessions.sqlite"
    },
    "compaction": {
      "maxMessagesBeforeCompaction": 100,
      "retainLastMessages": 20
    },
    "cors": {
      "allowedOrigins": ["http://localhost:3000"]
    },
    "rateLimit": {
      "requestsPerMinute": 120
    },
    "extensions": {
      "path": "~/.botnexus/extensions",
      "enabled": true
    },
    "world": {
      "id": "production-gateway",
      "displayName": "Production BotNexus"
    }
  },
  "agents": {
    "assistant": {
      "displayName": "General Assistant",
      "description": "Multi-purpose AI assistant",
      "provider": "copilot",
      "model": "gpt-4.1",
      "systemPromptFiles": ["SOUL.md", "IDENTITY.md"],
      "toolIds": ["web_search", "web_fetch"],
      "enabled": true,
      "extensions": {
        "botnexus-skills": {
          "enabled": true,
          "autoLoad": ["project-conventions"]
        }
      }
    },
    "coder": {
      "displayName": "Coding Agent",
      "description": "Code generation and review",
      "provider": "copilot",
      "model": "claude-opus-4.6",
      "allowedModels": ["claude-opus-4.6", "gpt-5.4"],
      "systemPromptFiles": ["SOUL.md", "IDENTITY.md", "TOOLS.md"],
      "toolIds": ["read_file", "write_file", "grep", "glob"],
      "subAgents": ["reviewer"],
      "enabled": true,
      "extensions": {
        "botnexus-mcp": {
          "toolPrefix": true,
          "servers": {
            "filesystem": {
              "command": "npx",
              "args": ["-y", "@modelcontextprotocol/server-filesystem", "/workspace"],
              "inheritEnv": false
            }
          }
        }
      }
    },
    "reviewer": {
      "displayName": "Code Reviewer",
      "description": "Code review specialist",
      "provider": "anthropic",
      "model": "claude-sonnet-4-20250514",
      "systemPromptFiles": ["SOUL.md", "reviewer-guidelines.md"],
      "enabled": true
    }
  },
  "providers": {
    "copilot": {
      "enabled": true,
      "apiKey": "auth:copilot",
      "baseUrl": "https://api.githubcopilot.com",
      "defaultModel": "gpt-4.1"
    },
    "anthropic": {
      "enabled": true,
      "apiKey": "${ANTHROPIC_API_KEY}",
      "baseUrl": "https://api.anthropic.com",
      "defaultModel": "claude-sonnet-4-20250514"
    }
  },
  "channels": {
    "webui": {
      "type": "signalr",
      "enabled": true
    },
    "telegram": {
      "botToken": "${TELEGRAM_BOT_TOKEN}",
      "agentId": "assistant",
      "allowedChatIds": [123456789]
    }
  },
  "cron": {
    "enabled": true,
    "jobs": {
      "daily-summary": {
        "name": "Daily Summary",
        "schedule": "0 9 * * *",
        "actionType": "agent-prompt",
        "agentId": "assistant",
        "message": "Generate a daily summary",
        "enabled": true
      }
    }
  },
  "apiKey": "production-api-key-change-me"
}
```

---

## Telemetry & Remote Collection

The optional `telemetry` section controls the in-process OpenTelemetry metrics/tracing plane and the optional remote OTLP exporter.

```json
{
  "telemetry": {
    "enabled": true,
    "exporter": {
      "type": "none"
    }
  }
}
```

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `enabled` | bool | `true` | Wires the in-process `MeterProvider`/`TracerProvider` for the canonical `BotNexus` scope. When `false`, the metrics facade still resolves but no OpenTelemetry providers are attached. |
| `exporter.type` | string | `none` | `none` (no egress, default), `otlp` (export to a collector), or `console` (local debug). |
| `exporter.endpoint` | string | _(unset)_ | OTLP collector endpoint. Required for `otlp`. `http://host:4317` (grpc) or `http://host:4318` (http/protobuf). |
| `exporter.protocol` | string | `grpc` | `grpc` or `http/protobuf`. |
| `exporter.headers` | object | `{}` | OTLP request headers (collector auth). **Secrets** - redacted in logs/config dumps. |
| `exporter.resource.serviceName` | string | `botnexus` | `service.name` attribute. |
| `exporter.resource.serviceInstanceId` | string | _(auto)_ | `service.instance.id`. Auto-generated stable per-process id when unset so an aggregator can tell instances apart. |
| `exporter.resource.deploymentEnvironment` | string | _(unset)_ | `deployment.environment` (e.g. `production`). |

**Off by default:** with `exporter.type` set to `none` (the default), BotNexus produces **zero network egress** - no OTLP connection is ever attempted and no endpoint is shipped. Remote collection is strictly opt-in.

**Serilog  OTel logs** routing is deferred; only metrics/trace export is wired today.

### Agent 365 observability export

The optional `telemetry.agent365` section routes BotNexus spans (turn / tool-call / provider-invocation, plus sub-agent spawn child spans) directly to the [Microsoft Agent 365 observability](https://learn.microsoft.com/en-us/microsoft-agent-365/developer/observability) endpoint over raw **OTLP/HTTP**. It is a **direct OTLP** integration (no `Microsoft.Agents.A365.Observability` SDK dependency) and is attached as an **additional** target alongside the generic `exporter`. **Off by default.**

```json
{
  "telemetry": {
    "enabled": true,
    "agent365": {
      "enabled": true,
      "endpoint": "https://agent365.svc.cloud.microsoft/observabilityService/tenants/<tenantId>/otlp/agents/<agentId>/traces?api-version=1",
      "authHeaderValue": "Bearer <access-token>"
    }
  }
}
```

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `agent365.enabled` | bool | `false` | Ship spans to Agent 365 over OTLP/HTTP when `true` and an `endpoint` is set. Zero egress until enabled. |
| `agent365.endpoint` | string | _(unset)_ | Agent 365 OTLP/HTTP traces endpoint (`.../otlp/agents/{agentId}/traces?api-version=1`). Required when enabled. |
| `agent365.authHeaderValue` | string | _(unset)_ | Convenience for the `Authorization` header (`Bearer <token>`). **Secret** - redacted in logs. Acquire via MSAL out of band. |
| `agent365.headers` | object | `{}` | Additional OTLP headers. **Secrets** - redacted in logs. |
| `agent365.resource.*` | object | see above | `serviceName` / `serviceInstanceId` / `deploymentEnvironment` resource attributes reported to Agent 365. |

Agent 365 ingestion requires a licensed tenant, tenant consent, and the `Agent365.Observability.OtelWrite` app role/scope on your agent app. See the [direct OTel integration guide](https://learn.microsoft.com/en-us/microsoft-agent-365/developer/direct-open-telemetry-integration). Sub-agent spawns surface as child spans of the parent turn for full delegation visibility.

### Remote-collection quickstart

1. Run an OTLP collector (OpenTelemetry Collector, Grafana Alloy, or a vendor gateway) with an OTLP receiver on `:4317`.
2. Point BotNexus at it in `~/.botnexus/config.json`:

   ```json
   {
     "telemetry": {
       "exporter": {
         "type": "otlp",
         "endpoint": "http://localhost:4317",
         "protocol": "grpc",
         "headers": { "Authorization": "Bearer <collector-token>" },
         "resource": { "deploymentEnvironment": "production" }
       }
     }
   }
   ```

3. Restart the gateway. The `botnexus.*` instruments flow to your collector, tagged with `service.name`/`service.instance.id`/`deployment.environment` so a downstream aggregator can attribute data per instance. Set `type` back to `none` to stop egress.

For the full field reference and a sample collector config, see [Configuration Guide  Telemetry](../configuration.md#telemetry-telemetryconfig).

## Hot Reload

BotNexus monitors `config.json` for changes and applies most updates without requiring a restart:

- ✅ **Agent definitions** — Add, remove, or modify agents
- ✅ **Provider settings** — Update API keys, models, base URLs
- ✅ **Cron jobs** — Add, remove, or reschedule jobs
- ✅ **Agent `fileAccess` policies** - Changed allow/deny paths re-register the agent and rebuild its path validator
- ✅ **Channel configuration** — Enable/disable channels
- ⚠️ **Gateway settings** — Require restart (listen URL, session store type)

Watch the logs for reload events:
```text
info: BotNexus.Gateway.Configuration[0]
      Configuration file changed, reloading...
info: BotNexus.Gateway[0]
      3 agent(s) registered
```

---

## Next Steps

- **[Create your first agent](agents.md)** — Define custom agents with system prompts
- **[Add extensions](extensions.md)** — Connect MCP servers, tools, and skills
- **[Explore the API](../api-reference.md)** — REST and SignalR endpoints
- **[Troubleshooting](troubleshooting.md)** — Common configuration issues
