---
id: feature-agent-file-access-policy
title: "Per-Agent File Access Policy - Research"
type: research
created: 2026-07-18
author: nova
---

# Research: Per-Agent File Access Policy Configuration

## Codebase Investigation

### Complete Tool Inventory

Tools are registered from three locations in the BotNexus codebase:

#### 1. Core Tools ()

Registered via :

| Tool Name | Class | File | Accepts PathValidator |
|-----------|-------|------|-----------------------|
| bash | ShellTool | ShellTool.cs | No |
| read | ReadTool | ReadTool.cs | Yes |
| write | WriteTool | WriteTool.cs | Yes |
| edit | EditTool | EditTool.cs | Yes |
| ls | ListDirectoryTool | ListDirectoryTool.cs | Yes |
| grep | GrepTool | GrepTool.cs | Yes |
| glob | GlobTool | GlobTool.cs | Yes |

#### 2. Gateway Tools ()

Registered directly in DI or created per-session:

| Tool Name | Class | File |
|-----------|-------|------|
| sessions | SessionTool | SessionTool.cs |
| agent_converse | AgentConverseTool | AgentConverseTool.cs |
| spawn_subagent | SubAgentSpawnTool | SubAgentSpawnTool.cs |
| list_subagents | SubAgentListTool | SubAgentListTool.cs |
| manage_subagent | SubAgentManageTool | SubAgentManageTool.cs |
| delay | DelayTool | DelayTool.cs |
| watch_file | FileWatcherTool | FileWatcherTool.cs |

#### 3. Extension Tools ()

Loaded dynamically via :

| Tool Name | Class | Location |
|-----------|-------|----------|
| exec | ExecTool | extensions/tools/exec/BotNexus.Extensions.ExecTool/ |
| process | ProcessTool | extensions/tools/process/BotNexus.Extensions.ProcessTool/ |
| web_fetch | WebFetchTool | extensions/web/BotNexus.Extensions.WebTools/ |
| web_search | WebSearchTool | extensions/web/BotNexus.Extensions.WebTools/ |
| skills | SkillTool | extensions/skills/BotNexus.Extensions.Skills/ |

#### 4. Other Tools

| Tool Name | Class | Location |
|-----------|-------|----------|
| cron | CronTool | src/cron/BotNexus.Cron/Tools/ |
| memory_search | MemorySearchTool | src/tools/BotNexus.Memory/Tools/ |
| memory_get | MemoryGetTool | src/tools/BotNexus.Memory/Tools/ |
| memory_store | MemoryStoreTool | src/tools/BotNexus.Memory/Tools/ |

### bash vs exec: Overlap Analysis

Both  (ShellTool) and  (ExecTool) execute shell commands but with different designs:

| Aspect | bash (ShellTool) | exec (ExecTool) |
|--------|------------------|-----------------|
| Location | Core tool | Extension tool |
| Input | Single command string | Command array (argv-style) |
| Background mode | No | Yes (returns PID) |
| Stdin piping | No | Yes |
| No-output timeout | No | Yes |
| Env vars | No | Yes (merge) |
| Working dir override | No | Yes |
| Windows resolution | Git Bash preferred, PowerShell fallback | .cmd/.bat resolution via cmd.exe |
| Output cap | 50KB | 100KB |
| Process tracking | No | Registers in BackgroundProcesses dict |

**Key finding**:  is a strict superset of  functionality. However:
-  is simpler for quick commands (single string vs array)
-  requires command as array, which some models struggle with
- Both exist and both are registered — agents see both tools

### exec + process: Integration Bug

**ExecTool** has its own static  for tracking background processes.

**ProcessTool** uses  (a separate static singleton with ).

These are **completely disconnected**. When  starts a background process, it records the PID in its own dictionary but never registers with . When  tries to list/status/kill that PID, it queries  which knows nothing about it.

Additionally,  is a simple record (pid, command, startedUtc) — it does not retain the  handle. So even if the data were shared,  cannot provide the output capture or stdin piping that  supports.

**To fix**: ExecTool should create a  wrapper and register it with  instead of (or in addition to) its own dictionary.

### DefaultPathValidator Behavior

The path validator logic in :

1. Check deny list first — if path matches any denied pattern, reject
2. If policy is null/empty (): only allow paths under workspace
3. If policy has allowed paths: check if path matches any allowed pattern
4. Always allow paths under workspace (fallback)

This means:
- Setting  adds Q:/repos as readable WITHOUT removing workspace access
- Deny list always wins over allow list
- Glob patterns work via 

### Configuration Flow



The path from AgentDescriptor to DefaultPathValidator is already wired at line 99 of InProcessIsolationStrategy:


So once  is populated from config, everything downstream just works.

### System Prompt References

The gateway SystemPromptBuilder references  and  by name:
-  and 
- Used to generate tool usage guidelines in the system prompt
- These resolve correctly because the extension tools register with those names

The  classifies these tools as approval-required:


### Extension Loading

Extensions are discovered via  which:
1. Scans the extensions path for DLLs
2. Looks for types implementing 
3. Checks for auto-resolvable constructors
4. Registers them as  in DI
5.  collects all  instances

The extension config for exec/process in agent config:


## Recommendation

The file access policy feature is a clean, low-risk change:
1. Add config model property + mapping (Phase 1) — ~20 lines
2. The domain, security, and tool layers are already complete
3. Hot reload should work since path validator is created per-execution

The ironic proof: Nova can  for any file on the system but cannot use  outside workspace. The policy exists to govern the structured tools.
