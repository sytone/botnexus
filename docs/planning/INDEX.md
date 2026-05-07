# 📋 Planning Index

> Auto-maintained by Nova's daily planning maintenance. Last updated: 2026-05-07

## 🐛 Bugs

### 🔴 Critical
- [SQLite Session Store Global Lock Blocks Multi-Agent Concurrency](bug-sqlite-session-lock/design-spec.md) — `draft`
- [No Tool Execution Timeout or Stuck-Turn Recovery](bug-tool-execution-timeout/design-spec.md) — `draft`

### 🟠 High
- [NO_REPLY Sentinel Visible as Literal Text in Blazor UI](bug-noreply-visible-in-ui/design-spec.md) — `draft`

### 🟡 Medium
- [Blazor UI Loses Session History for Agent/Channel Combo](bug-blazor-session-history-loss/design-spec.md) — `done`
- [Message Queue Injection Timing](message-queue-injection-timing/design-spec.md) — `planning`
- [Steering Messages Not Visible in Conversation Flow](bug-steering-message-visibility/design-spec.md) — `draft` 📄

## ✨ Features

### 🟠 High
- [Conversation Model for Omnichannel Continuity](feature-conversation-topics/design-spec.md) — `ready` 📄
- [ask_user Tool](feature-ask-user-tool/design-spec.md) — `design`

### 🟡 Medium
- [API Documentation — REST, SignalR, and .NET API Reference](feature-api-documentation/design-spec.md) — `draft`
- [Architecture Documentation — arc42, C4, ADRs](feature-architecture-documentation/design-spec.md) — `draft`
- [Prompt Templates](feature-prompt-templates/design-spec.md) — `planning`
- [Spec-Driven Squad Automation](feature-spec-driven-squad-automation/design-spec.md) — `draft`
- [Code & Developer Documentation](feature-code-documentation/design-spec.md) — `draft`

## 🔧 Improvements

### 🟠 High
- [BotNexus OpenClaw Memory Model Alignment](botnexus-openclaw-memory-alignment/design-spec.md) — `planning`
- [CLI multi-instance support via --source/--target](improvement-cli-multi-instance/design-spec.md) — `in-progress`
- [Dynamic Configuration Reload](improvement-dynamic-config-reload/design-spec.md) — `in-progress`
### 🟡 Medium
- [Memory Persistence Lifecycle](improvement-memory-lifecycle/design-spec.md) — `draft` 📄
- [Skills Extension — Expose Base Path on Load](improvement-skills-path-resolution/design-spec.md) — `draft`
- [Blazor Configuration UI — C/D: Locations + Per-Agent](improvement-blazor-configuration-ui/design-spec.md) — `partially-delivered`

### 🔵 Low
- [Dynamic Agent List](improvement-blazor-dynamic-agent-list/design-spec.md) — `proposed`

## 📐 Process

- [Planning Pipeline Convention](feature-planning-pipeline/design-spec.md) — `active` 📄

---

📄 = has research.md

<details>
<summary>📦 Archived (41 items)</summary>

### Bugs
- [Internal Channel Adapter Missing](archived/bug-internal-channel-adapter-missing/design-spec.md) — `delivered`
- [Session Resumption and Rehydration](archived/bug-session-resumption/design-spec.md) — `in-progress`
- [Sub-Agent AgentId Illegal Paths](archived/bug-subagent-spawn-path/design-spec.md) — `delivered`
- [Tool Argument Type Mismatch](archived/bug-tool-argument-type-mismatch/design-spec.md) — `done`
- [Cross-Agent Session Blocking](archived/bug-cross-agent-session-blocking/design-spec.md) — `delivered`
- [EditTool Double-Parse](archived/bug-edit-tool-double-parse/design-spec.md) — `done`
- [ExecTool/ProcessTool Wrong Assumptions](archived/bug-exec-process-disconnect/design-spec.md) — `done`
- [PathUtils Ignores FileAccessPolicy](archived/bug-pathutils-ignores-file-access-policy/design-spec.md) — `done`
- [Session Lifecycle Fragmentation](archived/bug-session-lifecycle-fragmentation/design-spec.md) — `done`
- [Steering Delivery Latency](archived/bug-steering-delivery-latency/design-spec.md) — `done`
- [Blazor Message Timestamps/Ordering](archived/bug-blazor-message-timestamps-ordering/design-spec.md) — `delivered`
- [Sub-Agent Realtime Updates](archived/bug-subagent-realtime-updates/design-spec.md) — `done`
- [Blazor Auto-Scroll](archived/bug-blazor-autoscroll/design-spec.md) — `delivered`
- [Edit Tool DiffPlex Missing](archived/bug-edit-tool-diffplex-missing/design-spec.md) — `delivered`
- [Sub-Agent Completion Wakeup](archived/bug-subagent-completion-wakeup/design-spec.md) — `delivered`
- [Session Switching Broken During Active Agent Work](bug-session-switching-ui/design-spec.md) — `done` — Fixed by Blazor redesign

### Features
- [Context Diagnostics](archived/feature-context-diagnostics/design-spec.md) — `delivered`
- [Media Pipeline](archived/feature-media-pipeline/design-spec.md) — `delivered`
- [User Documentation](archived/feature-user-documentation/design-spec.md) — `delivered`
- [Tool Permission Model](archived/feature-tool-permission-model/design-spec.md) — `done`
- [Sub-Agent Spawning](archived/feature-subagent-spawning/design-spec.md) — `done`
- [Session Visibility Rules](archived/feature-session-visibility/design-spec.md) — `implemented`
- [Config Management API](archived/feature-config-management-api/design-spec.md) — `delivered`
- [Agent File Access Policy](archived/feature-agent-file-access-policy/design-spec.md) — `delivered`
- [Blazor WebUI](archived/feature-blazor-webui/design-spec.md) — `delivered`
- [Context Visibility](archived/feature-context-visibility/design-spec.md) — `superseded`
- [Sub-Agent UI Visibility](archived/feature-subagent-ui-visibility/design-spec.md) — `delivered`
- [Blazor Sub-Agent Session View](archived/feature-blazor-subagent-session-view/design-spec.md) — `done`
- [Agent Delay Tool](archived/feature-agent-delay-tool/design-spec.md) — `draft`
- [Infinite Scrollback](archived/feature-infinite-scrollback/design-spec.md) — `draft`
- [File Watcher Tool](archived/feature-file-watcher-tool/design-spec.md) — `draft`
- [Extension-Contributed Commands](archived/feature-extension-contributed-commands/design-spec.md) — `delivered`
- [Location Management](archived/feature-location-management/design-spec.md) — `done`
- [Multi-Session Connection](archived/feature-multi-session-connection/architecture-proposal.md)

### Improvements
- [Repo Folder & Namespace Cleanup](archived/improvement-repo-folder-and-namespace-cleanup/design-spec.md) — `delivered`
- [DDD Refactoring](archived/ddd-refactoring/design-spec.md) — `done`
- [Heartbeat Service](archived/improvement-heartbeat-service/design-spec.md) — `delivered`
- [Memory Indexing & Backfill](archived/improvement-memory-indexing/design-spec.md) — `done`
- [Sub-Agent Completion Handling](archived/improvement-subagent-completion-handling/design-spec.md) — `delivered`
- [Agent Trust Paths](archived/improvement-agent-trust-paths/design-spec.md) — `draft`
- [Blazor Chat Auto-Scroll](archived/improvement-blazor-chat-autoscroll/design-spec.md) — `delivered`
- [DateTime Awareness](archived/improvement-datetime-awareness/design-spec.md) — `draft`
- [Extension Config Inheritance](archived/improvement-extension-config-inheritance/design-spec.md) — `delivered`
- [Gateway Detached Process](archived/improvement-gateway-detached-process/design-spec.md) — `done`

</details>
