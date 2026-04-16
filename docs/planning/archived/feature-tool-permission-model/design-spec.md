# Design Spec: Per-Agent File System Permission Model

**Date:** 2026-04-11
**Author:** Leela (Lead/Architect)
**Status:** Done
**Design Item:** [feature-tool-permission-model](./design-item.md)
**Requested By:** Jon Bullen

---

## 1. Overview

### What's Broken

All file-system tools (`read`, `write`, `edit`, `glob`, `grep`, `ls`, `bash`) are locked to a single workspace directory via `PathUtils.ResolvePath()`, which enforces a strict containment boundary:

```
Path '{relative}' resolves outside working directory '{root}'.
```

This means:

1. **Agents can't read project files.** Nova can't read `Q:\repos\botnexus\` even when Jon explicitly asks her to review code there — the workspace is `~/.botnexus/agents/nova/workspace`.
2. **All-or-nothing.** Either locked to workspace or bypassed entirely (shell `cat` has no path check).
3. **No per-agent customization.** Every agent gets the same `_workingDirectory` passed through `DefaultAgentToolFactory.CreateTools(workspacePath)`.
4. **Read/write parity.** An agent that should only _read_ a repo can also _write_ to it — there's no access mode distinction.

### What We're Building

A configurable permission model where:

- Each agent declares which paths it can read and which it can write
- Deny rules override allow rules
- Default policy (no config) restricts to workspace only (preserving current behavior)
- A single `IPathValidator` interface mediates all file-tool access checks

---

## 2. Permission Model

### FileAccessPolicy

```csharp
namespace BotNexus.Gateway.Abstractions.Security;

/// <summary>
/// Per-agent file system access policy. Deny overrides allow.
/// </summary>
public sealed record FileAccessPolicy
{
    /// <summary>
    /// Directories the agent can read from. Supports absolute paths and ~ expansion.
    /// Default: agent workspace only.
    /// </summary>
    public IReadOnlyList<string> AllowedReadPaths { get; init; } = [];

    /// <summary>
    /// Directories the agent can write to. Supports absolute paths and ~ expansion.
    /// Default: agent workspace only.
    /// </summary>
    public IReadOnlyList<string> AllowedWritePaths { get; init; } = [];

    /// <summary>
    /// Paths explicitly denied regardless of allow rules. Takes precedence.
    /// Supports file paths and directory paths.
    /// </summary>
    public IReadOnlyList<string> DeniedPaths { get; init; } = [];
}
```

### Resolution Rules

1. **Relative paths** in config are resolved from the agent workspace directory.
2. **`~`** is expanded to `Environment.GetFolderPath(UserProfile)`.
3. **Deny overrides allow** — if a path matches both an allow and deny rule, access is denied.
4. **Default policy** — when `FileAccessPolicy` is null/empty, the agent can read and write only within its workspace (current behavior unchanged).
5. **Directory containment** — `AllowedReadPaths: ["Q:\\repos\\botnexus"]` grants read access to all files under that directory tree.
6. **Glob pattern support** — paths containing `*` or `?` wildcards are treated as glob patterns and matched using `FileSystemName.MatchesSimpleExpression`. Non-glob paths use the existing directory-prefix matching. Both allow and deny paths support globs.

### Glob Pattern Support

Glob patterns enable flexible path matching in `AllowedReadPaths`, `AllowedWritePaths`, and `DeniedPaths`.

#### Pattern Syntax

| Pattern | Meaning | Example Match |
|---------|---------|---------------|
| `Q:\repos\*` | All files/dirs under repos | `Q:\repos\botnexus\file.cs` |
| `Q:\repos\botnexus\src\**` | All files under src recursively | `Q:\repos\botnexus\src\gateway\Program.cs` |
| `C:\Users\*\.botnexus\**` | Any user's botnexus directory | `C:\Users\jon\.botnexus\config.json` |
| `*.env` | Any `.env` file anywhere | `Q:\repos\botnexus\.env` |
| `**\secrets\**` | Any secrets directory at any depth | `Q:\repos\project\secrets\key.txt` |

#### Matching Rules

1. `*` matches any characters within a single path segment.
2. `**` matches any characters across multiple path segments (recursive).
3. Glob matching uses `FileSystemName.MatchesSimpleExpression` (.NET built-in, `System.IO.Enumeration`) — no extra NuGet packages required.
4. Glob patterns are identified by containing `*` or `?` characters.
5. Non-glob paths use the existing directory-prefix matching.
6. Both allow and deny paths support globs.
7. Glob patterns that are not rooted are stored as-is (not resolved relative to workspace), enabling patterns like `*.env` to match files at any location.
8. Path comparisons in glob matching normalize to forward slashes internally to avoid backslash escape conflicts with `MatchesSimpleExpression`.

#### Example Configuration

```json
{
  "fileAccess": {
    "allowedReadPaths": [
      "Q:\\repos\\botnexus\\src\\**",
      "Q:\\repos\\**\\*.cs"
    ],
    "allowedWritePaths": [
      "~\\.botnexus\\agents\\nova\\workspace"
    ],
    "deniedPaths": [
      "**\\*.env",
      "**\\secrets\\**"
    ]
  }
}
```

### Access Check Flow

```
Agent requests file operation (path, mode)
  → PathValidator.ValidateAndResolve(path, mode)
    → Resolve to absolute path (expand ~, resolve relative)
    → Check DeniedPaths — if match → return null (denied)
    → Check AllowedReadPaths / AllowedWritePaths — if match → return resolved path
    → Fallback: check workspace containment (default behavior)
    → No match → return null (denied)
```

---

## 3. IPathValidator Interface

```csharp
namespace BotNexus.Gateway.Abstractions.Security;

public enum FileAccessMode
{
    Read,
    Write
}

/// <summary>
/// Validates and resolves file paths against an agent's file access policy.
/// </summary>
public interface IPathValidator
{
    /// <summary>Returns true if the agent can read this absolute path.</summary>
    bool CanRead(string absolutePath);

    /// <summary>Returns true if the agent can write this absolute path.</summary>
    bool CanWrite(string absolutePath);

    /// <summary>
    /// Resolves a raw tool path to an absolute path and validates access.
    /// Returns the resolved absolute path, or null if access is denied.
    /// </summary>
    string? ValidateAndResolve(string path, FileAccessMode mode);
}
```

### DefaultPathValidator

```csharp
namespace BotNexus.Tools.Security;

/// <summary>
/// Default implementation that enforces FileAccessPolicy rules.
/// When no policy is configured, falls back to workspace-only containment.
/// </summary>
public sealed class DefaultPathValidator : IPathValidator
{
    private readonly string _workspacePath;
    private readonly FileAccessPolicy _policy;
    private readonly IFileSystem _fileSystem;

    // Resolved absolute paths (computed once at construction)
    private readonly IReadOnlyList<string> _resolvedReadPaths;
    private readonly IReadOnlyList<string> _resolvedWritePaths;
    private readonly IReadOnlyList<string> _resolvedDeniedPaths;

    public DefaultPathValidator(
        string workspacePath,
        FileAccessPolicy? policy,
        IFileSystem? fileSystem = null) { ... }

    public bool CanRead(string absolutePath)
        => !IsDenied(absolutePath) && IsAllowedForMode(absolutePath, FileAccessMode.Read);

    public bool CanWrite(string absolutePath)
        => !IsDenied(absolutePath) && IsAllowedForMode(absolutePath, FileAccessMode.Write);

    public string? ValidateAndResolve(string path, FileAccessMode mode)
    {
        var resolved = ResolvePath(path);  // expand ~, resolve relative
        if (resolved is null) return null;
        return mode == FileAccessMode.Read ? (CanRead(resolved) ? resolved : null)
                                           : (CanWrite(resolved) ? resolved : null);
    }
}
```

**Key behaviors:**

- `IsDenied()` checks if the resolved path starts with any denied path (directory containment) or equals a denied file path.
- `IsAllowedForMode()` checks the appropriate allow list, then falls back to workspace containment.
- All path comparisons use `StringComparison.OrdinalIgnoreCase` on Windows, `Ordinal` on Unix — matching existing `PathUtils.PathComparer`.
- Symlink resolution reuses existing `PathUtils.ResolveFinalTargetPath` logic.

---

## 4. Tool Integration

### Pattern: Constructor Injection

Each file tool receives `IPathValidator` instead of (or in addition to) the raw `_workingDirectory`:

```csharp
// Before
public ReadTool(string workingDirectory, IFileSystem? fileSystem = null)

// After
public ReadTool(string workingDirectory, IPathValidator pathValidator, IFileSystem? fileSystem = null)
```

### Per-Tool Changes

| Tool | Access Mode | Integration Point |
|------|-------------|-------------------|
| `ReadTool` | Read | Replace `PathUtils.ResolvePath()` call with `_pathValidator.ValidateAndResolve(path, Read)` |
| `WriteTool` | Write | Replace `PathUtils.ResolvePath()` call with `_pathValidator.ValidateAndResolve(path, Write)` |
| `EditTool` | Write | Replace `PathUtils.ResolvePath()` call with `_pathValidator.ValidateAndResolve(path, Write)` |
| `GlobTool` | Read | Validate base directory, post-filter results through `_pathValidator.CanRead()` |
| `GrepTool` | Read | Validate target path, filter candidate files through `_pathValidator.CanRead()` |
| `ListDirectoryTool` | Read | Replace `PathUtils.ResolvePath()` call with `_pathValidator.ValidateAndResolve(path, Read)` |
| `FileWatcherTool` | Read | Validate watch target through `_pathValidator.CanRead()` |

### Error messages

When `ValidateAndResolve` returns null, tools throw:

```
Access denied: agent does not have {read/write} permission for '{path}'.
```

### DefaultAgentToolFactory Changes

```csharp
public sealed class DefaultAgentToolFactory : IAgentToolFactory
{
    public IReadOnlyList<IAgentTool> CreateTools(string workingDirectory, FileAccessPolicy? fileAccessPolicy = null)
    {
        var resolved = Path.GetFullPath(workingDirectory);
        var fileSystem = new FileSystem();
        var validator = new DefaultPathValidator(resolved, fileAccessPolicy, fileSystem);
        return
        [
            new ReadTool(resolved, validator, fileSystem),
            new WriteTool(resolved, validator, fileSystem),
            new EditTool(resolved, validator, fileSystem),
            new ShellTool(workingDirectory: resolved),
            new ListDirectoryTool(resolved, validator, fileSystem),
            new GrepTool(resolved, validator, fileSystem),
            new GlobTool(resolved, validator, fileSystem)
        ];
    }
}
```

The `IAgentToolFactory` interface gains an optional `FileAccessPolicy` parameter. Existing callers that pass only `workingDirectory` get the default workspace-only policy — **zero breaking changes**.

---

## 5. Configuration

### AgentDescriptor Addition

```csharp
public sealed record AgentDescriptor
{
    // ... existing properties ...

    /// <summary>
    /// File system access policy for this agent. Null means workspace-only (default).
    /// </summary>
    public FileAccessPolicy? FileAccessPolicy { get; init; }
}
```

### Config JSON Format

```json
{
  "agents": {
    "nova": {
      "agentId": "nova",
      "modelId": "claude-sonnet-4-20250514",
      "apiProvider": "copilot",
      "fileAccess": {
        "allowedReadPaths": [
          "~/.botnexus/agents/nova/workspace",
          "Q:\\repos\\botnexus"
        ],
        "allowedWritePaths": [
          "~/.botnexus/agents/nova/workspace"
        ],
        "deniedPaths": [
          "Q:\\repos\\botnexus\\.env",
          "Q:\\repos\\botnexus\\secrets"
        ]
      }
    }
  }
}
```

### InProcessIsolationStrategy Wiring

```csharp
// In CreateAsync():
var workspacePath = _workspaceManager.GetWorkspacePath(descriptor.AgentId);
var workspaceTools = _toolFactory.CreateTools(workspacePath, descriptor.FileAccessPolicy);
```

---

## 6. Shell Tool — Special Case

The shell tool (`bash`) cannot enforce path-level access control — any shell command can access arbitrary files. Current behavior: shell sets `WorkingDirectory` but has no path validation at all.

**v1 approach (pragmatic):**

1. **Set working directory** to agent workspace (already done).
2. **Inject environment variables** so shell scripts can discover allowed paths:
   - `BOTNEXUS_WORKSPACE` — agent workspace path
   - `BOTNEXUS_ALLOWED_READ` — semicolon-delimited allowed read paths
   - `BOTNEXUS_ALLOWED_WRITE` — semicolon-delimited allowed write paths
3. **Log shell commands** for audit trail.
4. **Document the limitation** — shell is inherently unsandboxable without OS-level isolation (containers, seccomp, etc.).

**Future (v2):** Consider container isolation strategy for agents that need strict shell sandboxing.

---

## 7. Work Breakdown

### Wave 1 — Abstractions + Config (Farnsworth)

| Task | Location |
|------|----------|
| Create `FileAccessPolicy` record | `BotNexus.Gateway.Abstractions/Security/` |
| Create `FileAccessMode` enum | `BotNexus.Gateway.Abstractions/Security/` |
| Create `IPathValidator` interface | `BotNexus.Gateway.Abstractions/Security/` |
| Create `DefaultPathValidator` implementation | `BotNexus.Tools/Security/` |
| Add `FileAccessPolicy?` to `AgentDescriptor` | `BotNexus.Gateway.Abstractions/Models/` |
| Deserialize `fileAccess` from agent config JSON | Config loading pipeline |

### Wave 2 — Tool Integration (Bender)

| Task | Location |
|------|----------|
| Add `IPathValidator` constructor param to all file tools | `BotNexus.Tools/` |
| Replace `PathUtils.ResolvePath()` calls with validator | Each tool's `ExecuteAsync` |
| Update `DefaultAgentToolFactory` to accept + pass policy | `BotNexus.Gateway/Agents/` |
| Update `InProcessIsolationStrategy.CreateAsync()` wiring | `BotNexus.Gateway/Isolation/` |
| Shell tool: inject env vars for allowed paths | `BotNexus.Tools/ShellTool.cs` |

### Wave 3 — Tests (Hermes)

| Task | Location |
|------|----------|
| `DefaultPathValidator` unit tests (allow, deny, defaults, ~, relative) | `BotNexus.Tools.Tests/` |
| Tool integration tests (each tool respects validator) | `BotNexus.Tools.Tests/` |
| Config deserialization tests (FileAccessPolicy from JSON) | `BotNexus.Gateway.Tests/` |
| End-to-end: agent with policy reads allowed path, denied path | `BotNexus.Gateway.Tests/` |

---

## 8. Migration & Backward Compatibility

- **No breaking changes.** `FileAccessPolicy` is nullable; null means workspace-only (current behavior).
- **Existing tools** continue to work with `_workingDirectory` as fallback when no validator is injected.
- **Config is optional.** Agents without `fileAccess` config behave exactly as today.
- **`PathUtils.ResolvePath()`** is preserved for internal use; tools migrate to `IPathValidator` for access checks.

---

## 9. Security Considerations

1. **Deny overrides allow** — always. No configuration can override a deny rule.
2. **Symlink traversal** — resolved paths are checked after symlink resolution (reuses existing `ResolveFinalTargetPath`).
3. **Path normalization** — all comparisons use `Path.GetFullPath()` to prevent `..` traversal bypasses.
4. **Shell remains unvalidated** — documented limitation. Agents with shell access can bypass file permissions.
5. **Glob pattern auditing** — glob patterns (`*`, `?`) in allow lists should be reviewed carefully to avoid over-permissive access. Patterns like `*` in `AllowedWritePaths` could grant write access to unintended locations.

---

## 10. Open Questions

1. ~~Wildcard support (`Q:\repos\*`)?~~ **Resolved** — glob patterns are now supported using `FileSystemName.MatchesSimpleExpression`.
2. Should `FileAccessPolicy` support network path access? **Deferred** — not needed for current use cases.
3. Should we add `FileWatcherTool` to Wave 2? **Yes** — it watches files and should respect read permissions.
