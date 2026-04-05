# CodingAgent Alignment Audit

**Auditor:** Bender (Runtime Dev)
**Date:** 2025-07-18
**Scope:** `src/coding-agent/BotNexus.CodingAgent/` vs `@mariozechner/pi-coding-agent` (badlogic/pi-mono)

## Summary

**5/10 areas aligned, 2 partial, 3 gaps found.**

BotNexus has solid coverage of the core coding-agent loop — tools, config, sessions, system prompt, and CLI all exist and function. The major gaps are in the extension API richness (pi-mono has 100+ event types, custom commands, provider registration, UI widgets vs our DLL-based tool-only extensions), the lack of an RPC/JSON streaming mode, and missing session branching/compaction. The built-in tools are functionally equivalent with minor schema differences.

---

## Detailed Findings

### 1. Built-in Tools — ⚠️ Partial

**pi-mono (7 tools):**
- `read` — Read file (text + image support with auto-resize, offset/limit pagination, syntax highlighting)
- `write` — Write file (auto-create dirs, abort support, file mutation queue for serialization)
- `edit` — Multi-edit in one call (`edits[]` array), fuzzy matching fallback (smart-quote normalization, whitespace), overlap detection, BOM handling, line-ending preservation
- `bash` — Shell execution with streaming output, rolling buffer, temp-file for large output, process-tree kill, abort signal, pluggable `BashOperations` for remote exec (e.g. SSH)
- `find` — Glob via `fd` binary (auto-downloads), .gitignore respect, hidden files, 1000-result limit
- `grep` — Regex/literal via `ripgrep` binary (auto-downloads), context lines, JSON output parsing, 100-match limit, 500-char line truncation
- `ls` — Directory listing with type indicators, hidden files, 500-entry limit

**BotNexus (5 tools):**
- `ReadTool` — Read file with line numbers or list directory (depth ≤ 2). No image support, no offset/limit pagination.
- `WriteTool` — Full-file write, auto-create dirs. No mutation queue, no abort support.
- `EditTool` — Single `old_str`→`new_str` replacement per call. Strict exact match only (no fuzzy). Line-ending preservation. Returns ±120 char context snippet.
- `ShellTool` — Platform-aware (PowerShell on Windows, bash on Unix). 120s default timeout. Captures stdout/stderr separately. 10,000-char output cap.
- `GlobTool` — Built-in .NET glob + .gitignore filtering via `git check-ignore`. Returns sorted relative paths.

**Gaps:**
| Feature | pi-mono | BotNexus | Status |
|---------|---------|----------|--------|
| `grep` tool | ✅ ripgrep-backed | ❌ Missing | **Gap** |
| `ls` tool | ✅ Dedicated | ⚠️ ReadTool handles dirs | Partial |
| Image reading | ✅ jpg/png/gif/webp with resize | ❌ Text only | **Gap** |
| Multi-edit per call | ✅ `edits[]` array | ❌ Single replacement | **Gap** |
| Fuzzy edit matching | ✅ Normalized whitespace/quotes | ❌ Exact only | **Gap** |
| Edit overlap detection | ✅ Validates non-overlapping | ❌ N/A (single edit) | **Gap** |
| Streaming shell output | ✅ Rolling buffer + partial updates | ❌ Waits for completion | **Gap** |
| Shell output to temp file | ✅ Auto-saves when >50KB | ❌ Truncates at 10K chars | **Gap** |
| File mutation queue | ✅ Serializes per-file writes | ❌ No serialization | Gap |
| Pluggable operations | ✅ All tools support remote backends | ❌ Local only | Gap |
| Tool truncation limits | ✅ Configurable (2000 lines/50KB) | ⚠️ Fixed (2000 lines/10K chars) | Partial |
| `fd`/`rg` binary management | ✅ Auto-downloads | ❌ Uses .NET built-in | N/A (design choice) |

**Action:**
- P1: Add a `GrepTool` (search file contents by pattern). This is the most impactful missing tool.
- P1: Support multi-edit per call in `EditTool` (accept `edits[]` array).
- P2: Add fuzzy matching fallback to `EditTool` (smart-quote, whitespace normalization).
- P2: Increase shell output cap and consider streaming or temp-file for large output.
- P3: Add image file reading support to `ReadTool`.

---

### 2. Agent Factory — ✅ Aligned

**pi-mono (`main.ts`):**
1. Parse CLI args → resolve session (create/resume/fork/no-session)
2. Create cwd-bound services (settings, model registry, resource loader, extension runner)
3. Build session options (model, thinking level, tools from CLI/config)
4. Create `AgentSessionRuntime` wrapping session + services
5. Resolve app mode (interactive/print/json/rpc)
6. Prepare initial message from args/stdin/files
7. Dispatch to mode handler

**BotNexus (`CodingAgent.CreateAsync` + `Program.Main`):**
1. Parse CLI args via `CommandParser`
2. Load config, extensions, skills
3. Create/resume session via `SessionManager`
4. `CodingAgent.CreateAsync()`:
   - Validates config & directory
   - Creates 5 built-in tools + extension tools
   - Gathers git context (branch, status, package manager)
   - Builds system prompt via `SystemPromptBuilder`
   - Resolves model from registry or creates new
   - Wires hooks (SafetyHooks + AuditHooks)
   - Returns configured `Agent` with sequential tool execution
5. Run interactive or single-shot mode

**Gap:** Flow is structurally equivalent. pi-mono has more granular service injection (ModelRegistry, ResourceLoader, ExtensionRunner as separate services). BotNexus wires everything inside the static factory. Both produce a configured agent ready to run.

**Action:** No immediate action needed. Consider extracting services into a DI container if the factory grows.

---

### 3. System Prompt — ⚠️ Partial

**pi-mono (`system-prompt.ts`):**
- Dynamically assembles from: tool snippets, guidelines (adaptive based on which tools are enabled), documentation links (pi docs, extensions, themes, skills), project context files, skills (XML-formatted `<available_skills>`), working directory + date
- Supports `customPrompt` (full override) and `appendSystemPrompt` (additive)
- Guidelines adjust to available tools (e.g., "Prefer grep/find/ls over bash" only when grep/find are enabled)
- Includes pi self-documentation references

**BotNexus (`SystemPromptBuilder`):**
- Static markdown template with sections: Environment, Tool Guidelines, Skills, Custom Instructions
- Includes: OS description, working directory, git branch/status, package manager, tool names
- Skills injected as raw markdown content
- Custom instructions from config appended
- No tool-adaptive guidelines, no context files, no documentation links

**Gap:**
| Feature | pi-mono | BotNexus | Status |
|---------|---------|----------|--------|
| Tool-adaptive guidelines | ✅ Adjusts to enabled tools | ❌ Static guidelines | **Gap** |
| Context files | ✅ Loaded from project | ❌ None | **Gap** |
| Custom prompt override | ✅ `customPrompt` | ❌ Only append | **Gap** |
| Skills formatting | ✅ XML `<available_skills>` | ⚠️ Raw markdown | Partial |
| Documentation references | ✅ Self-doc links | ❌ None | Gap |
| Date injection | ✅ Current date | ❌ None | Gap |

**Action:**
- P2: Add context-file loading (`.botnexus-agent/context/*.md` or similar).
- P2: Add `customPrompt` override option to config.
- P3: Format skills as structured XML instead of raw markdown.
- P3: Add current date to system prompt.

---

### 4. Configuration — ✅ Aligned

**pi-mono (`config.ts`):**
- Base directory: `~/.pi/agent/` (or `$PI_CODING_AGENT_DIR`)
- Subdirectories: `models.json`, `auth.json`, `settings.json`, `tools/`, `bin/`, `prompts/`, `skills/`, `sessions/`, themes
- Runtime detection (Bun binary vs Node.js vs tsx) for package asset resolution
- Environment variables: `PI_PACKAGE_DIR`, `PI_OFFLINE`, `PI_SKIP_VERSION_CHECK`

**BotNexus (`CodingAgentConfig`):**
- Base directory: `.botnexus-agent/` (project-local)
- Global override: `~/.botnexus/coding-agent.json`
- Local override: `.botnexus-agent/config.json`
- Subdirectories: `sessions/`, `extensions/`, `skills/`
- Properties: Model, Provider, ApiKey, MaxToolIterations, MaxContextTokens, AllowedCommands, BlockedPaths, Custom dict
- `Load()` merges: defaults → global → local

**Gap:** Both have layered config resolution. pi-mono separates auth/models/settings into separate files; BotNexus uses a single config document. pi-mono has more environment variable support. BotNexus has `AllowedCommands` and `BlockedPaths` safety config that pi-mono lacks (safety is handled differently in pi-mono via the bash spawn hook).

**Action:** No blockers. Config structures serve their respective designs well.

---

### 5. Session Management — ⚠️ Partial

**pi-mono (`session-manager.ts` + `agent-session-runtime.ts`):**
- JSONL format with versioned headers (V1→V2→V3 migrations)
- Entry types: message, thinking_level_change, model_change, compaction, branch_summary, custom, custom_message, label, session_info
- **Branching:** Tree structure via `parentId` chains. `fork()` creates branch from any entry.
- **Compaction:** Summarizes old messages when context exceeds threshold. Records `CompactionEntry` with token counts.
- **Runtime switching:** `switchSession()`, `newSession()`, `fork()`, `importFromJsonl()`
- Extension lifecycle events on session operations
- Session tree reconstruction via `getTree()`, `buildSessionContext()`

**BotNexus (`SessionManager`):**
- JSONL messages + separate `session.json` metadata
- Storage: `.botnexus-agent/sessions/{id}/session.json` + `messages.jsonl`
- CRUD: create, save, resume, list, delete
- Message serialization via `MessageEnvelope` with type discriminator
- Session ID: `yyyyMMdd-HHmmss-{hex}`

**Gap:**
| Feature | pi-mono | BotNexus | Status |
|---------|---------|----------|--------|
| Session branching/forking | ✅ Tree structure + fork from entry | ❌ Linear only | **Gap** |
| Compaction | ✅ Auto-compact on token overflow | ❌ None | **Gap** |
| Version migration | ✅ V1→V2→V3 schema evolution | ❌ No versioning | Gap |
| Entry types | ✅ 10+ types (model change, label, etc.) | ⚠️ 4 message types | Partial |
| Runtime session switching | ✅ Switch/new/fork at runtime | ❌ Single session per run | **Gap** |
| Extension events on session ops | ✅ before_switch, before_fork, shutdown | ❌ None | Gap |

**Action:**
- P1: Add compaction (summarize old messages when context grows too large). This is critical for long sessions.
- P2: Add session branching/forking.
- P2: Add version header to session format for future migrations.
- P3: Add session switching at runtime.

---

### 6. Hooks — ✅ Aligned

**pi-mono:**
- Extension event system with 100+ event types
- Key hooks: `BashSpawnHook` (modify command/cwd/env before execution), tool-specific events (`BashToolCallEvent`, `ReadToolCallEvent`, etc.), session lifecycle events
- Hooks are registered via `api.on(event, handler)` — extensible, event-driven
- No built-in "safety hooks" — safety is handled by bash spawn hook and extension events

**BotNexus:**
- `SafetyHooks.ValidateAsync()` — before-tool-call validation:
  - Path blocking (write/edit tools check `BlockedPaths`)
  - Large write warnings (>1MB)
  - Shell command allowlisting (`AllowedCommands`) and dangerous-pattern blocking
- `AuditHooks.AuditAsync()` — after-tool-call logging:
  - Tool call counting, duration tracking, verbose audit output

**Gap:** Different paradigms. pi-mono uses an event-driven extension system where any extension can hook any event. BotNexus uses purpose-built safety + audit hooks wired directly in the factory. BotNexus's approach is simpler but less extensible. However, BotNexus has **stronger built-in safety** (path blocking, command allowlisting, dangerous pattern detection) that pi-mono delegates entirely to extensions.

**Action:** The current hook system is functionally sound and arguably safer by default. When the extension system is enriched (see §8), hooks should migrate to the event-driven model. No immediate action.

---

### 7. CLI — ⚠️ Partial

**pi-mono (`cli.ts` + `cli/args.ts` + modes):**
- **Modes:** interactive (full TUI), print (single-shot text), json (event stream JSONL), rpc (JSON-RPC 2.0 on stdin/stdout)
- **Args:** 30+ flags including `--model`, `--provider`, `--thinking`, `--tools`, `--extensions`, `--skills`, `--system-prompt`, `--append-system-prompt`, `--session`, `--resume`, `--continue`, `--fork`, `--no-session`, `--export`, `--verbose`, `--offline`, `--list-models`
- **Extension flags:** Unknown `--flag-name` args passed to extensions via `unknownFlags`
- **File args:** `@filename` syntax to load content from files
- **Model cycling:** `--models pattern1,pattern2` for multi-model rotation
- **Package manager CLI:** Install/remove/update extensions, list, config TUI

**BotNexus (`CommandParser` + `InteractiveLoop`):**
- **Modes:** interactive (readline loop), non-interactive (single prompt)
- **Args:** `--model`, `--provider`, `--resume`, `--non-interactive`, `--verbose`, `--help`
- **Interactive commands:** `/quit`, `/exit`, `/clear`, `/session`, `/help`, `/model <name>`
- **Output formatting:** Colored console output (welcome, tool start/end, errors, separators)

**Gap:**
| Feature | pi-mono | BotNexus | Status |
|---------|---------|----------|--------|
| RPC mode (JSON-RPC 2.0) | ✅ Full protocol | ❌ Missing | **Gap** |
| JSON event streaming | ✅ JSONL output mode | ❌ Missing | **Gap** |
| `@file` args | ✅ Load content from files | ❌ Missing | Gap |
| Thinking level control | ✅ `--thinking` flag + levels | ❌ Not exposed | Gap |
| Extension flags passthrough | ✅ Unknown flags → extensions | ❌ No extension flags | Gap |
| `--system-prompt` override | ✅ CLI flag | ❌ Config only | Gap |
| `--export` session to HTML | ✅ Built-in | ❌ Missing | Gap |
| Model cycling | ✅ `--models` | ❌ Single model | Gap |
| Session --continue/--fork | ✅ Multiple resume modes | ❌ `--resume <id>` only | Partial |
| Full TUI (ink/react) | ✅ Rich terminal UI | ⚠️ Console.ReadLine loop | Design choice |
| Package manager commands | ✅ Install/remove extensions | ❌ Manual DLL placement | Gap |

**Action:**
- P1: Add JSON streaming mode (`--json`) for programmatic integration. This unblocks VS Code extension and CI usage.
- P1: Add RPC mode or equivalent for IDE integration.
- P2: Add `@file` argument support.
- P2: Add `--system-prompt` and `--append-system-prompt` CLI flags.
- P3: Add `--thinking` flag when thinking-level models are supported.
- P3: Add `--export` for session export.

---

### 8. Extensions — ❌ Major Gap

**pi-mono (full extension framework):**
- **Discovery:** Project-local (`.pi/extensions/`) → global (`~/.pi/agent/extensions/`) → explicit paths
- **Loading:** jiti-based TypeScript/JavaScript JIT loading (no compilation needed)
- **Factory pattern:** `export default async (api: ExtensionAPI) => { ... }`
- **Registration API:** `registerTool()`, `registerCommand()`, `registerShortcut()`, `registerFlag()`, `registerMessageRenderer()`, `registerProvider()`
- **Action API:** `sendMessage()`, `sendUserMessage()`, `appendEntry()`, `exec()`, `setModel()`, `setThinkingLevel()`, `setActiveTools()`
- **Events:** 100+ event types (session, agent, tool, resource, UI lifecycle)
- **UI API:** Dialogs, status, widgets, editor, theme, terminal input, custom overlays
- **Provider registration:** Extensions can add custom LLM providers
- **Virtual modules:** Bundled packages available without filesystem (for Bun binary)

**BotNexus (minimal extension system):**
- **Discovery:** `.botnexus-agent/extensions/*.dll`
- **Loading:** .NET assembly reflection (`AssemblyLoadContext.LoadFromAssemblyPath`)
- **Interface:** `IExtension { Name, GetTools() }` — returns `IReadOnlyList<IAgentTool>`
- **Capability:** Extensions can only provide additional tools. No commands, no events, no UI, no provider registration.

**Gap:**
| Capability | pi-mono | BotNexus | Status |
|------------|---------|----------|--------|
| Custom tools | ✅ | ✅ | Aligned |
| Custom commands | ✅ `/command` registration | ❌ | **Gap** |
| Event subscriptions | ✅ 100+ events | ❌ | **Gap** |
| Provider registration | ✅ Custom LLM providers | ❌ | **Gap** |
| Flag registration | ✅ CLI flag passthrough | ❌ | **Gap** |
| UI widgets | ✅ Full UI API | ❌ | **Gap** |
| Message rendering | ✅ Custom renderers | ❌ | **Gap** |
| Session entry injection | ✅ `appendEntry()` | ❌ | **Gap** |
| Dynamic tool activation | ✅ `setActiveTools()` | ❌ | **Gap** |
| TS/JS loading (no compile) | ✅ jiti-based | ❌ Requires DLL | Design |
| Lifecycle events | ✅ Before/after every op | ❌ | **Gap** |

**Action:**
- P1: Expand `IExtension` interface to support event hooks (before/after tool call, session start/end).
- P1: Add command registration for extensions (enables `/command` in interactive mode).
- P2: Add provider registration so extensions can bring custom LLM backends.
- P2: Design an extension event bus. Doesn't need 100+ events day one — start with: `session_start`, `session_end`, `tool_call`, `tool_result`, `message_start`, `message_end`.
- P3: Consider a scripting-based extension format (C# scripting or Roslyn) to avoid DLL compilation requirement.

---

### 9. Skills — ✅ Aligned

**pi-mono (`skills.ts`):**
- **Discovery:** Global (`~/.pi/agent/skills/`) → project-local (`.pi/skills/`) → explicit paths
- **Format:** `SKILL.md` with YAML frontmatter (`name`, `description`, `disable-model-invocation`)
- **Validation:** Name (lowercase, hyphens, ≤64 chars), description (required, ≤1024 chars)
- **Prompt formatting:** XML `<available_skills>` with name/description/location
- **Ignore rules:** Respects `.gitignore`, `.ignore`, `.fdignore`
- **Disable flag:** `disable-model-invocation` hides skill from prompt (command-only)

**BotNexus (`SkillsLoader`):**
- **Discovery:** `{workingDir}/AGENTS.md` → `{workingDir}/.botnexus-agent/AGENTS.md` → `{workingDir}/.botnexus-agent/skills/*.md`
- **Format:** Plain markdown files (no frontmatter parsing)
- **Prompt formatting:** Raw markdown content injected into system prompt
- **No validation:** Files read as-is

**Gap:**
| Feature | pi-mono | BotNexus | Status |
|---------|---------|----------|--------|
| Multi-source loading | ✅ Global + local + explicit | ⚠️ Local only | Partial |
| YAML frontmatter | ✅ Structured metadata | ❌ Plain markdown | Gap |
| Name/description validation | ✅ Strict rules | ❌ None | Gap |
| XML formatting for prompt | ✅ Structured | ❌ Raw markdown | Gap |
| `disable-model-invocation` | ✅ Command-only skills | ❌ All skills in prompt | Gap |
| Ignore rules | ✅ .gitignore etc. | ❌ None | Gap |

**Action:**
- P2: Add global skill loading (`~/.botnexus/skills/`).
- P3: Add YAML frontmatter parsing for skill metadata.
- P3: Format skills as structured XML in system prompt.

---

### 10. Missing Features — ❌ Gaps

Major capabilities present in pi-coding-agent with no equivalent in BotNexus CodingAgent:

| # | Feature | pi-mono Implementation | Impact |
|---|---------|----------------------|--------|
| 1 | **Session compaction** | Auto-summarize old messages when context grows; `CompactionEntry` with token tracking | **High** — without this, long sessions hit token limits |
| 2 | **RPC mode** | JSON-RPC 2.0 on stdin/stdout for IDE integration | **High** — needed for VS Code extension |
| 3 | **JSON streaming mode** | JSONL event stream for programmatic consumers | **High** — needed for CI/CD and tooling |
| 4 | **Session branching** | Fork from any entry, tree structure with parentId | **Medium** — enables exploratory workflows |
| 5 | **Thinking level control** | `--thinking off|minimal|low|medium|high|xhigh` | **Medium** — cost/quality tradeoff |
| 6 | **Model cycling** | `--models pattern1,pattern2` for fallback/rotation | **Low** — reliability feature |
| 7 | **Session export** | `--export session.html` for sharing | **Low** — nice-to-have |
| 8 | **Clipboard/image support** | Paste images from clipboard into prompts | **Low** — interactive UX |
| 9 | **Extension package manager** | Install/remove/update extensions via CLI | **Medium** — developer experience |
| 10 | **Provider registration** | Extensions can add custom LLM backends | **Medium** — extensibility |
| 11 | **Grep tool** | Dedicated ripgrep-backed content search | **High** — most-used tool for code exploration |
| 12 | **Context files** | Auto-load `.pi/context/*.md` into system prompt | **Medium** — project customization |

### Priority Ranking (recommended order):

**P0 — Critical for production use:**
1. Session compaction (prevents token limit crashes)
2. Grep tool (fundamental code exploration capability)

**P1 — Required for integration:**
3. JSON streaming mode (CI/CD, programmatic use)
4. RPC mode (IDE integration)
5. Multi-edit support in EditTool

**P2 — Important for parity:**
6. Extension event system (basic lifecycle hooks)
7. Context file loading
8. Fuzzy edit matching
9. Global skill loading

**P3 — Nice to have:**
10. Session branching
11. Thinking level control
12. Extension package manager
13. Session export

---

## Architecture Comparison

```
pi-mono                                    BotNexus
──────────────────────                     ──────────────────────
cli.ts → main.ts                           Program.cs → CodingAgent.CreateAsync()
  ├── cli/args.ts (30+ flags)               ├── Cli/CommandParser.cs (6 flags)
  ├── config.ts (multi-file config)          ├── CodingAgentConfig.cs (single JSON)
  ├── core/                                  ├── Tools/ (5 built-in)
  │   ├── tools/ (7 tools)                   ├── Session/ (linear JSONL)
  │   ├── session-manager.ts (tree)          ├── Hooks/ (safety + audit)
  │   ├── agent-session.ts (runtime)         ├── Extensions/ (DLL loading)
  │   ├── extensions/ (rich API)             ├── SystemPromptBuilder.cs
  │   ├── skills.ts (frontmatter)            └── Cli/InteractiveLoop.cs
  │   └── system-prompt.ts (dynamic)
  ├── modes/
  │   ├── interactive/ (TUI)
  │   ├── print-mode.ts
  │   └── rpc/
  └── utils/ (git, shell, image, etc.)
```

## Conclusion

BotNexus CodingAgent has a solid foundation that covers the core agent loop — tool execution, safety hooks, sessions, and interactive CLI all work. The critical gaps are (1) no session compaction for long-running sessions, (2) no grep tool for code search, and (3) no programmatic output modes (JSON/RPC) for integration. The extension system is the largest architectural gap — pi-mono's event-driven extension framework is a different league from our DLL-based tool-provider model. However, the BotNexus safety hooks (path blocking, command allowlisting) are actually *stronger* by default than pi-mono's approach.

Recommended next step: address P0 items (compaction + grep tool), then P1 (JSON mode + multi-edit), then gradually enrich the extension API.
