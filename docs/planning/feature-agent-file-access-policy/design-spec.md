---
id: feature-agent-file-access-policy
title: "Per-Agent File Access Policy Configuration"
type: feature
priority: high
status: delivered
created: 2026-07-18
author: nova
tags: [security, agents, configuration, file-access, path-validation]
ddd_types: [AgentDescriptor, FileAccessPolicy, DefaultPathValidator, AgentDefinitionConfig]
---

# Design Spec: Per-Agent File Access Policy Configuration

**Type**: Feature
**Priority**: High (blocks agent access to repos and other safe locations)
**Status**: Delivered
**Author**: Nova

## Problem

Agents are currently locked to their workspace directory for all file operations (read, write, edit, grep, glob, ls). The domain model has a fully implemented `FileAccessPolicy` with `AllowedReadPaths`, `AllowedWritePaths`, and `DeniedPaths`, and `DefaultPathValidator` correctly enforces these policies — but **there is no way to configure them**.

The configuration pipeline has two gaps:

1. **`AgentDefinitionConfig`** (the config.json model) has no `FileAccess` property
2. **`PlatformConfigAgentSource`** (the mapper) never populates `AgentDescriptor.FileAccess`
3. **`FileAgentConfigurationSource`** (the file-based config source) also never maps it

The result: `DefaultPathValidator` always receives `null` for the policy, hits the `IsPolicyEmpty` check, and falls back to workspace-only mode. Agents cannot access any path outside their workspace, even when it would be safe and useful (e.g., reading a codebase repo).

### Concrete Use Case

Nova needs read access to `Q:\repos\botnexus` to review code, write planning docs, and investigate bugs. Currently, file tools (`read`, `grep`, `glob`) return "Access denied" for any path outside `~/.botnexus/agents/nova/workspace`. Shell tools (`bash`, `exec`) bypass this restriction entirely, creating an inconsistent security model.

## Root Cause Analysis

### Domain Layer (Complete)

**`FileAccessPolicy`** — `src/domain/BotNexus.Domain/Gateway/Security/FileAccessPolicy.cs`
```csharp
public sealed record FileAccessPolicy
{
    public IReadOnlyList<string> AllowedReadPaths { get; init; } = [];
    public IReadOnlyList<string> AllowedWritePaths { get; init; } = [];
    public IReadOnlyList<string> DeniedPaths { get; init; } = [];
}
```

**`AgentDescriptor`** — `src/domain/BotNexus.Domain/Gateway/Models/AgentDescriptor.cs`
```csharp
public FileAccessPolicy? FileAccess { get; init; }
```

### Security Layer (Complete)

**`DefaultPathValidator`** — `src/gateway/BotNexus.Gateway/Security/DefaultPathValidator.cs`
- Accepts `FileAccessPolicy?` in constructor
- Resolves `~` home paths, handles glob patterns (`*`, `?`)
- Falls back to workspace-only when policy is null or empty
- Supports separate read/write path lists plus deny list
- Used by `InProcessIsolationStrategy` when creating agent tool sets

### Configuration Layer (Gap)

**`AgentDefinitionConfig`** — `src/gateway/BotNexus.Gateway/Configuration/PlatformConfig.cs`
- **Missing**: No `FileAccess` property. Cannot express file access policy in config.json.

**`PlatformConfigAgentSource`** — `src/gateway/BotNexus.Gateway/Configuration/PlatformConfigAgentSource.cs`
- **Missing**: Does not map `FileAccess` when building `AgentDescriptor`.

**`FileAgentConfigurationSource`** — `src/gateway/BotNexus.Gateway/Configuration/FileAgentConfigurationSource.cs`
- **Missing**: `BuildDescriptor()` does not map `FileAccess`.

### Tool Layer (Mixed)

File tools respect path validation:

| Tool | Source | Uses PathValidator |
|------|--------|--------------------|
| `read` | `BotNexus.Tools/ReadTool.cs` | Yes |
| `write` | `BotNexus.Tools/WriteTool.cs` | Yes |
| `edit` | `BotNexus.Tools/EditTool.cs` | Yes |
| `grep` | `BotNexus.Tools/GrepTool.cs` | Yes |
| `glob` | `BotNexus.Tools/GlobTool.cs` | Yes |
| `ls` | `BotNexus.Tools/ListDirectoryTool.cs` | Yes |
| `watch_file` | Gateway `FileWatcherTool.cs` | Yes |
| `bash` | `BotNexus.Tools/ShellTool.cs` | No (arbitrary commands) |
| `exec` | Extension `ExecTool.cs` | No (arbitrary commands) |

Shell tools inherently bypass file path validation since they execute arbitrary commands. This is expected but creates an inconsistency: agents can `bash -c "cat /etc/passwd"` but cannot `read("/etc/passwd")`. The file access policy governs structured file tools, not shell access.

## Solution

### Phase 1: Wire Configuration (Minimal — 2 files changed)

**1a. Add property to `AgentDefinitionConfig`**

In `src/gateway/BotNexus.Gateway/Configuration/PlatformConfig.cs`:

```csharp
public sealed class AgentDefinitionConfig
{
    // ... existing properties ...

    /// <summary>File access policy for this agent's file tools.</summary>
    public FileAccessPolicyConfig? FileAccess { get; set; }
}

/// <summary>Per-agent file access policy configuration.</summary>
public sealed class FileAccessPolicyConfig
{
    /// <summary>Paths the agent can read (exact paths or glob patterns). Workspace is always readable.</summary>
    public List<string>? AllowedReadPaths { get; set; }

    /// <summary>Paths the agent can write (exact paths or glob patterns). Workspace is always writable.</summary>
    public List<string>? AllowedWritePaths { get; set; }

    /// <summary>Paths explicitly denied even if otherwise allowed.</summary>
    public List<string>? DeniedPaths { get; set; }
}
```

> **Note**: Using a separate `FileAccessPolicyConfig` class (with `List<string>?` setters) rather than reusing the domain `FileAccessPolicy` record (with `IReadOnlyList<string>` init) follows the existing config pattern where config models are mutable for JSON deserialization, then mapped to domain records.

**1b. Map in `PlatformConfigAgentSource`**

In `LoadFromConfig()`, add to the `AgentDescriptor` initialization:

```csharp
FileAccess = agentConfig.FileAccess is not null
    ? new FileAccessPolicy
    {
        AllowedReadPaths = agentConfig.FileAccess.AllowedReadPaths?.ToArray() ?? [],
        AllowedWritePaths = agentConfig.FileAccess.AllowedWritePaths?.ToArray() ?? [],
        DeniedPaths = agentConfig.FileAccess.DeniedPaths?.ToArray() ?? [],
    }
    : null,
```

**1c. Map in `FileAgentConfigurationSource`** (if file-based agent configs need it)

Same pattern in `BuildDescriptor()`. Lower priority since most agents use platform config.

### Phase 2: Config.json Usage

After Phase 1, agents can be configured with file access policies:

```json
{
  "agents": {
    "nova": {
      "fileAccess": {
        "allowedReadPaths": [
          "Q:/repos/botnexus",
          "Q:/repos/botnexus/docs/**"
        ],
        "allowedWritePaths": [
          "Q:/repos/botnexus/docs/planning"
        ],
        "deniedPaths": [
          "Q:/repos/botnexus/.env",
          "Q:/repos/botnexus/**/appsettings.*.json"
        ]
      }
    }
  }
}
```

**Path semantics** (already implemented in `DefaultPathValidator`):
- Exact paths: `Q:/repos/botnexus` — allows access to this directory and all children
- Glob patterns: `Q:/repos/botnexus/docs/**` — wildcard matching via `FileSystemName.MatchesSimpleExpression`
- Home expansion: `~/Documents` — resolved to user profile path
- Relative paths: resolved relative to workspace
- Workspace is **always** accessible regardless of policy (hardcoded in `CanAccess`)
- Deny list takes priority over allow list (checked first in `CanAccess`)

### Phase 3: Documentation & Schema

- Update `docs/configuration.md` with `fileAccess` section under `AgentDefinitionConfig`
- Update JSON schema (`docs/botnexus-config.schema.json`) via `botnexus config schema`
- Add example to the configuration examples section

### Phase 4: Hot Reload Support (Optional)

`FileAccessPolicy` flows through `AgentDescriptor` -> `InProcessIsolationStrategy` -> `DefaultPathValidator`. Since the path validator is created per-agent-execution, config reloads that rebuild agent descriptors should automatically pick up policy changes. Verify this works end-to-end.

## Immediate Action: Nova's Config

Once Phase 1 lands, add to `~/.botnexus/config.json`:

```json
{
  "agents": {
    "nova": {
      "fileAccess": {
        "allowedReadPaths": [
  
