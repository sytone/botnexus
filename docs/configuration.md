# BotNexus Configuration Guide

BotNexus uses a hierarchical, dictionary-based configuration model with a unified home directory at `~/.botnexus/` (or `BOTNEXUS_HOME`).

## Table of Contents

1. [Quick Start](#quick-start)
2. [Configuration Hierarchy](#configuration-hierarchy)
3. [Primary Deployment: ~/.botnexus/](#primary-deployment-botnexus)
4. [Project Defaults: appsettings.json](#project-defaults-appsettingsjson)
5. [Configuration Sections](#configuration-sections)
6. [Hot Reload](#hot-reload)
7. [Extension Configuration](#extension-configuration)
8. [Environment Variable Overrides](#environment-variable-overrides)
9. [Security Best Practices](#security-best-practices)
10. [Examples](#examples)

---

## Quick Start

### Using the CLI (Recommended)

Instead of editing JSON manually, use the `botnexus` CLI to manage configuration:

```powershell
# Initialize home directory
botnexus init

# List agents
botnexus agent list

# Add an agent
botnexus agent add myagent --provider copilot --model gpt-4.1

# Update a setting
botnexus config set gateway.listenUrl http://localhost:8080

# Validate
botnexus validate
```

See [CLI Reference](cli-reference.md) for all available commands.

### Manual Configuration (`~/.botnexus/config.json`)

On first run, BotNexus creates a minimal default config:

```json
{
  "BotNexus": {
    "ExtensionsPath": "~/.botnexus/extensions",
    "Providers": {},
    "Channels": {
      "Instances": {}
    },
    "Tools": {
      "Extensions": {},
      "McpServers": {}
    }
  }
}
```

To add your first provider (e.g., Copilot), add it to the `Providers` section:

```json
{
  "BotNexus": {
    "ExtensionsPath": "~/.botnexus/extensions",
    "Providers": {
      "copilot": {
        "Auth": "oauth",
        "DefaultModel": "gpt-4o",
        "ApiBase": "https://api.githubcopilot.com"
      }
    }
  }
}
```

---

## Configuration Hierarchy

BotNexus follows a **defaults → overrides** pattern:

1. **Defaults** — Built-in constants in code (e.g., `Model = "gpt-4o"`)
2. **Configuration file** — `~/.botnexus/config.json` (or `${BOTNEXUS_HOME}/config.json` when set)
3. **Environment variables** — Override any setting (see [Environment Variable Overrides](#environment-variable-overrides))
4. **Named agent overrides** — Per-agent customization in `Agents.Named` dict

**Example:**
```
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

```
~/.botnexus/
├── config.json
├── extensions/
│   ├── providers/
│   ├── channels/
│   └── tools/
├── tokens/
├── sessions/
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
| `ExtensionsPath` | string | `~/.botnexus/extensions` | Path to extension discovery folder (dynamic loading) |
| `Extensions` | ExtensionLoadingConfig | — | Extension loader behavior (signing, max assemblies) |
| `Agents` | AgentDefaults | — | Agent defaults and named agent configurations |
| `Providers` | ProvidersConfig | — | LLM provider registry (Copilot, OpenAI, Anthropic, Azure) |
| `Channels` | ChannelsConfig | — | Social channel integrations (Telegram, Discord, Slack) |
| `Gateway` | GatewayConfig | — | WebSocket gateway server settings |
| `Tools` | ToolsConfig | — | Tool/extension tool settings (exec, web search, MCP) |
| `Api` | ApiConfig | — | OpenAI-compatible REST API (optional) |
| `Cron` | CronConfig | — | Scheduled job execution (agent prompts, system actions, maintenance) |

---

### Agents: AgentDefaults

Default settings for all agents. Individual agents can override any property.

```json
{
  "Agents": {
    "Workspace": "~/.botnexus/workspace",
    "Model": "gpt-4o",
    "ContextWindowTokens": 65536,
    "MaxToolIterations": 40,
    "Timezone": "UTC",
    "Named": {
      "planner": { ... },
      "writer": { ... }
    }
  }
}
```

#### AgentDefaults Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Workspace` | string | `~/.botnexus/workspace` | Directory for agent session data, memory, and local files |
| `Model` | string | `gpt-4o` | Default LLM model name (e.g., gpt-4o, gpt-4-turbo, claude-3-5-sonnet) |
| `MaxTokens` | int? | null | Max tokens per response (if unset, provider decides) |
| `ContextWindowTokens` | int? | 65536 | Total context window size used for planning and session limits |
| `Temperature` | double? | null | Randomness (0.0=deterministic, 1.0=creative). If unset, provider decides |
| `MaxToolIterations` | int | 40 | Max tool call loops in a single agent cycle |
| `MaxRepeatedToolCalls` | int | 2 | Max times the same tool can be called with identical arguments (loop detection) |
| `Timezone` | string | `UTC` | Default timezone for agent operations (IANA format) |
| `Named` | dict | `{}` | Per-agent customizations (see AgentConfig below) |

#### AgentConfig: Per-Agent Customization

Override any default for a specific agent:

```json
{
  "Agents": {
    "Named": {
      "planner": {
        "Model": "gpt-4-turbo",
        "SystemPrompt": "You are an expert strategic planner...",
        "Temperature": 0.5,
        "MaxTokens": 12000,
        "EnableMemory": true,
        "Skills": ["research", "analysis"],
        "McpServers": [
          { "Name": "filesystem", "Command": "npx", ... }
        ],
        "CronJobs": [                                              // ⚠️ Deprecated — use Cron.Jobs
          { "Name": "daily-briefing", "Schedule": "0 9 * * *", ... }
        ]
      }
    }
  }
}
```

| Property | Type | Description |
|----------|------|-------------|
| `Name` | string | Agent identifier (matches key in Named dict) |
| `SystemPrompt` | string | Custom system message (overrides default) |
| `SystemPromptFile` | string | Path to .txt file with system prompt (if SystemPrompt not set) |
| `Workspace` | string | Custom workspace directory for this agent |
| `Model` | string | Override default model for this agent |
| `Provider` | string | Override default provider (copilot, openai, anthropic, etc.) |
| `MaxTokens` | int? | Override default max tokens. When `null`, provider uses its own default |
| `Temperature` | double? | Override default temperature (0.0-2.0). When `null`, provider uses its own default |
| `MaxToolIterations` | int | Override default max tool calls |
| `MaxRepeatedToolCalls` | int | Override default repeated tool call limit (loop detection) |
| `Timezone` | string | Override default timezone |
| `EnableMemory` | bool | Enable persistent memory for this agent |
| `DisallowedTools` | list | Tool names to exclude for this agent (e.g., `["shell", "filesystem"]`) — see [Internal Tools](#internal-tools) |
| `McpServers` | list | MCP servers enabled for this agent (see [MCP Servers](#mcp-servers)) |
| `Skills` | list | Named skill references (plugin extension names) |
| `DisabledSkills` | list | Skill names or patterns to exclude for this agent (e.g., `["debug-*", "experimental-*", "test-skill"]`) — supports wildcards (`*`, `?`). See [Skills Guide](./skills.md#disabling-skills) |
| `CronJobs` | list | **Deprecated.** Use centralized `Cron.Jobs` instead (see [Cron and Scheduling Guide](./cron-and-scheduling.md)). Legacy entries are auto-migrated at startup. |

---

#### Agent Iteration & Loop Detection

Each agent's tool calling loop is bounded by two configurable limits to prevent infinite loops and excessive token usage:

**MaxToolIterations** — Total number of LLM calls in a single agent cycle
- **Default:** 40
- **Purpose:** Limits total loop iterations; prevents runaway multi-turn conversations
- **Typical usage:** File operations (5-10), analysis chains (10-20), complex planning (30-40)

**MaxRepeatedToolCalls** — Max times the same tool can be called with identical arguments
- **Default:** 2
- **Purpose:** Detects when agents get stuck repeatedly calling the same tool without variation
- **How it works:** Tracks tool signatures (`tool_name + arguments`); blocks execution when threshold is reached; returns error to LLM

**Example Configuration:**

```json
{
  "Agents": {
    "MaxToolIterations": 40,
    "MaxRepeatedToolCalls": 2,
    "Named": {
      "careful-agent": {
        "MaxToolIterations": 10,        // Strict: max 10 LLM calls
        "MaxRepeatedToolCalls": 1       // No retries allowed
      },
      "researcher": {
        "MaxToolIterations": 100,       // Permissive: max 100 LLM calls
        "MaxRepeatedToolCalls": 5       // Allow up to 5 retries per tool
      }
    }
  }
}
```

**Loop Detection Example:**

When an agent calls `search_files("")` three times without changing arguments:

```
Iteration 0: search_files("") → executes (count: 1)
Iteration 1: search_files("") → executes (count: 2)
Iteration 2: search_files("") → blocked! count: 2 >= MaxRepeatedToolCalls: 2
  └─ Agent receives error: "Tool 'search_files' called 3 times with identical arguments"
```

The agent then receives this error as a tool result and can decide to:
1. Try a different search query
2. Use a different tool
3. Proceed with current context

---

### Providers: ProvidersConfig

Dictionary mapping provider names to provider configurations. Keys are case-insensitive and match extension folder names under `extensions/providers/{name}/`.

```json
{
  "Providers": {
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
  "BotNexus": {
    "Providers": {
      "copilot": {
        "Auth": "oauth",
        "DefaultModel": "claude-opus-4.6",
        "TimeoutSeconds": 120,
        "MaxRetries": 3
      }
    },
    "Agents": {
      "Named": {
        "analyst": {
          "Model": "gpt-4o",
          "Provider": "copilot"
        },
        "researcher": {
          "Model": "gpt-5.4",
          "Provider": "copilot"
        },
        "coder": {
          "Model": "claude-opus-4.6",
          "Provider": "copilot"
        }
      }
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
  "Providers": {
    "copilot": {
      "Auth": "oauth",
      "DefaultModel": "gpt-4o",
      "ApiBase": "https://api.individual.githubcopilot.com",
      "OAuthClientId": "Iv1.b507a08c87ecfe98",
      "TimeoutSeconds": 120,
      "MaxRetries": 3
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

```
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
  "Providers": {
    "openai": {
      "Auth": "apikey",
      "ApiKey": "sk-...",
      "DefaultModel": "gpt-4-turbo",
      "ApiBase": "https://api.openai.com/v1",
      "TimeoutSeconds": 120,
      "MaxRetries": 3
    }
  }
}
```

#### Anthropic Provider

**Folder:** `extensions/providers/anthropic/`  
**Auth:** API Key

```json
{
  "Providers": {
    "anthropic": {
      "Auth": "apikey",
      "ApiKey": "sk-ant-...",
      "DefaultModel": "claude-3-5-sonnet-20241022",
      "ApiBase": "https://api.anthropic.com",
      "TimeoutSeconds": 120,
      "MaxRetries": 3
    }
  }
}
```

#### Azure OpenAI Provider

**Folder:** `extensions/providers/azure-openai/`  
**Auth:** API Key

```json
{
  "Providers": {
    "azure-openai": {
      "Auth": "apikey",
      "ApiKey": "your-azure-key",
      "DefaultModel": "deployment-name",
      "ApiBase": "https://your-resource.openai.azure.com/openai/deployments/deployment-name",
      "TimeoutSeconds": 120,
      "MaxRetries": 3
    }
  }
}
```

---

### Channels: ChannelsConfig

Social channel integrations. Keys in `Instances` dict are case-insensitive and match extension folder names.

```json
{
  "Channels": {
    "SendProgress": true,
    "SendToolHints": false,
    "SendMaxRetries": 3,
    "Instances": {
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

```json
{
  "Channels": {
    "Instances": {
      "telegram": {
        "Enabled": true,
        "BotToken": "123456789:ABCdefGHijKlmnoPQRstuvWXYZ",
        "AllowFrom": ["12345", "67890"]
      }
    }
  }
}
```

#### Discord Channel

**Folder:** `extensions/channels/discord/`

```json
{
  "Channels": {
    "Instances": {
      "discord": {
        "Enabled": true,
        "BotToken": "MzA5NTkyMzAzMTgyNzIzODQw.C_DUbA.Tz3u1NBoI7K-xypwWD",
        "AllowFrom": []
      }
    }
  }
}
```

#### Slack Channel

**Folder:** `extensions/channels/slack/`

```json
{
  "Channels": {
    "Instances": {
      "slack": {
        "Enabled": true,
        "BotToken": "xoxb-1234567890-1234567890-xxxxxxxxxxxxx",
        "SigningSecret": "8f742231b91ee1522d552420d224e9aa",
        "AllowFrom": []
      }
    }
  }
}
```

---

### Gateway: GatewayConfig

WebSocket gateway server settings.

```json
{
  "Gateway": {
    "Host": "0.0.0.0",
    "Port": 18790,
    "ApiKey": "secret-gateway-key",
    "WebSocketEnabled": true,
    "WebSocketPath": "/ws",
    "DefaultAgent": "default",
    "BroadcastWhenAgentUnspecified": false,
    "Heartbeat": {
      "Enabled": true,
      "IntervalSeconds": 1800
    }
  }
}
```

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Host` | string | `0.0.0.0` | Bind address (0.0.0.0 = all interfaces, 127.0.0.1 = localhost) |
| `Port` | int | 18790 | Listen port for gateway server |
| `ApiKey` | string | null | Optional API key for authentication (recommended for production) |
| `WebSocketEnabled` | bool | true | Enable WebSocket support |
| `WebSocketPath` | string | `/ws` | WebSocket endpoint path |
| `DefaultAgent` | string | null | Default agent name if message has no agent metadata |
| `BroadcastWhenAgentUnspecified` | bool | false | If true, route to all agents when agent not specified |
| `Heartbeat.Enabled` | bool | true | Enable heartbeat/keepalive messages |
| `Heartbeat.IntervalSeconds` | int | 1800 | Heartbeat interval (30 minutes) |

**API Key Authentication:**
If `ApiKey` is set, clients must include it in WebSocket headers or REST requests:
```
Authorization: Bearer YOUR-GATEWAY-KEY
```

---

### Tools: ToolsConfig

Tool and extension tool settings.

```json
{
  "Tools": {
    "RestrictToWorkspace": false,
    "Exec": {
      "Enable": true,
      "Timeout": 60
    },
    "Web": {
      "Search": {
        "Provider": "brave",
        "ApiKey": "...",
        "MaxResults": 5
      }
    },
    "Extensions": {},
    "McpServers": {
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
| `shell` | Execute shell commands | Enabled if `Exec.Enable=true` | Yes |

**Disabling Tools per Agent:**

Use the `DisallowedTools` array in agent configuration to disable specific tools:

```json
{
  "Agents": {
    "Named": {
      "secure-agent": {
        "DisallowedTools": ["shell", "filesystem"]
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
  "Tools": {
    "McpServers": {
      "filesystem": {
        "Type": "Stdio",
        "Command": "npx",
        "Args": ["-y", "@modelcontextprotocol/server-filesystem", "/tmp"],
        "ToolTimeout": 30,
        "EnabledTools": ["*"]
      },
      "github-mcp": {
        "Type": "Sse",
        "Url": "http://localhost:3001/sse",
        "Headers": {
          "Authorization": "Bearer YOUR_TOKEN"
        },
        "ToolTimeout": 30,
        "EnabledTools": ["*"]
      },
      "trello": {
        "Type": "Stdio",
        "Command": "node",
        "Args": ["./mcp-servers/trello-server.js"],
        "Env": {
          "TRELLO_API_KEY": "xxx",
          "TRELLO_API_TOKEN": "yyy"
        },
        "ToolTimeout": 30,
        "EnabledTools": ["*"]
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
  "Api": {
    "Host": "127.0.0.1",
    "Port": 8900,
    "Timeout": 120.0,
    "Enabled": false
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

### Nullable Parameters (Provider Defaults)

When `MaxTokens` or `Temperature` are not specified (null), providers use their own defaults:

```json
{
  "Agents": {
    "Model": "gpt-4o",
    "MaxTokens": null,      // Provider uses OpenAI default (e.g., 4096)
    "Temperature": null     // Provider uses OpenAI default (e.g., 0.7)
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
  "Agents": {
    "Named": {
      "fast-agent": {
        "Model": "gpt-4o",
        "Temperature": 0.5     // Set explicitly
        // MaxTokens not set → use OpenAI default
      },
      "creative-agent": {
        "Model": "claude-3-5-sonnet",
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

On reload, the Gateway publishes a `gateway.config.reloaded` activity event listing which subsystems were updated. WebUI and WebSocket clients receive this event in real time.

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

```
extensions/
├── providers/
│   ├── copilot/              # BotNexus.Providers.Copilot.dll
│   ├── openai/               # BotNexus.Providers.OpenAI.dll
│   ├── anthropic/            # BotNexus.Providers.Anthropic.dll
│   └── azure-openai/         # Custom provider
├── channels/
│   ├── telegram/             # BotNexus.Channels.Telegram.dll
│   ├── discord/              # BotNexus.Channels.Discord.dll
│   └── slack/                # BotNexus.Channels.Slack.dll
└── tools/
    ├── github/               # BotNexus.Tools.GitHub.dll
    └── custom-tool/          # Custom tool extension
```

### Extension Registration

Each extension assembly can implement `IExtensionRegistrar` to register types in the DI container:

```csharp
public class CopilotExtensionRegistrar : IExtensionRegistrar
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<ILlmProvider>(sp =>
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
  "Tools": {
    "Extensions": {
      "github": {
        "Token": "ghp_...",
        "DefaultOwner": "microsoft",
        "ApiBase": "https://api.github.com"
      }
    }
  }
}
```

Extensions access their config from the DI container or from the main `BotNexusConfig`.

---

## Environment Variable Overrides

Any configuration value can be overridden via environment variables using the standard .NET Configuration pattern:

```
BotNexus__<Path>__<To>__<Property>=value
```

Double underscores (`__`) separate nested levels. The prefix `BotNexus__` is required.

### Examples

```bash
# Override default agent model
export BotNexus__Agents__Model=claude-3-5-sonnet

# Override Copilot API base
export BotNexus__Providers__copilot__ApiBase=https://api.githubcopilot.com

# Override gateway port
export BotNexus__Gateway__Port=9000

# Override Telegram bot token
export BotNexus__Channels__Instances__telegram__BotToken=123456789:ABCdef...

# Override planner agent model
export BotNexus__Agents__Named__planner__Model=gpt-4-turbo

# Override MCP server command
export BotNexus__Tools__McpServers__filesystem__Command=/usr/local/bin/mcp-filesystem
```

### Precedence

Configuration sources are loaded in order — later sources override earlier ones:

1. **Code defaults** (built-in fallbacks, lowest priority)
2. **appsettings.json** (project defaults in `src/BotNexus.Gateway/`)
3. **appsettings.{Environment}.json** (environment-specific overrides)
4. **~/.botnexus/config.json** (user configuration)
5. **Environment variables** (highest priority)

---

## Security Best Practices

### 1. Protect Secrets

**Never commit API keys or tokens to source control.**

Options:
- **User secrets** (.NET Core dev only):
  ```bash
  dotnet user-secrets set "BotNexus:Providers:openai:ApiKey" "sk-..."
  ```
- **Environment variables** (production):
  ```bash
  export BotNexus__Providers__openai__ApiKey=sk-...
  ```
- **Secret management** (Azure Key Vault, HashiCorp Vault, etc.)

### 2. API Key Authentication

For production Gateway deployments, always set `Gateway.ApiKey`:

```json
{
  "Gateway": {
    "ApiKey": "random-secret-key"
  }
}
```

Clients must provide this key:
```
Authorization: Bearer random-secret-key
```

### 3. OAuth Token Storage

OAuth tokens are stored encrypted (on supported platforms) at:
```
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
  "Tools": {
    "RestrictToWorkspace": true
  }
}
```

### 5. Channel Allow Lists

Use `AllowFrom` to whitelist specific users/chats:

```json
{
  "Channels": {
    "Instances": {
      "telegram": {
        "Enabled": true,
        "BotToken": "...",
        "AllowFrom": ["12345", "67890"]
      }
    }
  }
}
```

### 6. Sensitive Config in Development

Use `appsettings.Development.json` for local dev secrets (add to .gitignore):

```json
{
  "BotNexus": {
    "Providers": {
      "openai": {
        "ApiKey": "sk-local-dev-key-here"
      }
    }
  }
}
```

---

## Examples

### Example 1: Basic Setup with Copilot

```json
{
  "BotNexus": {
    "Agents": {
      "Model": "gpt-4o"
    },
    "Providers": {
      "copilot": {
        "Auth": "oauth",
        "DefaultModel": "gpt-4o",
        "ApiBase": "https://api.githubcopilot.com"
      }
    },
    "Channels": {
      "SendProgress": true,
      "Instances": {
        "telegram": {
          "Enabled": false
        }
      }
    },
    "Gateway": {
      "Host": "127.0.0.1",
      "Port": 18790,
      "WebSocketEnabled": true
    }
  }
}
```

### Example 2: Multi-Agent Team with OpenAI Fallback

```json
{
  "BotNexus": {
    "Agents": {
      "Model": "gpt-4o",
      "Named": {
        "planner": {
          "Model": "gpt-4-turbo",
          "Temperature": 0.5,
          "MaxTokens": 12000,
          "SystemPrompt": "You are a strategic planning expert. Break down complex goals into actionable steps."
        },
        "writer": {
          "Model": "gpt-4o",
          "Temperature": 0.7,
          "MaxTokens": 16000,
          "SystemPrompt": "You are a technical writer. Write clear, concise documentation."
        }
      }
    },
    "Providers": {
      "openai": {
        "Auth": "apikey",
        "ApiKey": "sk-...",
        "DefaultModel": "gpt-4-turbo"
      }
    },
    "Gateway": {
      "DefaultAgent": "planner",
      "BroadcastWhenAgentUnspecified": false
    }
  }
}
```

### Example 3: Full Stack with Multiple Channels and MCP Servers

```json
{
  "BotNexus": {
    "Agents": {
      "Workspace": "~/.botnexus/workspace",
      "Model": "gpt-4o",
      "Named": {
        "researcher": {
          "Model": "gpt-4o",
          "Temperature": 0.3,
          "McpServers": ["filesystem", "github-mcp"]
        }
      }
    },
    "Providers": {
      "copilot": {
        "Auth": "oauth",
        "DefaultModel": "gpt-4o",
        "ApiBase": "https://api.githubcopilot.com"
      }
    },
    "Channels": {
      "SendProgress": true,
      "SendToolHints": true,
      "Instances": {
        "telegram": {
          "Enabled": true,
          "BotToken": "123456789:ABCdef...",
          "AllowFrom": ["12345"]
        },
        "discord": {
          "Enabled": true,
          "BotToken": "xoxp-..."
        },
        "slack": {
          "Enabled": true,
          "BotToken": "xoxb-...",
          "SigningSecret": "8f742231b91ee1522d..."
        }
      }
    },
    "Gateway": {
      "Host": "0.0.0.0",
      "Port": 18790,
      "ApiKey": "gateway-secret-key",
      "DefaultAgent": "researcher",
      "Heartbeat": {
        "Enabled": true,
        "IntervalSeconds": 1800
      }
    },
    "Tools": {
      "RestrictToWorkspace": false,
      "Exec": {
        "Enable": true,
        "Timeout": 60
      },
      "Web": {
        "Search": {
          "Provider": "brave",
          "ApiKey": "...",
          "MaxResults": 10
        }
      },
      "McpServers": {
        "filesystem": {
          "Type": "Stdio",
          "Command": "npx",
          "Args": ["-y", "@modelcontextprotocol/server-filesystem", "/tmp"],
          "ToolTimeout": 30,
          "EnabledTools": ["*"]
        },
        "github-mcp": {
          "Type": "Sse",
          "Url": "http://localhost:3001/sse",
          "Headers": {
            "Authorization": "Bearer github-token"
          },
          "ToolTimeout": 30,
          "EnabledTools": ["*"]
        }
      }
    },
    "Api": {
      "Host": "127.0.0.1",
      "Port": 8900,
      "Timeout": 120.0,
      "Enabled": false
    }
  }
}
```

### Example 4: Using Environment Variables for Secrets

appsettings.json:
```json
{
  "BotNexus": {
    "Providers": {
      "openai": {
        "Auth": "apikey",
        "ApiKey": ""
      }
    }
  }
}
```

Environment setup:
```bash
#!/bin/bash
export BotNexus__Providers__openai__ApiKey="sk-$(openssl rand -hex 32)"
export BotNexus__Gateway__ApiKey="$(openssl rand -hex 32)"
./BotNexus.Gateway
```

---

## Troubleshooting

### Agent Not Responding

1. Check `DefaultAgent` is set and matches a named agent (if using named agents)
2. Verify provider config: `Auth`, `ApiKey` (for apikey auth), or OAuth token (for oauth)
3. Check `Enabled` flag on channels if using Telegram/Discord/Slack
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

- [BotNexus README](../README.md) — Project overview
- [Architecture Guide](./architecture.md) — System design and component overview
- [Cron and Scheduling Guide](./cron-and-scheduling.md) — Scheduled jobs and automation
- [API Reference](./) — Gateway WebSocket and REST API docs
- [Extension Development](./extension-development.md) — Building custom channels, providers, and tools
