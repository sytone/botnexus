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
10. [Complete Example](#complete-example)

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

## Gateway Settings

Gateway-level settings control the HTTP server, routing, and runtime behavior.

```json
{
  "gateway": {
    "listenUrl": "http://localhost:5005",
    "defaultAgentId": "assistant",
    "agentsDirectory": "~/.botnexus/agents",
    "sessionsDirectory": "~/.botnexus/workspace/sessions",
    "sessionStore": {
      "type": "File",
      "filePath": "~/.botnexus/workspace/sessions"
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
| `listenUrl` | string | `http://localhost:5005` | HTTP listen URL for REST API and WebUI |
| `defaultAgentId` | string | `null` | Agent to route to when none specified |
| `agentsDirectory` | string | `~/.botnexus/agents` | Directory containing agent descriptor JSON files |
| `sessionsDirectory` | string | `~/.botnexus/workspace/sessions` | Directory for session persistence |
| `sessionStore.type` | string | `File` | Session store type: `File`, `InMemory`, or `Sqlite` |
| `sessionStore.filePath` | string | (same as sessionsDirectory) | Path for file-based session store |
| `sessionStore.connectionString` | string | `null` | SQLite connection string (when type=Sqlite) |
| `compaction.maxMessagesBeforeCompaction` | int | `100` | Trigger compaction after this many messages |
| `compaction.retainLastMessages` | int | `20` | Keep this many recent messages after compaction |
| `cors.allowedOrigins` | array | `[]` | Allowed CORS origins for browser clients |
| `rateLimit.requestsPerMinute` | int | `60` | Max requests per client per minute |
| `rateLimit.windowSeconds` | int | `60` | Rate limit window size in seconds |
| `logLevel` | string | `Information` | Logging level: `Trace`, `Debug`, `Information`, `Warning`, `Error`, `Critical` |
| `extensions.path` | string | `~/.botnexus/extensions` | Root directory for extension assemblies |
| `extensions.enabled` | bool | `true` | Enable/disable dynamic extension loading |
| `world.id` | string | `local-gateway` | Unique identifier for this Gateway instance |
| `world.displayName` | string | `BotNexus Gateway` | Human-readable Gateway name |

---

## Agent Configuration

Agents are defined in the `agents` section, keyed by agent ID.

```json
{
  "agents": {
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
        "maxEntries": 100
      },
      "soul": {
        "enabled": true,
        "idleTimeoutMinutes": 30
      },
      "sessionAccess": {
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

### Agent Settings Reference

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `displayName` | string | (required) | Human-readable agent name shown in UI |
| `description` | string | `null` | Optional description of agent's purpose |
| `provider` | string | (required) | Provider key (e.g., `copilot`, `anthropic`, `openai`) |
| `model` | string | (required) | Default model ID for this agent |
| `allowedModels` | array | `[]` | Models this agent can use. Empty = unrestricted within provider |
| `systemPromptFiles` | array | `[]` | System prompt files to load (in order). Empty = default order: `AGENTS.md`, `SOUL.md`, `TOOLS.md`, `BOOTSTRAP.md`, `IDENTITY.md`, `USER.md` |
| `systemPromptFile` | string | `null` | (Legacy) Single system prompt file path |
| `toolIds` | array | `[]` | Tool identifiers this agent can use |
| `subAgents` | array | `[]` | Agent IDs this agent can call as sub-agents |
| `isolationStrategy` | string | `in-process` | Execution isolation: `in-process` or `sandbox` |
| `maxConcurrentSessions` | int | `0` | Max concurrent sessions (0 = unlimited) |
| `enabled` | bool | `true` | Enable/disable this agent |
| `memory.enabled` | bool | `false` | Enable memory system for this agent |
| `memory.maxEntries` | int | `100` | Max memory entries to retain |
| `soul.enabled` | bool | `false` | Enable soul session (persistent agent identity) |
| `soul.idleTimeoutMinutes` | int | `30` | Idle timeout for soul sessions |
| `sessionAccess.level` | string | `own` | Session access: `own`, `allowlist`, or `all` |
| `sessionAccess.allowedAgents` | array | `[]` | Agent IDs to allow (when level=`allowlist`) |
| `toolPolicy.alwaysApprove` | array | `[]` | Tools requiring approval before execution |
| `toolPolicy.neverApprove` | array | `[]` | Trusted tools that skip approval |
| `toolPolicy.denied` | array | `[]` | Tools completely blocked for this agent |

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
      "type": "telegram",
      "enabled": true,
      "settings": {
        "botToken": "${TELEGRAM_BOT_TOKEN}",
        "allowedUsers": ["123456789"]
      }
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

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `type` | string | (required) | Channel adapter type: `signalr`, `telegram`, `tui` |
| `enabled` | bool | `true` | Enable/disable this channel |
| `settings` | object | `{}` | Channel-specific settings |

### Channel-Specific Settings

**SignalR (WebUI):**
- No additional settings required. WebUI is served at the Gateway root URL.

**Telegram:**
```json
{
  "settings": {
    "botToken": "${TELEGRAM_BOT_TOKEN}",
    "allowedUsers": ["123456789", "987654321"]
  }
}
```

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
- `actionType` (string): `agent-prompt`, `webhook`, or `shell`
- `agentId` (string): Target agent (for `agent-prompt` jobs)
- `message` (string): Prompt message (for `agent-prompt` jobs)
- `webhookUrl` (string): Webhook URL (for `webhook` jobs)
- `shellCommand` (string): Shell command (for `shell` jobs)
- `enabled` (bool): Enable/disable this job

---

## Session Management

Sessions persist conversation history to disk.

```json
{
  "gateway": {
    "sessionsDirectory": "~/.botnexus/workspace/sessions",
    "sessionStore": {
      "type": "File",
      "filePath": "~/.botnexus/workspace/sessions"
    },
    "compaction": {
      "maxMessagesBeforeCompaction": 100,
      "retainLastMessages": 20
    }
  }
}
```

**Session Store Types:**

1. **File** (default): JSONL files in `sessionsDirectory`
   ```json
   {
     "sessionStore": {
       "type": "File",
       "filePath": "~/.botnexus/workspace/sessions"
     }
   }
   ```

2. **InMemory**: Ephemeral (lost on restart)
   ```json
   {
     "sessionStore": {
       "type": "InMemory"
     }
   }
   ```

3. **Sqlite**: SQLite database
   ```json
   {
     "sessionStore": {
       "type": "Sqlite",
       "connectionString": "Data Source=~/.botnexus/sessions.db"
     }
   }
   ```

**Compaction Settings:**
- `maxMessagesBeforeCompaction`: Trigger compaction after this many messages
- `retainLastMessages`: Keep this many recent messages after compaction

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
    "sessionsDirectory": "~/.botnexus/workspace/sessions",
    "sessionStore": {
      "type": "File"
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
      "type": "telegram",
      "enabled": true,
      "settings": {
        "botToken": "${TELEGRAM_BOT_TOKEN}",
        "allowedUsers": ["123456789"]
      }
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

## Hot Reload

BotNexus monitors `config.json` for changes and applies most updates without requiring a restart:

- ✅ **Agent definitions** — Add, remove, or modify agents
- ✅ **Provider settings** — Update API keys, models, base URLs
- ✅ **Cron jobs** — Add, remove, or reschedule jobs
- ✅ **Channel configuration** — Enable/disable channels
- ⚠️ **Gateway settings** — Require restart (listen URL, session store type)

Watch the logs for reload events:
```
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
