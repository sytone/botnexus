---
id: bug-pathutils-ignores-file-access-policy
title: "PathUtils Enforces Workspace-Only Containment, Ignoring FileAccessPolicy"
type: bug
priority: high
status: done
created: 2026-04-14
updated: 2026-04-14
author: nova
tags: [tools, security, path-validation, file-access, grep, glob]
related: [feature-agent-file-access-policy, feature-location-management]
---

# Bug: PathUtils Enforces Workspace-Only Containment, Ignoring FileAccessPolicy

**Type**: Bug
**Priority**: High (breaks grep and glob on all allowed-but-out-of-workspace paths)
**Status**: Delivered
**Author**: Nova

## Problem

`grep` and `glob` tools fail with path validation errors when operating on paths that are allowed by the agent's `FileAccessPolicy` but are outside the workspace directory. The `read`, `write`, `ls`, and `watch_file` tools work correctly on the same paths.

### Repro

Config grants Nova read access to `Q:\repos\botnexus`:

```json
{
  "agents": {
    "nova": {
      "fileAccess": {
        "allowedReadPaths": ["@botnexus-repo"],
        "allowedWritePaths": ["@botnexus-repo/docs/planning"]
      }
    }
  }
}
```

```
# These WORK:
read("Q:\repos\botnexus\src\gateway\BotNexus.Gateway\GatewayHost.cs")  -> OK
ls("Q:\repos\botnexus\src\gateway\BotNexus.Gateway\Security")          -> OK
write("Q:\repos\botnexus\docs\planning\test.md", "test")               -> OK

# These FAIL:
grep(pattern="FileAccess", path="Q:\repos\botnexus\src\gateway\BotNexus.Gateway")
  -> ERROR: Path 'Q:\repos\botnexus\src\gateway\BotNexus.Gateway\GatewayHost.cs' 
     resolves outside working directory 'C:\Users\jobullen\.botnexus\agents\nova\workspace'.

glob(pattern="*.cs", path="Q:\repos\botnexus\src\gateway\BotNexus.Gateway\Security")
  -> Same error
```

## Root Cause

There are **two path validation systems** that don't know about each other:

### 1. `IPathValidator` / `DefaultPathValidator` (policy-aware, correct)
- Knows about `FileAccessPolicy` (AllowedReadPaths, AllowedWritePaths, DeniedPaths)
- Workspace is always allowed
- Used by `read`, `write`, `edit`, `ls`, `watch_file`, and the *initial directory check* in `grep`/`glob`

### 2. `PathUtils.ResolvePath()` (workspace-only, wrong for multi-path)
- Hardcoded workspace containment check: `IsUnderRoot(resolved, root)` where `root` is always the workspace
- Throws `InvalidOperationException` if path is outside workspace
- Used by `PathUtils.GetGitIgnoredPaths()` which is called by `grep` and `glob` AFTER the `IPathValidator` check passes

### The conflict

`grep` and `glob` correctly validate the target directory via `IPathValidator`:
```csharp
// GrepTool.cs line ~161 - this passes
var targetPath = _validator?.ValidateAndResolve(rawPath, FileAccessMode.Read);
```

But then they call `PathUtils.GetGitIgnoredPaths()` which internally calls `PathUtils.ResolvePath()` for each file:
```csharp
// PathUtils.cs GetGitIgnoredPaths() -> ResolvePath()
var resolved = paths
    .Select(path => ResolvePath(path, workingDirectory, fs))  // <-- THROWS for out-of-workspace files
    .Distinct(PathComparer)
    .ToList();
```

`ResolvePath` enforces workspace-only containment and throws:
```csharp
if (!IsUnderRoot(resolved, root))
{
    throw new InvalidOperationException(
        $"Path '{relative}' resolves outside working directory '{root}'.");
}
```

### Affected tools

| Tool | Initial path check | Secondary path check | Bug? |
|------|--------------------|---------------------|------|
| `read` | `IPathValidator` | None (direct file read) | No |
| `write` | `IPathValidator` | None | No |
| `edit` | `IPathValidator` | `PathUtils.GetRelativePath` (display only) | **Yes** (minor - relative path display is wrong for out-of-workspace files but doesn't throw) |
| `ls` | `IPathValidator` | None | No |
| `grep` | `IPathValidator` | `PathUtils.GetGitIgnoredPaths` -> `PathUtils.ResolvePath` | **YES** |
| `glob` | `IPathValidator` | `PathUtils.GetGitIgnoredPaths` -> `PathUtils.ResolvePath` | **YES** |
| `watch_file` | `IPathValidator` | None | No |

## Fix

### Option A: Make `GetGitIgnoredPaths` skip containment check (recommended)

`GetGitIgnoredPaths` doesn't need `ResolvePath`'s containment check — the files are already validated by `IPathValidator`. It only needs path normalization and git-ignore filtering.

Add a `NormalizePath` method to `PathUtils` that does everything `ResolvePath` does EXCEPT the `IsUnderRoot` check:

```csharp
// New method - normalize without containment check
public static string NormalizePath(string path, IFileSystem? fileSystem = null)
{
    var resolved = Path.GetFullPath(SanitizePath(path));
    var resolvedFinal = ResolveFinalTargetPath(resolved, fileSystem);
    return resolved;
}
```

Then update `GetGitIgnoredPaths` to use `NormalizePath` instead of `ResolvePath`.

### Option B: Pass `IPathValidator` into `PathUtils` methods

Make `ResolvePath` aware of `IPathValidator` and use it instead of hardcoded workspace check when available. More invasive but eliminates the dual-validation problem entirely.

### Option C: Skip git-ignore for out-of-workspace paths

In `grep`/`glob`, detect when the target is outside workspace and skip `GetGitIgnoredPaths` entirely — the git repo root for those files is different from the workspace anyway, so the gitignore check is likely wrong.

**Recommendation**: Option A + Option C combined. Skip gitignore for out-of-workspace paths (C is the quick fix), and also fix `PathUtils` to not enforce containment in utility methods (A is the proper fix).

## Files to Change

| File | Change |
|------|--------|
| `src/tools/BotNexus.Tools/Utils/PathUtils.cs` | Add `NormalizePath` without containment; update `GetGitIgnoredPaths` |
| `src/tools/BotNexus.Tools/GrepTool.cs` | Skip gitignore for out-of-workspace paths, or pass normalized paths |
| `src/tools/BotNexus.Tools/GlobTool.cs` | Same as GrepTool |

## Testing

1. `grep(pattern="class", path="Q:\repos\botnexus\src")` — should return matches, not throw
2. `glob(pattern="*.cs", path="Q:\repos\botnexus\src\gateway")` — should return file list
3. `grep` on workspace path — should still work (regression check)
4. `glob` on workspace path — should still work
5. `grep` on denied path — should return access denied
6. `grep` on unallowed path — should return access denied
7. `grep` result paths should be displayed reasonably (relative to search root, not workspace)
