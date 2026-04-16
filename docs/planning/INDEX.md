# Planning Index

> Auto-maintained by Nova. Last updated: 2025-07-23.

## Active

| ID | Type | Priority | Status | Created | Summary |
|----|------|----------|--------|---------|---------|
| bug-exec-process-disconnect | bug | high | ready | 2026-07-18 | ExecTool and ProcessTool built on wrong assumptions about process lifecycle |
| bug-session-lifecycle-fragmentation | bug | high | ready | 2026-04-15 | 7 session creation paths, no single truth; sub-agent sessions never persisted |
| bug-session-switching-ui | bug | high | partially-delivered | 2026-04-10 | Cross-session message bleed when switching agents in WebUI |
| bug-steering-delivery-latency | bug | high | draft | 2026-04-10 | Steering messages delivered too late to influence agent behavior |
| bug-steering-message-visibility | bug | medium | draft | 2026-04-10 | Steering messages not visible in conversation flow in WebUI |
| feature-context-visibility | feature | medium | draft | 2026-04-10 | /context command to show token usage, context window state |
| feature-media-pipeline | feature | high | draft | 2026-07-14 | Audio transcription and extensible media type pipeline |
| feature-planning-pipeline | process | n/a | active | 2026-04-10 | Planning folder conventions and spec lifecycle (this process) |
| feature-spec-driven-squad-automation | feature | medium | draft | 2025-07-23 | Automate Squad execution based on spec status transitions |
| improvement-memory-lifecycle | improvement | high | draft | 2026-04-10 | Memory persistence, compaction, and dreaming consolidation |
| improvement-skills-path-resolution | improvement | medium | draft | 2026-04-15 | Expose skill base path on load so agents can resolve relative file refs |
| message-queue-injection-timing | bug | -- | planning | -- | User messages queued during multi-tool turns not visible until turn completes |

## Archived / Done

| ID | Type | Priority | Status | Created | Summary |
|----|------|----------|--------|---------|---------|
| bug-edit-tool-double-parse | bug | high | done | 2026-04-14 | EditTool double-parses edits argument, breaking all edit calls |
| bug-pathutils-ignores-file-access-policy | bug | high | done | 2026-04-14 | PathUtils enforces workspace-only, ignoring FileAccessPolicy |
| bug-session-resumption | bug | critical | in-progress | 2026-04-10 | Session rehydration fails after gateway restart; regressed after initial fix |
| bug-subagent-spawn-path | bug | critical | delivered | 2026-04-14 | Sub-agent AgentId contains :: which creates illegal Windows paths |
| bug-tool-argument-type-mismatch | bug | high | done | 2026-04-14 | Type mismatch between StreamingJsonParser and tool argument parsers |
| ddd-refactoring | -- | -- | done | -- | Domain-driven design refactoring of entire codebase |
| feature-agent-delay-tool | feature | medium | draft | 2026-07-17 | Agent delay/wait tool for pausing mid-turn |
| feature-agent-file-access-policy | feature | high | delivered | 2026-07-18 | Per-agent file access policy configuration |
| feature-extension-contributed-commands | feature | medium | delivered | 2026-04-15 | Extension-contributed commands for WebUI/TUI command palette |
| feature-file-watcher-tool | feature | medium | draft | 2026-07-19 | File watcher tool for reactive file change monitoring |
| feature-infinite-scrollback | feature | -- | draft | 2025-07-24 | Infinite scrollback without DOM wipe; paginated history loading |
| feature-location-management | feature | -- | -- | -- | Location management (minimal spec) |
| feature-multi-session-connection | feature | -- | -- | -- | Multi-session SignalR connection architecture |
| feature-session-visibility | feature | high | implemented | 2026-04-11 | Session visibility rules for multi-session UI |
| feature-subagent-spawning | feature | high | done | 2026-04-10 | Sub-agent spawning for parallel work delegation |
| feature-subagent-ui-visibility | feature | medium | delivered | 2026-04-10 | Sub-agent sessions visible in WebUI sidebar |
| feature-tool-permission-model | feature | -- | done | 2026-04-11 | Per-agent file system permission model |
| improvement-agent-trust-paths | improvement | medium | draft | 2026-04-10 | Configurable per-agent trusted file paths |
| improvement-datetime-awareness | improvement | medium | draft | 2026-04-10 | Agent datetime/timezone awareness in system prompt |

---

_To update: Nova maintains this index. Run `/planning index` (future) or ask Nova to refresh it._
