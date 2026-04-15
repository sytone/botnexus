---
id: bug-pathutils-ignores-file-access-policy
title: "PathUtils Enforces Workspace-Only Containment, Ignoring FileAccessPolicy"
type: research
created: 2026-04-14
author: nova
---

# Research: PathUtils vs IPathValidator Dual Validation

## Discovery

During the planning audit session (2026-04-14), Nova was granted file access to `Q:\repos\botnexus` via location management + file access policy. The `read`, `write`, and `ls` tools worked immediately. `grep` and `glob` failed on the same paths.

## Code Trace

### Path validation architecture

BotNexus has two path validation layers:

1. **`IPathValidator` / `DefaultPathValidator`** (in `BotNexus.Gateway/Security/`)
   - Created per-agent with the agent's `FileAccessPolicy`
   - Injected into tools via constructor
   - Validates against AllowedReadPaths, AllowedWritePaths, DeniedPaths
   - Workspace always allowed as fallback
   - Used correctly by: `ReadTool`, `WriteTool`, `EditTool`, `GlobTool`, `GrepTool`, `ListDirectoryTool`, `FileWatcherTool`

2. **`PathUtils.ResolvePath()`** (in `BotNexus.Tools/Utils/`)
   - Static utility method
   - Always checks `IsUnderRoot(resolved, root)` where root = workspace directory
   - Throws `InvalidOperationException` if outside workspace
   - Has NO awareness of `IPathValidator` or `FileAccessPolicy`
   - Used by: `GetGitIgnoredPaths` (called from GrepTool and GlobTool), `GlobTool` fallback path, `EditTool` display path

### The flow for grep on an allowed-but-out-of-workspace path

```
1. GrepTool.ExecuteAsync receives path="Q:\repos\botnexus\src\gateway\BotNexus.Gateway"
2. _validator.ValidateAndResolve(rawPath, Read) -> PASS (path is in AllowedReadPaths)
3. EnumerateCandidateFiles(targetPath, include) -> finds files
4. .Where(file => _validator?.CanRead(file) ?? true) -> all PASS
5. PathUtils.GetGitIgnoredPaths(candidateFiles, _workingDirectory)
   -> internally calls ResolvePath(file, workspace) for each file
   -> ResolvePath checks IsUnderRoot(file, workspace)
   -> file is NOT under workspace
   -> THROWS InvalidOperationException
```

Step 5 is where it breaks. The validated files are re-validated by `PathUtils` using a stricter, policy-unaware check.

### Why `read`/`write`/`ls` work

These tools use ONLY `IPathValidator` and never call `PathUtils.ResolvePath()` for already-validated paths. They resolve paths through the validator and operate directly on the result.

### `PathUtils.GetGitIgnoredPaths` is also semantically wrong

Even if the containment check didn't throw, `GetGitIgnoredPaths` passes the **workspace** as the git working directory:

```csharp
// In GrepTool.cs
var ignoredPaths = PathUtils.GetGitIgnoredPaths(candidateFiles, _workingDirectory);
```

When searching `Q:\repos\botnexus`, the gitignore rules should come from `Q:\repos\botnexus\.gitignore`, not from the agent workspace (which likely has no `.git` directory at all). So even without the crash, the gitignore filtering would be wrong for out-of-workspace paths.

### `PathUtils` origin

`PathUtils` was ported from the CodingAgent, where the working directory IS the project directory. The containment check made sense in that context — the agent should only operate within the repo it's working on. But when used by gateway tools that have a separate workspace + allowed paths, the assumption breaks.

## Affected areas

| Method | Used by | Problem |
|--------|---------|---------|
| `PathUtils.ResolvePath()` | `GetGitIgnoredPaths`, `GlobTool` fallback, `EditTool` fallback | Throws for out-of-workspace paths |
| `PathUtils.GetGitIgnoredPaths()` | `GrepTool`, `GlobTool` | Calls ResolvePath; also uses wrong git root |
| `PathUtils.GetRelativePath()` | `GrepTool` display, `EditTool` diff display | Returns `../../../...` paths for out-of-workspace files (ugly but doesn't crash) |
