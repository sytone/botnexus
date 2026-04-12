# Working with Agents

This guide covers everything you need to know about creating, configuring, and managing agents in BotNexus.

## What is an Agent?

An **agent** in BotNexus is an AI assistant with:

1. **Identity** — Display name, description, and purpose
2. **LLM Provider** — Which API to use (Copilot, Anthropic, OpenAI, etc.)
3. **Model** — Which specific model to invoke (GPT-4.1, Claude Opus, etc.)
4. **System Prompt** — Instructions defining the agent's behavior and knowledge
5. **Tools** — Capabilities like file access, web search, code execution
6. **Extensions** — MCP servers, skills, and custom integrations

Each agent runs independently with its own:
- Session history
- Tool access
- Memory (optional)
- Soul session (optional persistent identity)

---

## Agent Descriptor Format

Agents are defined in `config.json` under the `agents` section:

```json
{
  "agents": {
    "assistant": {
      "displayName": "General Assistant",
      "description": "Multi-purpose AI helper",
      "provider": "copilot",
      "model": "gpt-4.1",
      "systemPromptFiles": ["SOUL.md", "IDENTITY.md"],
      "toolIds": ["web_search", "read_file"],
      "enabled": true
    }
  }
}
```

**Alternatively**, define agents as separate JSON files in `~/.botnexus/agents/`:

**`~/.botnexus/agents/assistant.json`:**
```json
{
  "$schema": "https://botnexus.dev/schemas/agent-descriptor.json",
  "agentId": "assistant",
  "displayName": "General Assistant",
  "description": "Multi-purpose AI helper",
  "modelId": "gpt-4.1",
  "apiProvider": "copilot",
  "systemPromptFiles": ["SOUL.md", "IDENTITY.md"],
  "toolIds": ["web_search", "read_file"]
}
```

BotNexus loads agents from **both** `config.json` and individual files in `agentsDirectory`.

---

## Creating Your First Agent

### Step 1: Define the Agent

Add an agent to `~/.botnexus/config.json`:

```json
{
  "agents": {
    "my-assistant": {
      "displayName": "My Assistant",
      "description": "Personal AI assistant for daily tasks",
      "provider": "copilot",
      "model": "gpt-4.1",
      "enabled": true
    }
  }
}
```

### Step 2: Create Agent Workspace

Create a directory for your agent's files:

```bash
mkdir -p ~/.botnexus/agents/my-assistant
```

### Step 3: Write a System Prompt

Create `~/.botnexus/agents/my-assistant/IDENTITY.md`:

```markdown
# Identity: My Assistant

You are a helpful personal assistant. Your role is to:

- Help with daily tasks and reminders
- Answer questions concisely and accurately
- Provide recommendations when asked

## Communication Style

- Be friendly and conversational
- Use clear, simple language
- Ask clarifying questions when needed

## Boundaries

- Decline requests for harmful content
- Admit when you don't know something
- Focus on practical, actionable advice
```

### Step 4: Apply Configuration

BotNexus automatically detects the config change (hot reload). Verify in the logs:

```
info: BotNexus.Gateway.Configuration[0]
      Configuration file changed, reloading...
info: BotNexus.Gateway[0]
      Agent 'my-assistant' registered successfully
```

### Step 5: Test Your Agent

Using the WebUI:
1. Open `http://localhost:5005`
2. Select **my-assistant** from the dropdown
3. Send a message: "What can you help me with?"

Using the API:
```bash
curl -X POST http://localhost:5005/api/chat \
  -H "Content-Type: application/json" \
  -d '{
    "agentId": "my-assistant",
    "content": "What can you help me with?"
  }'
```

---

## Agent Configuration Options

### Basic Settings

```json
{
  "displayName": "Agent Name",
  "description": "What this agent does",
  "provider": "copilot",
  "model": "gpt-4.1",
  "enabled": true
}
```

- **`displayName`** (required): Name shown in UI and logs
- **`description`** (optional): Purpose and capabilities
- **`provider`** (required): Provider key (`copilot`, `anthropic`, `openai`)
- **`model`** (required): Model ID (e.g., `gpt-4.1`, `claude-opus-4.6`)
- **`enabled`** (default: `true`): Enable/disable this agent

### Model Selection

Restrict which models an agent can use:

```json
{
  "model": "gpt-4.1",
  "allowedModels": ["gpt-4.1", "gpt-4o", "claude-sonnet-4-20250514"]
}
```

If `allowedModels` is:
- **Empty array `[]`**: Agent can use any model from its provider
- **Specified list**: Agent restricted to these models
- Users can switch models in the WebUI or via API (if in allowed list)

### System Prompts

**Option 1: Inline System Prompt (Simple)**

```json
{
  "systemPrompt": "You are a helpful assistant. Be concise and accurate."
}
```

**Option 2: Single File (Legacy)**

```json
{
  "systemPromptFile": "prompts/assistant.md"
}
```

**Option 3: Multiple Files (Recommended)**

```json
{
  "systemPromptFiles": ["SOUL.md", "IDENTITY.md", "TOOLS.md", "custom.md"]
}
```

Files are concatenated in the order specified. Paths are relative to `~/.botnexus/agents/<agentId>/`.

**Default Load Order** (if `systemPromptFiles` is empty):

1. `AGENTS.md` — Multi-agent patterns
2. `SOUL.md` — Personality and values
3. `TOOLS.md` — Tool usage guidelines
4. `BOOTSTRAP.md` — Initialization
5. `IDENTITY.md` — Role and expertise
6. `USER.md` — User preferences

### Tool Assignment

Grant tools to an agent:

```json
{
  "toolIds": [
    "read_file",
    "write_file",
    "web_search",
    "web_fetch",
    "grep",
    "glob"
  ]
}
```

Available built-in tools:
- `read_file`, `write_file` — File system operations
- `web_search`, `web_fetch` — Web access
- `grep`, `glob` — Code search
- `bash`, `powershell` — Shell execution

See [Extensions Guide](extensions.md) for custom tools and MCP servers.

### Tool Policy

Control tool approval and access:

```json
{
  "toolPolicy": {
    "alwaysApprove": ["bash", "powershell"],
    "neverApprove": ["read_file", "web_fetch"],
    "denied": ["dangerous_tool"]
  }
}
```

- **`alwaysApprove`**: Tools requiring user approval before execution
- **`neverApprove`**: Trusted tools that skip approval
- **`denied`**: Tools completely blocked (even if in `toolIds`)

### Sub-Agents

Allow an agent to delegate to other agents:

```json
{
  "subAgents": ["specialist", "reviewer", "researcher"]
}
```

The agent can spawn background sub-agents for complex tasks using the `task` tool.

### Isolation Strategy

Control how agents execute:

```json
{
  "isolationStrategy": "in-process",
  "isolationOptions": {
    "workingDirectory": "/workspace",
    "allowNetwork": true
  }
}
```

**Strategies:**
- **`in-process`**: Agent runs in the same process (default, fastest)
- **`sandbox`**: Agent runs in an isolated environment (future)

### Concurrency Limits

Limit concurrent sessions:

```json
{
  "maxConcurrentSessions": 5
}
```

- `0` = unlimited
- `> 0` = max concurrent sessions for this agent

### Memory System

Enable persistent memory across sessions:

```json
{
  "memory": {
    "enabled": true,
    "maxEntries": 100
  }
}
```

Memory allows agents to recall information from previous conversations.

### Soul Sessions

Enable a persistent agent identity:

```json
{
  "soul": {
    "enabled": true,
    "idleTimeoutMinutes": 30
  }
}
```

Soul sessions maintain agent state across interactions with idle timeout.

### Session Access Control

Control what sessions an agent can see:

```json
{
  "sessionAccess": {
    "level": "own",
    "allowedAgents": []
  }
}
```

**Levels:**
- **`own`** (default): Agent sees only its own sessions
- **`allowlist`**: Agent sees sessions from `allowedAgents`
- **`all`**: Agent sees all sessions (admin mode)

**Example (Multi-Agent Coordinator):**
```json
{
  "sessionAccess": {
    "level": "allowlist",
    "allowedAgents": ["assistant", "coder", "reviewer"]
  }
}
```

---

## System Prompt Files

System prompts define agent behavior. BotNexus supports a structured approach:

### File Structure

```
~/.botnexus/agents/<agentId>/
├── SOUL.md          # Personality, values, tone
├── IDENTITY.md      # Role, expertise, boundaries
├── TOOLS.md         # Tool usage guidelines
├── BOOTSTRAP.md     # Initialization instructions
├── AGENTS.md        # Multi-agent coordination
└── USER.md          # User-specific preferences
```

### Example: SOUL.md

```markdown
# Soul: Research Assistant

## Core Values

- **Accuracy**: Cite sources, verify facts, admit uncertainty
- **Clarity**: Use precise language, define technical terms
- **Efficiency**: Summarize when appropriate, avoid verbosity

## Personality

- Professional yet approachable
- Curious and inquisitive
- Patient with complex questions

## Tone

- Direct and informative
- Avoid jargon unless necessary
- Use examples to illustrate concepts
```

### Example: IDENTITY.md

```markdown
# Identity: Research Assistant

## Role

You are a research assistant specializing in academic and technical research. Your expertise includes:

- Literature reviews and source evaluation
- Data analysis and interpretation
- Citation formatting (APA, MLA, Chicago)
- Research methodology guidance

## Capabilities

- Search academic databases and journals
- Summarize research papers
- Identify gaps in existing research
- Recommend research methodologies

## Boundaries

- Do not fabricate sources or citations
- Decline to write entire papers (provide guidance instead)
- Refer to domain experts for specialized topics outside your training
```

### Example: TOOLS.md

```markdown
# Tool Usage Guidelines

## Web Search

Use `web_search` to:
- Find recent information not in training data
- Verify facts and claims
- Discover relevant sources

**When to search:**
- User asks "what's the latest..." or "recent developments in..."
- Technical questions requiring current documentation

## File Operations

Use `read_file` to:
- Review user-provided documents
- Access context for follow-up questions

Use `write_file` to:
- Save research summaries
- Create structured notes
- Generate bibliography files

## Code Search

Use `grep` and `glob` to:
- Find code patterns in repositories
- Locate function definitions
- Search for specific API usage
```

---

## Multi-Agent Setup

### Scenario: Code Review Workflow

Create a multi-agent system for code development:

**1. Developer Agent:**
```json
{
  "developer": {
    "displayName": "Developer",
    "description": "Code generation and implementation",
    "provider": "copilot",
    "model": "claude-opus-4.6",
    "toolIds": ["read_file", "write_file", "grep", "glob", "bash"],
    "subAgents": ["reviewer"],
    "systemPromptFiles": ["SOUL.md", "IDENTITY.md", "coding-standards.md"]
  }
}
```

**2. Reviewer Agent:**
```json
{
  "reviewer": {
    "displayName": "Code Reviewer",
    "description": "Code review and quality assurance",
    "provider": "anthropic",
    "model": "claude-sonnet-4-20250514",
    "toolIds": ["read_file", "grep"],
    "systemPromptFiles": ["SOUL.md", "review-checklist.md"]
  }
}
```

**Workflow:**
1. User asks `developer` to implement a feature
2. `developer` writes code
3. `developer` spawns `reviewer` as a sub-agent
4. `reviewer` analyzes the code and provides feedback
5. `developer` addresses feedback

---

## Agent Templates

BotNexus can auto-bootstrap agent workspaces. When you create a new agent, BotNexus generates template files:

```bash
# Create agent directory
mkdir -p ~/.botnexus/agents/my-agent

# BotNexus auto-generates:
# - SOUL.md
# - IDENTITY.md
# - USER.md
# - HEARTBEAT.md
# - MEMORY.md
```

Edit these templates to customize your agent.

---

## Agent Discovery

List all registered agents:

**REST API:**
```bash
curl http://localhost:5005/api/agents
```

**Response:**
```json
{
  "agents": [
    {
      "agentId": "assistant",
      "displayName": "General Assistant",
      "description": "Multi-purpose AI helper",
      "modelId": "gpt-4.1",
      "provider": "copilot",
      "enabled": true
    },
    {
      "agentId": "coder",
      "displayName": "Coding Agent",
      "description": "Code generation specialist",
      "modelId": "claude-opus-4.6",
      "provider": "copilot",
      "enabled": true
    }
  ]
}
```

---

## Agent Lifecycle

### Registration

Agents are registered automatically when:
1. Defined in `config.json` under `agents`
2. JSON file exists in `~/.botnexus/agents/`
3. Configuration is reloaded (hot reload)

### Enable/Disable

Toggle agents without removing them:

```json
{
  "assistant": {
    "enabled": false
  }
}
```

Disabled agents:
- Do not appear in the WebUI dropdown
- Cannot receive messages
- Retain their configuration for later re-enabling

### Removal

Remove an agent by:
1. Deleting it from `config.json`
2. Removing its JSON file from `~/.botnexus/agents/`
3. Configuration reload applies changes

Session history is preserved in `~/.botnexus/workspace/sessions/`.

---

## Best Practices

### 1. System Prompt Organization

**Structure prompts hierarchically:**
```
SOUL.md        # Personality (rarely changes)
IDENTITY.md    # Role and expertise (occasionally updates)
TOOLS.md       # Tool guidelines (updates with new tools)
custom.md      # Project-specific rules (frequent updates)
```

### 2. Model Selection

**Match model to task:**
- **Complex reasoning**: `claude-opus-4.6`, `gpt-5.4`
- **General purpose**: `gpt-4.1`, `claude-sonnet-4-20250514`
- **Fast/cheap**: `gpt-4o-mini`, `claude-haiku-4.5`

### 3. Tool Access

**Principle of least privilege:**
- Only grant tools the agent needs
- Use `toolPolicy.denied` to block dangerous tools
- Review tool usage in session logs

### 4. Multi-Agent Coordination

**Design clear boundaries:**
- Developer: writes code
- Reviewer: checks code quality
- Researcher: finds information
- Coordinator: orchestrates other agents

### 5. Testing

**Test agents incrementally:**
1. Start with minimal config (no tools)
2. Add system prompt and verify tone
3. Add tools one at a time
4. Test multi-agent delegation last

---

## Advanced Configuration

### Custom Metadata

Add custom metadata for external systems:

```json
{
  "metadata": {
    "owner": "platform-team",
    "cost-center": "engineering",
    "tags": ["production", "customer-facing"],
    "version": "2.1.0"
  }
}
```

### Extension-Specific Config

Each extension reads its config from `extensions`:

```json
{
  "extensions": {
    "botnexus-skills": {
      "enabled": true,
      "autoLoad": ["git-workflow"]
    },
    "botnexus-mcp": {
      "toolPrefix": true,
      "servers": {
        "github": {
          "command": "npx",
          "args": ["-y", "@modelcontextprotocol/server-github"]
        }
      }
    },
    "custom-extension": {
      "customOption": "value"
    }
  }
}
```

See [Extensions Guide](extensions.md) for details.

---

## Troubleshooting

### Agent Not Listed in WebUI

**Check:**
1. Agent `enabled: true` in config
2. Configuration file is valid JSON
3. Gateway logs for registration errors

**Verify:**
```bash
curl http://localhost:5005/api/agents
```

### Agent Not Responding

**Check:**
1. Provider is configured and enabled
2. API key is valid
3. Model ID is correct for the provider

**Debug:**
```bash
curl http://localhost:5005/api/agents/my-agent
```

### System Prompt Not Loading

**Check:**
1. Files exist in `~/.botnexus/agents/<agentId>/`
2. Paths in `systemPromptFiles` are correct (relative to agent directory)
3. Files are readable (permissions)

**Verify:**
```bash
ls -la ~/.botnexus/agents/my-agent/
```

### Tool Not Available

**Check:**
1. Tool ID is in agent's `toolIds` array
2. Tool extension is installed in `~/.botnexus/extensions/`
3. Extension is enabled in config

**List available tools:**
```bash
curl http://localhost:5005/api/tools
```

---

## Next Steps

- **[Configure extensions](extensions.md)** — Add MCP servers, skills, and custom tools
- **[Extend with custom tools](extensions.md#custom-tools)** — Build your own tool extensions
- **[API Reference](../api-reference.md)** — Programmatic agent management

For more advanced scenarios, see:
- [Architecture: Agent Execution](../architecture/overview.md#agent-execution)
- [Workspace & Memory](../architecture/workspace-and-memory.md)
- [Multi-Agent Patterns](../features/sub-agent-spawning.md)
