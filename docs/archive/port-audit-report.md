# Port Audit Report: pi-mono coding-agent (TypeScript) → BotNexus CodingAgent (C#)

**Generated:** Comprehensive file-by-file comparison  
**Source:** `Q:\repos\pi-mono\packages\coding-agent\src\` (v0.65.0)  
**Target:** `Q:\repos\botnexus\src\coding-agent\BotNexus.CodingAgent\`

---

## Executive Summary

The C# port covers the **core coding-agent workflow** — tools, sessions, extensions, auth, CLI — but is a **deliberate simplification** of the full TypeScript codebase. The TS source has ~80+ source files across 8 subdirectories; the C# port has ~25 source files across 6 subdirectories. Critical tool logic (read, write, edit, grep, glob, shell) is ported with high fidelity, but there are several behavioral divergences that will affect real-world use. The largest gaps are in **modes** (no RPC/print mode), **CLI surface area** (8 flags vs 30+), **session format** (v2 vs v3), and **TUI components** (none ported).

### Coverage Scorecard

| Area | TS Files | C# Files | Coverage |
|------|----------|----------|----------|
| Entry point / main loop | 3 | 2 | ✅ Core ported |
| CLI parsing | 6 | 3 | ⚠️ Minimal flags |
| Config / settings | 3 | 1 | ⚠️ No SettingsManager |
| Tools (7 total) | 12 | 8 | ✅ All 7 ported |
| System prompt | 1 | 1 | ⚠️ Divergent sections |
| Session management | 1 | 4 | ✅ Ported (v2 vs v3) |
| Compaction | 3 | 1 | ✅ Core ported |
| Extensions | 4 | 4 | ✅ Adapted to DLL model |
| Auth | 1 | 1 | ⚠️ Simplified (OAuth only) |
| Skills | 1 | 1 | ✅ Ported |
| Hooks | 0 (in runner) | 2 | ✅ Enhanced |
| Modes (interactive/print/RPC) | 6+ | 1 | ❌ Only interactive |
| TUI components | 30+ | 0 | ❌ Not ported |
| Utils (git, path, pkg, etc.) | 15 | 4 | ⚠️ Core only |
| Migrations | 1 | 0 | ❌ Not ported |
| HTML export | 6 | 0 | ❌ Not ported |
| Package manager CLI | 2 | 1 | ⚠️ Detection only |
| Bun support | 2 | 0 | N/A (platform) |
| **Tests** | **52 files** | **22 files** | ⚠️ Partial |

---

## Detailed Findings

### Legend
- 🔴 **CRITICAL** — Behavioral bug or missing feature that will cause failures
- 🟡 **MAJOR** — Significant gap that reduces capability
- 🟢 **MINOR** — Style difference or low-impact divergence

---

### 1. TOOLS — Critical Implementation Comparison

| # | Category | Severity | Area | Description | TS Reference | C# Reference | Recommended Fix |
|---|----------|----------|------|-------------|--------------|--------------|-----------------|
| T1 | **FIXED** | ~~🔴 CRITICAL~~ | Shell Tool | **Output truncation direction is reversed.** TS truncates tail (keeps last/most-recent lines); C# truncates head (keeps first lines). For build/test output, the end is most important. **✅ Fixed in Phase 5 — C# now keeps tail.** | `bash.ts` truncate logic | `ShellTool.cs:~L170` | ~~Change C# to truncate tail, keeping last N lines~~ Done |
| T2 | **FIXED** | ~~🔴 CRITICAL~~ | Shell Tool | **Default timeout differs.** TS has no default timeout (runs until complete). C# defaults to 120 seconds. Long builds/tests will be killed unexpectedly. **✅ Fixed in Phase 5 — configurable via `DefaultShellTimeoutSeconds`, default 600s.** | `bash.ts` schema (no default) | `ShellTool.cs:DefaultTimeoutSeconds=120` | ~~Remove default timeout or set very high (e.g., 600s)~~ Done |
| T3 | **FIXED** | ~~🟡 MAJOR~~ | List Directory | **Recursion depth differs.** TS lists entries up to depth 2 (shows subdirectory contents). C# lists top-level only (`SearchOption.TopDirectoryOnly`). Agent gets less context about project structure. **✅ Fixed in Phase 5 — now lists 2 levels deep.** | `ls.ts` recursive with depth check | `ListDirectoryTool.cs:TopDirectoryOnly` | ~~Add depth parameter, default to 2 levels~~ Done |
| T4 | **DIVERGENT** | 🟡 MAJOR | Grep Tool | **Backend differs.** TS uses external `ripgrep` (rg) binary for fast, gitignore-aware search. C# uses .NET `Regex` with manual file enumeration. Performance will be worse on large repos. | `grep.ts` uses rg CLI | `GrepTool.cs` uses Regex class | Consider optional rg integration; current approach is functionally correct |
| T5 | **DIVERGENT** | 🟡 MAJOR | Find/Glob Tool | **Tool name differs.** TS names it `find` (uses `fd` binary). C# names it `glob` (uses .NET Matcher). LLM may reference wrong tool name in system prompt. | `find.ts` tool name "find" | `GlobTool.cs` tool name "glob" | Align to either `find` or `glob` consistently |
| T6 | **DIVERGENT** | 🟢 MINOR | Edit Tool | **Diff context lines differ.** TS uses 4 context lines; C# uses 3. Minor visual difference in diff output. | `edit-diff.ts` context=4 | `EditTool.cs` context=3 | Change C# to 4 lines for parity |
| T7 | **DIVERGENT** | 🟢 MINOR | Read Tool | **Directory listing depth differs.** TS read tool lists directories recursively; C# limits to depth 2. | `read.ts` recursive listing | `ReadTool.cs` depth ≤ 2 | Acceptable simplification |
| T8 | **MISSING** | 🟢 MINOR | Shell Tool | **No temp file on overflow.** TS saves full output to temp file when truncated; C# discards overflow. | `bash.ts` temp file logic | N/A | Consider adding for debugging |
| T9 | **STYLE** | 🟢 MINOR | File Mutation Queue | **Cleanup differs.** TS removes queue entries when no waiters. C# keeps SemaphoreSlim instances in ConcurrentDictionary permanently. Small memory leak over long sessions. | `file-mutation-queue.ts` cleanup | `FileMutationQueue.cs` no cleanup | Add periodic cleanup or WeakReference pattern |
| T10 | **STYLE** | 🟢 MINOR | Path Utils | **Tilde expansion missing.** TS expands `~` to home directory. C# does not. | `path-utils.ts` expandPath() | N/A in PathUtils.cs | Add tilde expansion if agents use `~` paths |
| T11 | **MISSING** | 🟢 MINOR | Path Utils | **macOS screenshot handling missing.** TS normalizes macOS NFD unicode and AM/PM filename variants. | `path-utils.ts` macOS logic | N/A | Low priority; macOS-specific edge case |

---

### 2. SYSTEM PROMPT

| # | Category | Severity | Description | TS Reference | C# Reference | Recommended Fix |
|---|----------|----------|-------------|--------------|--------------|-----------------|
| P1 | **DIVERGENT** | 🟡 MAJOR | **Role statement differs.** TS: "expert coding assistant operating inside pi"; C#: "coding assistant with access to tools". The TS phrasing gives stronger identity. | `system-prompt.ts:~L20` | `SystemPromptBuilder.cs:~L34` | Update C# to match TS branding (adapt to BotNexus) |
| P2 | **DIVERGENT** | 🟡 MAJOR | **Guidelines are static in C#.** TS dynamically generates guidelines based on available tools (e.g., "prefer grep/find over bash for search"). C# has 4 fixed guidelines. | `system-prompt.ts:L91-119` | `SystemPromptBuilder.cs:L118-134` | Add tool-aware guideline generation |
| P3 | **ENHANCED** | 🟢 MINOR | **C# adds Environment section.** C# includes OS, git branch, git status, package manager. TS only appends date + cwd. | `system-prompt.ts` (no env section) | `SystemPromptBuilder.cs:L88-99` | Keep — this is an improvement |
| P4 | **MISSING** | 🟡 MAJOR | **No pi documentation section.** TS includes extensive documentation guidance for the agent. C# has none. | `system-prompt.ts:L127-143` | N/A | Add BotNexus-specific documentation guidance |
| P5 | **DIVERGENT** | 🟢 MINOR | **Tool visibility.** TS only shows tools with provided snippets. C# always shows tools with default descriptions. | `system-prompt.ts:L86` | `SystemPromptBuilder.cs:L104` | Acceptable — C# approach is simpler |

---

### 3. CONTEXT FILE DISCOVERY

| # | Category | Severity | Description | TS Reference | C# Reference | Recommended Fix |
|---|----------|----------|-------------|--------------|--------------|-----------------|
| C1 | **DIVERGENT** | 🟡 MAJOR | **Different files discovered.** TS discovers `AGENTS.md` and `CLAUDE.md` walking up directories. C# discovers `.github/copilot-instructions.md`, `README.md`, `docs/*.md`. | `resource-loader.ts:L57-112` | `ContextFileDiscovery.cs:L28-40` | Add `AGENTS.md` support to C#. Keep `.github/copilot-instructions.md` too |
| C2 | **DIVERGENT** | 🟢 MINOR | **Budget enforcement.** TS loads all context files without truncation. C# enforces 16KB total budget with per-file truncation. | `resource-loader.ts` (no budget) | `ContextFileDiscovery.cs:TotalBudget=16384` | Keep C# budget — prevents prompt overflow |
| C3 | **MISSING** | 🟡 MAJOR | **No ancestor directory walk.** TS walks up from cwd to root checking for `AGENTS.md` at each level. C# only checks project root. | `resource-loader.ts:L84-109` | N/A | Add ancestor walk for `AGENTS.md` discovery |

---

### 4. CLI INTERFACE

| # | Category | Severity | Description | TS Reference | C# Reference | Recommended Fix |
|---|----------|----------|-------------|--------------|--------------|-----------------|
| L1 | **MISSING** | 🟡 MAJOR | **22+ CLI flags missing.** TS has: `--continue`, `--fork`, `--session-dir`, `--system-prompt`, `--append-system-prompt`, `--tools`, `--no-tools`, `--mode`, `--export`, `--extension`, `--skill`, `--theme`, `--models`, `--print`, `--list-models`, `--api-key`, `--offline`, `--no-extensions`, `--no-skills`, `--no-themes`, `@file` args. C# has 8 flags. | `cli/args.ts:L13-50` | `CommandParser.cs:L120-129` | Add flags incrementally. Priority: `--continue`, `--tools`, `--system-prompt`, `--api-key` |
| L2 | **MISSING** | 🟡 MAJOR | **No @file argument processing.** TS supports `@path` to attach files/images to initial prompt. C# has no equivalent. | `cli/file-processor.ts` | N/A | Add file argument processing |
| L3 | **MISSING** | 🟢 MINOR | **No extension flag forwarding.** TS captures unknown flags in `unknownFlags` map for extensions. C# drops unknown flags. | `cli/args.ts:L48` | N/A | Add if extensions need CLI flags |

---

### 5. CONFIG & SETTINGS

| # | Category | Severity | Description | TS Reference | C# Reference | Recommended Fix |
|---|----------|----------|-------------|--------------|--------------|-----------------|
| S1 | **MISSING** | 🟡 MAJOR | **No SettingsManager equivalent.** TS has 30+ settings (compaction, retry, display, shell, images, themes, etc.) with deep merge. C# has a flat config with 8 properties. | `core/settings-manager.ts` (500+ lines) | `CodingAgentConfig.cs` (8 properties) | Add settings for compaction, retry, shell, display |
| S2 | **MISSING** | 🟢 MINOR | **No retry settings.** TS configures retry (enabled, maxRetries=3, baseDelay=2s, maxDelay=60s). C# has no retry configuration. | `settings-manager.ts:retry` | N/A | Add retry configuration |
| S3 | **MISSING** | 🟢 MINOR | **No display/theme settings.** TS configures theme, terminal images, markdown rendering. C# has none. | `settings-manager.ts:theme` | N/A | Low priority — TUI not ported |
| S4 | **DIVERGENT** | 🟢 MINOR | **Config directory name differs.** TS uses `.pi/agent/` (home) + `.pi/` (project). C# uses `.botnexus-agent/` (project) + `~/.botnexus/` (home). | `config.ts:CONFIG_DIR_NAME` | `CodingAgentConfig.cs:L10-13` | Intentional rebrand — OK |

---

### 6. SESSION MANAGEMENT

| # | Category | Severity | Description | TS Reference | C# Reference | Recommended Fix |
|---|----------|----------|-------------|--------------|--------------|-----------------|
| N1 | **DIVERGENT** | 🟡 MAJOR | **Session version mismatch.** TS is at v3 with 9+ entry types. C# is at v2 with 3 entry types. Missing: `compaction`, `branch_summary`, `custom_message`, `custom`, `label`, `session_info`. | `session-manager.ts:VERSION=3` | `SessionManager.cs:Version=2` | Add missing entry types as needed |
| N2 | **MISSING** | 🟡 MAJOR | **No custom message injection.** TS extensions can inject messages into LLM context via `CustomMessageEntry`. C# has no equivalent. | `session-manager.ts:custom_message` | N/A | Add `custom_message` entry type |
| N3 | **MISSING** | 🟢 MINOR | **No session labels.** TS supports labeling messages in the tree. C# does not. | `session-manager.ts:LabelEntry` | N/A | Low priority — UI feature |
| N4 | **MISSING** | 🟢 MINOR | **No branch summary entries.** TS generates summaries when switching branches. C# does not. | `compaction/branch-summarization.ts` | N/A | Add if tree navigation is needed |
| N5 | **DIVERGENT** | 🟢 MINOR | **Session header type name.** TS: `"session"`. C#: `"session_header"`. Incompatible session files. | `session-manager.ts` | `SessionManager.cs` | Align to TS convention if cross-compat needed |

---

### 7. EXTENSION SYSTEM

| # | Category | Severity | Description | TS Reference | C# Reference | Recommended Fix |
|---|----------|----------|-------------|--------------|--------------|-----------------|
| E1 | **DIVERGENT** | 🟡 MAJOR | **Loading model.** TS loads .ts/.js modules via jiti JIT transpiler. C# loads compiled .dll assemblies via reflection. Fundamentally different but both work. | `extensions/loader.ts` | `ExtensionLoader.cs` | Intentional — STYLE |
| E2 | **MISSING** | 🟡 MAJOR | **Fewer lifecycle hooks.** TS has 8+ event types (input, user_bash, before_agent_start, before_switch, before_fork, before_tree). C# has 5 hooks. | `extensions/types.ts` | `IExtension.cs` | Add hooks as extensions need them |
| E3 | **MISSING** | 🟡 MAJOR | **No ExtensionAPI.** TS extensions get rich API (registerCommand, registerShortcut, UI dialogs, tool registration, message injection). C# extensions only have IExtension methods. | `extensions/types.ts:ExtensionAPI` | `IExtension.cs` | Add `IExtensionContext` with tool/command registration |
| E4 | **MISSING** | 🟢 MINOR | **No UI context for extensions.** TS provides select(), confirm(), input(), notify(), setWidget(). C# has none. | `extensions/types.ts:ui` | N/A | Low priority — TUI not ported |

---

### 8. AUTH SYSTEM

| # | Category | Severity | Description | TS Reference | C# Reference | Recommended Fix |
|---|----------|----------|-------------|--------------|--------------|-----------------|
| A1 | **SIMPLIFIED** | 🟢 MINOR | **OAuth-only auth.** TS supports both API key and OAuth credentials per provider. C# only supports OAuth flow for GitHub Copilot. | `auth-storage.ts:type="api_key"|"oauth"` | `AuthManager.cs` (OAuth only) | Add API key support for Anthropic/OpenAI direct use |
| A2 | **MISSING** | 🟢 MINOR | **No file locking on auth.json.** TS uses `proper-lockfile` for concurrent access safety. C# has no locking. | `auth-storage.ts:proper-lockfile` | `AuthManager.cs` | Add file locking if concurrent access is possible |
| A3 | **DIVERGENT** | 🟢 MINOR | **Token refresh buffer.** TS checks `Date.now() < expires`. C# checks with 60-second buffer (`nowMs >= expires - 60000`). C# is more conservative — good. | `auth-storage.ts` | `AuthManager.cs` | Keep C# approach — ENHANCED |

---

### 9. MODES

| # | Category | Severity | Description | TS Reference | C# Reference | Recommended Fix |
|---|----------|----------|-------------|--------------|--------------|-----------------|
| M1 | **MISSING** | 🟡 MAJOR | **No Print Mode.** TS supports `--print` / `-p` for single-shot text/JSON output (scripting/piping). C# has `--non-interactive` but less featured. | `modes/print-mode.ts` | N/A | Add print mode for scripting scenarios |
| M2 | **MISSING** | 🟡 MAJOR | **No RPC Mode.** TS supports `--mode rpc` for headless JSON-RPC protocol (embedding in editors/tools). C# has no equivalent. | `modes/rpc/rpc-mode.ts` | N/A | Add if editor integration planned |
| M3 | **MISSING** | 🟡 MAJOR | **No TUI components.** TS has 30+ TUI components (session selector, model selector, theme selector, diff viewer, etc.). C# has basic Console I/O. | `modes/interactive/components/` | `Cli/OutputFormatter.cs` | Add incrementally as needed |

---

### 10. MISSING SYSTEMS

| # | Category | Severity | Description | TS Reference | C# Reference | Recommended Fix |
|---|----------|----------|-------------|--------------|--------------|-----------------|
| X1 | **MISSING** | 🟢 MINOR | **No migrations system.** TS has 5 migration steps for data format upgrades. C# has none. | `migrations.ts` | N/A | Add when session/config format changes |
| X2 | **MISSING** | 🟢 MINOR | **No HTML export.** TS can export sessions to styled HTML. C# cannot. | `core/export-html/` | N/A | Low priority |
| X3 | **MISSING** | 🟢 MINOR | **No package manager CLI.** TS has install/remove/update/list commands. C# only detects package managers. | `package-manager-cli.ts` + `core/package-manager.ts` | `PackageManagerDetector.cs` | Detection-only is sufficient for system prompt |
| X4 | **MISSING** | 🟢 MINOR | **No theme system.** TS has dark/light/custom themes with JSON schemas. C# has fixed console colors. | `modes/interactive/theme/` | N/A | Low priority — TUI feature |
| X5 | **MISSING** | 🟢 MINOR | **No keybinding system.** TS supports configurable keybindings. C# uses fixed key handling. | `core/keybindings.ts` | N/A | Low priority — TUI feature |
| X6 | **MISSING** | 🟢 MINOR | **No event bus.** TS has a general-purpose EventBus for decoupled communication. C# uses direct method calls. | `core/event-bus.ts` | N/A | Add if decoupled events needed |
| X7 | **MISSING** | 🟢 MINOR | **No prompt templates.** TS supports loadable prompt templates from disk. C# does not. | `core/prompt-templates.ts` | N/A | Add when prompt customization needed |
| X8 | **ENHANCED** | 🟢 MINOR | **C# PackageManagerDetector is broader.** C# detects 10 ecosystems (dotnet, cargo, maven, gradle, bundler, python) vs TS focus on npm/git. | N/A | `PackageManagerDetector.cs` | Keep — improvement |

---

### 11. TEST COVERAGE COMPARISON

| Area | TS Test Files | C# Test Files | Gap |
|------|--------------|---------------|-----|
| Agent session (core) | 11 | 1 | 🔴 Major gap — branching, retry, concurrency, compaction E2E untested |
| Tools | 6 | 7 | ✅ C# covers all tools |
| CLI | 2 (implicit in E2E) | 2 | ✅ Comparable |
| Session management | 3 | 2 | ⚠️ TS has branching/tree tests |
| Compaction | 2 | 1 | ⚠️ TS has E2E compaction with real LLM |
| Extensions | 3 | 2 | ✅ Comparable |
| Skills | 1 (in extensions) | 1 | ✅ Match |
| Config | 1 | 1 | ✅ Match |
| Path utils | 2 | 1 | ⚠️ TS has macOS-specific tests |
| Safety/hooks | 1 | 1 | ✅ Match |
| System prompt | 1 | 1 | ✅ Match |
| Print mode | 2 | 0 | ❌ Not applicable |
| RPC mode | 2 | 0 | ❌ Not applicable |
| Bash platform-specific | 3 | 1 | ⚠️ TS has Windows handle cleanup tests |
| **TOTAL** | **~52** | **22** | **~42% coverage parity** |

---

## Prioritized Fix List

### Priority 1 — Critical (Fix Now) — ✅ All fixed in Phase 5
1. **T1**: ~~Fix shell tool output truncation to keep tail (last lines) instead of head~~ ✅ Done
2. **T2**: ~~Remove or increase default shell timeout (120s is too aggressive)~~ ✅ Done (now configurable, default 600s)

### Priority 2 — Major (Fix Soon)
3. **T3**: ~~Add depth parameter to ListDirectory tool (default 2 levels)~~ ✅ Done in Phase 5
4. **C1**: ~~Add `AGENTS.md` discovery to ContextFileDiscovery~~ ✅ Done in Phase 5
5. **C3**: ~~Add ancestor directory walk for context files~~ ✅ Done in Phase 5
6. **L1**: Add critical CLI flags: `--continue`, `--tools`, `--system-prompt`, `--api-key`
7. **P2**: Make system prompt guidelines dynamic based on available tools
8. **N1**: Add missing session entry types (compaction, branch_summary, custom_message)
9. **T5**: Align tool naming (find vs glob) — pick one and use consistently

### Priority 3 — Major (Plan for Next Phase)
10. **M1**: Add print mode for scripting
11. **M2**: Add RPC mode for editor integration
12. **S1**: Add SettingsManager with compaction, retry, shell settings
13. **E2/E3**: Extend IExtension with more lifecycle hooks and API
14. **L2**: Add @file argument processing
15. **P4**: Add BotNexus documentation guidance to system prompt

### Priority 4 — Minor (Backlog)
16. **T6**: Align diff context lines (3 → 4)
17. **T9**: Add FileMutationQueue cleanup
18. **T10**: Add tilde expansion to PathUtils
19. **A1**: Add API key auth support alongside OAuth
20. **A2**: Add file locking to AuthManager
21. **X1**: Add migrations framework
22. **N5**: Align session header type name

---

## Conclusion

The C# port successfully captures the **core value proposition** — an LLM-powered coding agent with file tools, session persistence, and extension support. The tool implementations are high-fidelity ports with two critical behavioral bugs (shell truncation direction and default timeout). The largest intentional simplifications are in the CLI surface (30+ → 8 flags), TUI components (entirely absent), and run modes (interactive only). The extension system is architecturally different (.dll vs .ts modules) but functionally equivalent for the supported hooks. Priority fixes should focus on the two critical tool bugs and then expanding context file discovery and CLI flags.
