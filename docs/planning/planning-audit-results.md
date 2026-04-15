# Planning Audit Results
**Date**: 2026-07-18 (updated from 2026-04-12)
**Assessor**: Nova
**Method**: Cross-referenced all planning specs against current codebase

## Summary

| # | Folder | Status | Action |
|---|--------|--------|--------|
| 1 | feature-agent-file-access-policy | **DELIVERED** | Archived |
| 2 | improvement-agent-trust-paths | **SUPERSEDED** | Archived (by #1) |
| 3 | feature-location-management | **partially-delivered** | Keep - Phases 1-2 done, CLI/WebUI remain |
| 4 | bug-exec-process-disconnect | **ready** | Keep - both tools built wrong, needs rewrite |
| 5 | bug-subagent-spawn-path | **confirmed** | Keep - `::` in AgentId creates illegal Windows paths |
| 6 | bug-session-resumption | **in-progress** | Keep - Phase 1 context bridge still the critical gap |
| 7 | bug-session-switching-ui | **partially-delivered** | Keep - receive-side fixed, send-side race remains |
| 8 | bug-steering-delivery-latency | **draft** | Keep - mid-turn injection still needed |
| 9 | bug-steering-message-visibility | **draft** | Keep - steering msgs not visible in conversation |
| 10 | feature-context-visibility | **draft** | Keep - no token count visibility exists |
| 11 | feature-media-pipeline | **draft** | Keep - architecture spec for audio/media pipeline |
| 16 | bug-pathutils-ignores-file-access-policy | **DONE** | Archived - PathUtils removed entirely |
| 17 | bug-tool-argument-type-mismatch | **confirmed** | NEW - exec/edit broken by StreamingJsonParser type mismatch |
| 12 | feature-subagent-ui-visibility | **draft** | Keep - sub-agents not visible in WebUI |
| 13 | feature-planning-pipeline | **active** | Keep - process convention doc |
| 14 | improvement-memory-lifecycle | **draft** | Keep - no pre-compaction flush exists |
| 15 | message-queue-injection-timing | **planning** | Keep - mid-turn message injection |

**Archived this audit: 3** | **Active: 14** (including 1 process doc)

---

## Changes Since Last Audit (2026-04-12)

### Newly Archived
- **feature-agent-file-access-policy** - Fully delivered. `FileAccessPolicyConfig` on `AgentDefinitionConfig` and `GatewaySettingsConfig`, `@location-ref` resolution in `PlatformConfigAgentSource`, `DefaultPathValidator` enforces policies. Nova actively using it.
- **improvement-agent-trust-paths** - Superseded by the above. Same concept, earlier draft.
- **bug-pathutils-ignores-file-access-policy** - Fixed by removing PathUtils entirely. grep/glob now work on all FileAccessPolicy-allowed paths.

### Previously Archived (2026-04-12)
- feature-agent-delay-tool (DelayTool implemented)
- feature-file-watcher-tool (FileWatcherTool implemented)
- feature-infinite-scrollback (cursor pagination + IntersectionObserver)
- feature-multi-session-connection (subscribe-all model)
- feature-session-visibility (SessionType filtering)
- improvement-datetime-awareness (SystemPromptBuilder timezone injection)

### Status Changes
- **feature-location-management**: draft -> **partially-delivered** (Phases 1-2 shipped, CLI/WebUI/doctor remain)
- **bug-subagent-spawn-path**: draft -> **confirmed** (root cause pinpointed to `DefaultSubAgentManager.cs` line ~69)
- **bug-session-switching-ui**: delivered -> **partially-delivered** (receive-side fixed in 28a0329, send-side race still open)

### New Planning Items (this session)
- **bug-exec-process-disconnect** - Created 2026-07-18. ExecTool has dead state tracking and premature Process disposal. ProcessTool queries empty registry instead of OS processes. Both need rewrite.

---

## Active Item Details

### Critical Priority
| Item | Core Issue | Blocking |
|------|-----------|----------|
| bug-session-resumption | Agent loses all context on gateway restart | Core continuity |
| bug-subagent-spawn-path | `::` in AgentId creates illegal Windows paths | All sub-agent delegation |

### High Priority
| Item | Core Issue | Blocking |
|------|-----------|----------|
| bug-exec-process-disconnect | Both tools built on wrong assumptions | Process management for agents |
| bug-session-switching-ui | Send-side race routes messages to wrong session | Multi-agent UX |
| feature-location-management | CLI/doctor/WebUI for location management | Operator experience |
| bug-tool-argument-type-mismatch | StreamingJsonParser types don't match tool parsers | exec and edit completely broken |

### Medium Priority
| Item | Core Issue | Blocking |
|------|-----------|----------|
| bug-steering-delivery-latency | Steering only delivered between turns, not tool calls | Review loops, mid-turn guidance |
| bug-steering-message-visibility | Steering messages not visible in conversation timeline | UX clarity |
| feature-context-visibility | No token count or context window visibility | Agent self-awareness |
| feature-subagent-ui-visibility | Sub-agent sessions invisible in WebUI | Observability |
| improvement-memory-lifecycle | No pre-compaction memory flush | Data persistence |
| message-queue-injection-timing | User messages queued until full turn completes | Collaborative workflows |

### Informational
| Item | Notes |
|------|-------|
| feature-planning-pipeline | Process convention doc - always active |
| feature-media-pipeline | Architecture spec for future audio/media support |

---

## Dependency Graph

```
bug-subagent-spawn-path
  |
  +-> feature-subagent-ui-visibility (can't show what can't spawn)
  
bug-session-switching-ui (send-side fix)
  |
  +-> feature-subagent-ui-visibility (session switching must work first)

bug-session-resumption (Phase 1: context bridge)
  |
  +-> improvement-memory-lifecycle (flush needs stable session lifecycle)

bug-steering-delivery-latency
  |
  +-> message-queue-injection-timing (same pipeline, different angles)

feature-location-management (Phases 3-4)
  |
  (no blockers, independent)

bug-exec-process-disconnect
  |
  (no blockers, independent)
```

## Recommended Execution Order

1. **bug-tool-argument-type-mismatch** - One-line fix in StreamingJsonParser, unblocks exec and edit tools
2. **bug-subagent-spawn-path** - Small fix, unblocks sub-agent workflows
3. **bug-exec-process-disconnect** - Independent, clear scope, has test matrix
4. **bug-session-resumption Phase 1** - The critical continuity fix
5. **bug-session-switching-ui** - Send-side race fix
6. **feature-location-management Phase 3** - CLI commands
7. **Everything else** - Can be prioritized as needed
