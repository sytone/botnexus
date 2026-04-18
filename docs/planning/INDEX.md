# 🗺️ Planning Index

> Maintained by Nova ✨ | Last updated: 2025-07-17
>
> Generated from spec frontmatter via `build-index.ps1` — don't hand-edit, rebuild instead.

---

## 🔥 Active

### 🐛 Bug Fixes

| Item | Pri | Status | Since | Summary |
|------|-----|--------|-------|---------|
| [bug-sqlite-session-lock](bug-sqlite-session-lock/design-spec.md) | 🔴 critical | draft | Apr '26 | SQLite global SemaphoreSlim(1,1) blocks all agents during any session I/O |
| [bug-edit-tool-diffplex-missing](bug-edit-tool-diffplex-missing/design-spec.md) | 🔴 high | draft | Apr '26 | Edit tool fails — DiffPlex assembly not found at runtime |
| [bug-session-switching-ui](bug-session-switching-ui/design-spec.md) | 🔴 high | partial | Apr '26 | Session switching broken during active agent work (send-side still open) |
| [bug-steering-message-visibility](bug-steering-message-visibility/design-spec.md) | 🟡 medium | draft | Apr '26 | Steering messages not visible in conversation flow |
| [message-queue-injection-timing](message-queue-injection-timing/design-spec.md) | 🟡 medium | planning | Jul '25 | User messages queued during multi-tool turns not visible until turn completes |

### ✨ Features

| Item | Pri | Status | Since | Summary |
|------|-----|--------|-------|---------|
| [feature-ask-user-tool](feature-ask-user-tool/design-spec.md) | 🔴 high | design | Apr '26 | Interactive ask_user tool — free-form, single/multi choice, hybrid input |
| [feature-prompt-templates](feature-prompt-templates/design-spec.md) | 🟡 medium | planning | Jul '26 | Saved parameterized prompt templates for agents, cron, and interactive use |
| [feature-spec-driven-squad-automation](feature-spec-driven-squad-automation/design-spec.md) | 🟡 medium | draft | Jul '25 | Automate Squad execution based on spec status transitions |
| [feature-api-documentation](feature-api-documentation/design-spec.md) | 🟡 medium | draft | Jul '25 | REST, SignalR, and .NET API reference — OpenAPI, DocFX, interactive docs |
| [feature-architecture-documentation](feature-architecture-documentation/design-spec.md) | 🟡 medium | draft | Jul '25 | Architecture documentation — arc42, C4, ADRs |
| [feature-code-documentation](feature-code-documentation/design-spec.md) | 🟡 medium | draft | Jul '25 | Contributing guide, XML comment standards, developer guides |

### 🔧 Improvements

| Item | Pri | Status | Since | Summary |
|------|-----|--------|-------|---------|
| [improvement-dynamic-config-reload](improvement-dynamic-config-reload/design-spec.md) | 🔴 high | draft | Apr '26 | Hot-reload config.json without gateway restart |
| [improvement-memory-lifecycle](improvement-memory-lifecycle/design-spec.md) | 🔴 high | draft | Apr '26 | Memory persistence, pre-compaction flush, dreaming consolidation |
| [improvement-skills-path-resolution](improvement-skills-path-resolution/design-spec.md) | 🟡 medium | draft | Apr '26 | Expose skill base path on load so agents can resolve relative file refs |

### 📋 Process

| Item | Status | Since | Purpose |
|------|--------|-------|---------|
| [feature-planning-pipeline](feature-planning-pipeline/design-spec.md) | active | Apr '26 | Planning folder conventions and spec lifecycle |

---

## ✅ Archived / Done

<details>
<summary>🐛 Bug Fixes — 12 resolved</summary>

| Item | Pri | Status | Since | Summary |
|------|-----|--------|-------|---------|
| bug-subagent-spawn-path | 🔴 critical | delivered | Apr '26 | Sub-agent AgentId :: creates illegal Windows paths |
| bug-internal-channel-adapter-missing | 🔴 critical | delivered | Jul '26 | Internal channel adapter missing — sub-agent messages silently drop |
| bug-session-resumption | 🔴 critical | in-progress | Apr '26 | Session rehydration fails after gateway restart |
| bug-cross-agent-session-blocking | 🔴 high | delivered | Jul '26 | Response delivery stalls while another agent runs |
| bug-edit-tool-double-parse | 🔴 high | done | Apr '26 | EditTool double-parses edits argument |
| bug-exec-process-disconnect | 🔴 high | done | Jul '26 | ExecTool/ProcessTool built on wrong process lifecycle assumptions |
| bug-pathutils-ignores-file-access-policy | 🔴 high | done | Apr '26 | PathUtils enforces workspace-only, ignoring FileAccessPolicy |
| bug-session-lifecycle-fragmentation | 🔴 high | done | Apr '26 | 7 session creation paths, no single truth |
| bug-steering-delivery-latency | 🔴 high | done | Apr '26 | Steering messages delivered too late |
| bug-tool-argument-type-mismatch | 🔴 high | done | Apr '26 | Type mismatch between StreamingJsonParser and tool parsers |
| bug-blazor-message-timestamps-ordering | 🟡 medium | delivered | Apr '26 | Blazor messages missing timestamps and misordered |
| bug-subagent-realtime-updates | 🟡 medium | done | Jul '26 | Sub-agent SignalR bridge missing — WebUI only showed on refresh |

</details>

<details>
<summary>✨ Features — 14 shipped/archived</summary>

| Item | Pri | Status | Since | Summary |
|------|-----|--------|-------|---------|
| feature-context-diagnostics | 🔴 critical | delivered | Apr '26 | /context command + debug API |
| feature-blazor-webui | 🔴 high | delivered | Apr '26 | Blazor WASM SPA migration |
| feature-config-management-api | 🔴 high | delivered | Apr '26 | Full config CRUD via Gateway REST + dynamic reload |
| feature-user-documentation | 🔴 high | delivered | Jul '25 | User docs — Diátaxis, tutorials, how-tos, reference |
| feature-tool-permission-model | 🔴 high | done | Apr '26 | Per-agent file system permission model |
| feature-agent-file-access-policy | 🔴 high | delivered | Jul '26 | Per-agent file access policy configuration |
| feature-subagent-spawning | 🔴 high | done | Apr '26 | Sub-agent spawning for parallel work delegation |
| feature-session-visibility | 🔴 high | implemented | Apr '26 | Session visibility rules for multi-session UI |
| feature-media-pipeline | 🔴 high | delivered | Jul '26 | Audio transcription and extensible media types |
| feature-subagent-ui-visibility | 🟡 medium | delivered | Apr '26 | Sub-agent sessions visible in WebUI sidebar |
| feature-extension-contributed-commands | 🟡 medium | delivered | Apr '26 | Extension-contributed commands for WebUI/TUI |
| feature-context-visibility | 🟡 medium | superseded | Apr '26 | Superseded by feature-context-diagnostics |
| feature-location-management | 🟡 medium | done | Jul '26 | Location management |
| feature-infinite-scrollback | 🟡 medium | draft | Jul '25 | Infinite scrollback — paginated history |
| feature-file-watcher-tool | 🟡 medium | draft | Jul '26 | File watcher tool |
| feature-agent-delay-tool | 🟡 medium | draft | Jul '26 | Agent delay/wait tool |

</details>

<details>
<summary>🔧 Improvements — 9 completed/archived</summary>

| Item | Pri | Status | Since | Summary |
|------|-----|--------|-------|---------|
| improvement-repo-folder-and-namespace-cleanup | 🔴 high | delivered | Apr '26 | Repo folder and namespace cleanup |
| improvement-heartbeat-service | 🔴 high | delivered | Jul '26 | Reliable periodic agent polling via cron |
| improvement-subagent-completion-handling | 🔴 high | delivered | Jul '26 | Reliable parent wake-up on sub-agent completion |
| improvement-memory-indexing | 🔴 high | done | Jul '26 | MemoryIndexer hosted service + CLI backfill |
| ddd-refactoring | 🔴 high | done | Jul '25 | Domain-driven design refactoring |
| improvement-blazor-chat-autoscroll | 🟡 medium | delivered | Apr '26 | Blazor chat auto-scroll to bottom |
| improvement-extension-config-inheritance | 🟡 medium | delivered | Jul '25 | World-level extension config defaults with agent overrides |
| improvement-agent-trust-paths | 🟡 medium | draft | Apr '26 | Configurable per-agent trusted file paths |
| improvement-datetime-awareness | 🟡 medium | draft | Apr '26 | Agent datetime/timezone awareness |

</details>

---

> **Legend:** 🔴 critical/high · 🟡 medium · 🟢 low · ⚪ unset
>
> **Statuses:** planning → draft → design → ready → in-progress → delivered → done
>
> **Rebuild:** `pwsh docs/planning/build-index.ps1` → JSON → regenerate this file
