---
id: feature-extension-contributed-commands
title: "Extension-Contributed Commands (WebUI / TUI)"
type: feature
priority: medium
status: delivered
created: 2026-04-15
updated: 2026-04-16
author: nova
depends_on: []
tags: [extensions, commands, webui, tui, architecture, extensibility]
ddd_types: [Extension, ExtensionManifest]
---

# Extension-Contributed Commands

## Summary

Extensions cannot contribute user-facing commands. The WebUI command palette (`/help`, `/new`, `/status`, `/agents`, `/reset`) is a hardcoded array in `chat.js`. The TUI (CodingAgent) has its own hardcoded slash commands (`/model`, `/clear`, `/session`, `/quit`). There is no mechanism for an extension to register commands that appear in the palette, execute via the backend, and return results to the user.

This means the Skills extension has no `/skills` command, the MCP extension has no `/mcp` command, and any future extension that wants a user-facing command must modify core platform code.

This violates **Principle 8 (Extension-First Architecture)**: *"New capabilities added via plugins, not code changes"* and **Principle 4 (Open/Closed Principle)**: *"Open for extension, closed for modification."*

## Current State

### WebUI Command System

```javascript
// chat.js — entirely client-side, hardcoded
const COMMANDS = [
    { name: '/help',   description: 'Show available commands' },
    { name: '/new',    description: 'Start a new chat session' },
    { name: '/reset',  description: 'Clear chat and reset current session' },
    { name: '/status', description: 'Show gateway health status' },
    { name: '/agents', description: 'List available agents' },
];

function executeCommand(name) {
    switch (name) {
        case '/help':   executeHelp(); break;
        case '/new':    executeReset('new'); break;
        case '/reset':  executeReset(); break;
        case '/status': executeStatus(); break;
        case '/agents': executeAgents(); break;
        default: appendSystemMessage(`Unknown command: ${name}`); break;
    }
}
```

- All commands execute **client-side only** (JS functions)
- No backend command API
- No command discovery
- No way for extensions to register commands
- Command palette appears when user types `/` — nice UX, but static

### TUI Command System

```csharp
// InteractiveLoop.cs — hardcoded switch
if (input.Equals("/quit", ...)) { ... }
if (input.Equals("/login", ...)) { ... }
if (input.Equals("/clear", ...)) { ... }
if (input.Equals("/session", ...)) { ... }
if (input.Equals("/help", ...)) { ... }
if (input.StartsWith("/model ", ...)) { ... }
```

Same problem — hardcoded, no extension point.

### Extension System

Extensions declare capabilities in `botnexus-extension.json`:

```json
{
  "id": "botnexus-skills",
  "extensionTypes": ["tool"]
}
```

Current extension types: `tool` (agent tools), `hook` (lifecycle hooks). No `command` type.

## Proposed Design

### Core Concept: Backend Command Registry

Commands should be **backend-driven, not client-driven**. The Gateway knows what extensions are loaded. Clients (WebUI, TUI, future surfaces) should discover available commands from the Gateway and delegate execution to it.

### Architecture

```
┌─────────────────────────────────────────────────────────┐
│                    Client (WebUI / TUI)                  │
│                                                          │
│  1. GET /api/commands  → get command list for palette    │
│  2. User types /skills list                              │
│  3. POST /api/commands/execute { name, args }            │
│  4. Render result (text, table, structured)              │
└─────────────────────┬───────────────────────────────────┘
                      │
┌─────────────────────▼───────────────────────────────────┐
│                 Gateway (CommandRegistry)                 │
│                                                          │
│  Built-in commands:                                      │
│    /help, /new, /reset, /status, /agents, /reasoning     │
│                                                          │
│  Extension-contributed commands:                         │
│    /skills (from botnexus-skills)                        │
│    /mcp    (from botnexus-mcp)                           │
│                                                          │
│  Discovery:                                              │
│    Extensions implement ICommandContributor              │
│    Registered at startup via extension loader             │
└─────────────────────────────────────────────────────────┘
```

### Interface

```csharp
namespace BotNexus.Gateway.Abstractions.Extensions;

/// <summary>
/// Implemented by extensions that contribute user-facing slash commands.
/// Commands appear in the WebUI palette, TUI, and any future client surface.
/// </summary>
public interface ICommandContributor
{
    /// <summary>
    /// Returns the commands this extension provides.
    /// </summary>
    IReadOnlyList<CommandDefinition> GetCommands();
}

public sealed record CommandDefinition
{
    /// <summary>Command name including slash, e.g. "/skills".</summary>
    public required string Name { get; init; }

    /// <summary>Short description for the command palette.</summary>
    public required string Description { get; init; }

    /// <summary>
    /// Sub-commands, e.g. "/skills list", "/skills show".
    /// Null if the command has no sub-commands.
    /// </summary>
    public IReadOnlyList<SubCommandDefinition>? SubCommands { get; init; }

    /// <summary>
    /// Execute the command. Returns a result to display to the user.
    /// </summary>
    public required Func<CommandExecutionContext, CancellationToken, Task<CommandResult>> ExecuteAsync { get; init; }
}

public sealed record SubCommandDefinition
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public IReadOnlyList<CommandArgument>? Arguments { get; init; }
}

public sealed record CommandArgument
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public bool Required { get; init; }
}

public sealed record CommandExecutionContext
{
    /// <summary>Full command text, e.g. "/skills list" or "/skills show ado-work-management".</summary>
    public required string RawInput { get; init; }

    /// <summary>Parsed sub-command, e.g. "list".</summary>
    public string? SubCommand { get; init; }

    /// <summary>Parsed arguments after the sub-command.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>The agent context (which agent the command was issued in).</summary>
    public string? AgentId { get; init; }

    /// <summary>The session context.</summary>
    public string? SessionId { get; init; }

    /// <summary>BotNexus home directory.</summary>
    public required string HomeDirectory { get; init; }
}

public sealed record CommandResult
{
    /// <summary>Display title for the result block.</summary>
    public required string Title { get; init; }

    /// <summary>Result body (plain text, rendered in a pre block).</summary>
    public required string Body { get; init; }

    /// <summary>Whether this was successful.</summary>
    public bool IsError { get; init; }
}
```

### Extension Manifest

```json
{
  "id": "botnexus-skills",
  "name": "Skills Extension",
  "version": "1.0.0",
  "entryAssembly": "BotNexus.Extensions.Skills.dll",
  "extensionTypes": ["tool", "command"]
}
```

### API Endpoints

```
GET  /api/commands              → list all available commands (for palette)
POST /api/commands/execute      → { "input": "/skills list", "agentId": "nova", "sessionId": "..." }
                                ← { "title": "📚 Skills (12)", "body": "...", "isError": false }
```

### WebUI Changes

Replace the hardcoded `COMMANDS` array with a dynamic fetch:

```javascript
// On connect / startup:
const commands = await api.getCommands();  // GET /api/commands

// Command palette uses dynamic list:
export function showCommandPalette(filter) {
    const query = filter.toLowerCase();
    const matches = commands.filter(c => c.name.startsWith(query));
    // ... same UI logic
}

// Execute routes to backend:
async function executeCommand(name) {
    hideCommandPalette();
    // Built-in client-side commands (that need no backend):
    if (name === '/reset') { executeReset(); return; }  // DOM + reconnect

    // Everything else goes to backend:
    const result = await api.executeCommand(input, agentId, sessionId);
    appendCommandResult(result.title, result.body);
}
```

Most commands execute via the backend. `/reset` may remain client-side (it clears the DOM and reconnects the agent handle). `/new` is a **backend command** -- it seals the current session and creates a fresh one, which is a session lifecycle operation that must go through the Gateway.

### Skills Extension `/skills` Command -- Full Spec
The Skills extension is the first consumer of the command system and serves as the reference implementation.
#### Sub-Commands
| Sub-Command | Args | Description |
|-------------|------|-------------|
| `list` | -- | Show all discovered skills grouped by status (loaded, available, denied) |
| `info <name>` | skill name | Show skill metadata, description, source path, and size |
| `add <name>` | skill name | Load a skill into the current agent session |
| `remove <name>` | skill name | Unload a skill from the current session |
| `reload` | -- | Re-discover skills from disk |
#### `/skills list` Output
```
Skills for nova
  Loaded (3):
    ado-work-management         Unified ADO work management...
    m365-communication          Microsoft 365 communication...
    reference-bank              Shared reference data...
  Available (8):
    calendar-interaction        Calendar management...
    datetime-helper             Date/time utilities...
    engineering-planning-expert  Engineering planning guidance...
    personal-knowledge-mgmt     PKM using PARA...
    powerpoint-integration      PowerPoint deck generation...
    s360-assistant              S360 compliance...
    skill-creator               Guide for creating skills...
    ai-artifact-best-practices  Maintain high-quality instructions...
  Denied (1):
    memory-bank                 Disabled by agent config
  Config: max 20 loaded, ~25K token budget, ~10.5K used
```
The list must be **agent-aware** -- uses the current agent's SkillsConfig and the SkillTool's _sessionLoaded set.
#### `/skills info <name>` Output
```
Skill: ado-work-management
  Name:         ado-work-management
  Description:  Unified ADO work management...
  Source:       Global (~/.botnexus/skills/ado-work-management/)
  Status:       Loaded (auto-load)
  Size:         ~3,200 tokens
  License:      --
  Allowed Tools: ado_get_work_item, ado_update_work_item
  Files:        SKILL.md, reference/features.md, workflows/
```
#### `/skills add <name>`
Loads a skill into the current session. Equivalent to agent calling skills tool load action but user-initiated.
- Validates: skill exists, not denied, not already loaded, within budget
- On success: adds to SkillTool._sessionLoaded, content available on next turn
- Returns confirmation with skill summary
#### `/skills remove <name>`
Unloads a skill from the current session.
- Removes from SkillTool._sessionLoaded
- Skill content no longer injected into system prompt on next turn
- Note: does NOT remove content from current conversation history (already sent to LLM)
- Returns confirmation
This is a **new capability** -- the existing SkillTool has no unload action. The command contributor needs a reference to the active SkillTool instance for the session.
#### `/skills reload`
Re-runs SkillDiscovery.Discover() to pick up changes from disk. Returns a diff summary.
#### Implementation: Session-Aware Commands
The /skills command needs access to the **live SkillTool instance** for the current agent session. CommandExecutionContext needs a tool resolver:
```csharp
public sealed record CommandExecutionContext
{
    // ... existing fields ...
    public Func<string, IAgentTool?>? ResolveSessionTool { get; init; }
}
```
#### SkillTool Changes Required
1. Add TryUnload(string name) method for /skills remove
2. Expose DiscoveryPaths property for the command contributor
3. Expose Config property for resolution
```csharp
public bool TryUnload(string skillName)
    => _sessionLoaded.TryRemove(skillName, out _);
public (string? Global, string? Agent, string? Workspace) DiscoveryPaths
    => (globalSkillsDir, agentSkillsDir, workspaceSkillsDir);
public SkillsConfig? Config => config;
```

## Implementation Plan

### Phase 1: Backend Command Registry

1. Add `ICommandContributor`, `CommandDefinition`, `CommandResult`, etc. to `Gateway.Contracts`
2. Create `CommandRegistry` in `BotNexus.Gateway` — collects built-in + extension commands
3. Add `CommandsController` with `GET /api/commands` and `POST /api/commands/execute`
4. Move existing built-in commands (`/status`, `/agents`, `/help`) to backend implementations
5. Register built-in commands + discover extension commands via extension loader

**Files:**
- New: `src/gateway/BotNexus.Gateway.Contracts/Extensions/ICommandContributor.cs`
- New: `src/gateway/BotNexus.Gateway.Contracts/Extensions/CommandModels.cs`
- New: `src/gateway/BotNexus.Gateway/Commands/CommandRegistry.cs`
- New: `src/gateway/BotNexus.Gateway/Commands/BuiltInCommands.cs`
- New: `src/gateway/BotNexus.Gateway.Api/Controllers/CommandsController.cs`
- Modified: `GatewayServiceCollectionExtensions.cs`

### Phase 2: WebUI Integration

1. Replace hardcoded `COMMANDS` with `GET /api/commands` on startup
2. Route command execution to `POST /api/commands/execute`
3. Keep `/reset` as client-side (needs DOM/reconnect). `/new` routes to backend (seals session + creates new one)
4. Support sub-command palette (typing `/skills ` shows `list`, `show`, `validate`)

**Files:**
- Modified: `src/BotNexus.WebUI/wwwroot/js/chat.js`
- Modified: `src/BotNexus.WebUI/wwwroot/js/api.js`

### Phase 3: Skills Extension /skills Command

1. Implement `SkillsCommandContributor : ICommandContributor`
2. Add `"command"` to skills `botnexus-extension.json`
3. Sub-commands: `list`, `info <name>`, `add <name>`, `remove <name>`, `reload`
4. Add `TryUnload()`, `DiscoveryPaths`, and `Config` accessors to `SkillTool`
5. Wire `ResolveSessionTool` in `CommandExecutionContext` to resolve the live `SkillTool` instance

**Files:**
- New: `extensions/skills/BotNexus.Extensions.Skills/SkillsCommandContributor.cs`
- Modified: `extensions/skills/BotNexus.Extensions.Skills/SkillTool.cs` (expose session state)
- Modified: `extensions/skills/BotNexus.Extensions.Skills/botnexus-extension.json`

### Phase 4: TUI Integration (Future)

The TUI (`InteractiveLoop.cs`) can use the same `CommandRegistry` to resolve commands, replacing its hardcoded switch. Same commands, same execution, different rendering.

### Phase 5: Hub Integration (Optional)

Expose commands via SignalR hub method for real-time palette updates:
- `GetCommands()` → returns command list
- `ExecuteCommand(input, agentId, sessionId)` → returns result

This avoids REST round-trips for the WebUI and enables push-based command updates if extensions are loaded at runtime.

## Architectural Alignment

| Principle | Alignment |
|-----------|-----------|
| **P4: OCP** | New commands via `ICommandContributor` — no core modification needed |
| **P8: Extension-First** | Extensions bring their own commands to the user surface |
| **P6: ISP** | `ICommandContributor` is small — just returns definitions + execute function |
| **P3: SRP** | `CommandRegistry` handles command discovery/dispatch. Controllers handle HTTP. Extensions handle logic. |
| **P15: Fail Gracefully** | If an extension's command fails, return error result — don't crash |

## Considerations

1. **Client-side vs. backend commands** — `/reset` needs client-side DOM/connection control and is flagged `clientSide: true`. `/new` is a backend command that seals the current session and creates a fresh one — the WebUI switches to the new session after the backend responds.

2. **Sub-command palette UX** — When user types `/skills `, the palette should show sub-commands. This requires the `SubCommands` array from `CommandDefinition`. The WebUI filters on the space-separated prefix.

3. **Agent context** — Commands like `/skills list` should be agent-aware (show skills for the current agent). The `CommandExecutionContext` includes `agentId` and `sessionId`.

4. **Command name collisions** — Two extensions register `/foo`. First-registered wins; log a warning for duplicates. Same approach as tool name collisions.

5. **Streaming results** — For now, commands return a single `CommandResult`. Future: support streaming results for long-running commands (e.g., `/skills validate` checking many skills).

## Success Criteria

- [ ] `ICommandContributor` interface in Gateway.Contracts
- [ ] `GET /api/commands` returns built-in + extension commands
- [ ] `POST /api/commands/execute` dispatches to the correct handler
- [ ] WebUI command palette populated from API (not hardcoded)
- [ ] `/skills list` shows loaded/available/denied skills with budget info
- [ ] `/skills info <name>` shows skill metadata, source, size, files
- [ ] `/skills add <name>` loads a skill into the current session
- [ ] `/skills remove <name>` unloads a skill from the current session
- [ ] `/skills reload` re-discovers skills from disk
- [ ] Extension command failures return error result, don't crash
- [ ] Built-in commands (`/new`, `/reset`, `/status`) still work
- [ ] Sub-command palette works (typing `/skills ` shows sub-commands)
