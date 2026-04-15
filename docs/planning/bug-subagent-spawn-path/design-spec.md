---
id: bug-subagent-spawn-path
title: "Sub-Agent AgentId Contains :: Which Creates Illegal Windows Paths"
type: bug
priority: critical
status: delivered
created: 2026-04-14
updated: 2026-04-15
author: nova
tags: [agents, sub-agents, windows, paths, spawning]
---

# Bug: Sub-Agent AgentId Contains `::` - Illegal Windows Paths

## Summary

`DefaultSubAgentManager.SpawnAsync` creates sub-agent AgentIds with `::` separators (e.g., `my-agent::subagent::general::abc123`). The `:` character is illegal in Windows file and directory names, causing all sub-agent spawning to fail on Windows.

## Symptoms

- Every sub-agent spawn fails on Windows
- Path errors when creating agent workspace directories
- Path errors when writing agent configuration files
- All other agent operations (direct agents, sessions) work fine

## Root Cause

`DefaultSubAgentManager.cs` line 66:
```csharp
var childAgentId = AgentId.From(`$"{request.ParentAgentId}::subagent::{archetype.Value}::{uniqueId}"`);
```

This AgentId flows to filesystem operations via:
- `BotNexusHome.GetAgentDirectory(agentId)` - directory with `:` fails
- `FileAgentConfigurationWriter.GetConfigPath(agentId)` - filename with `:` fails
- `FileAgentWorkspaceManager.GetWorkspacePath(agentId)` - workspace dir fails
- `InProcessIsolationStrategy` line 186 - skills directory fails

**Secondary issue**: `AgentSessionKey.Parse()` splits on `::` to separate `agentId::sessionId`. Sub-agent AgentIds containing `::` cause incorrect parsing.

## Fix: Change AgentId separator to `--`

Replace `::` with `--` in the sub-agent AgentId format only. SessionId format (`parent-session::subagent::uniqueId`) is NOT changed.

**Status**: In Progress
