# Tool security model

> **Audience:** Developers building coding agents or custom tools who need to understand how BotNexus enforces security boundaries.
> **Prerequisites:** C#/.NET, familiarity with [agent events](agent-events.md) and [provider architecture](providers.md).
> **Source code:** `src/coding-agent/BotNexus.CodingAgent/Utils/`, `Hooks/`, and `Tools/`

## What you'll learn

1. Path containment (how tools are sandboxed)
2. Symlink resolution and escape prevention
3. Blocked paths and blocked commands
4. File mutation queue (serialized file access)
5. Shell command safety
6. Audit logging
7. How to add safety hooks to a custom coding agent

---

## Security architecture overview

BotNexus enforces a defense-in-depth model with three layers:

```
┌─────────────────────────────────────────────────────┐
│  Layer 1: SafetyHooks (BeforeToolCall)              │
│  ● Path containment validation                      │
│  ● Blocked path enforcement                         │
│  ● Shell command filtering                          │
│  ● Payload size limits                              │
├─────────────────────────────────────────────────────┤
│  Layer 2: Tool-level enforcement                    │
│  ● PathUtils.ResolvePath in every file tool         │
│  ● FileMutationQueue for serialized writes          │
│  ● Output truncation (2000 lines, 50 KB)           │
│  ● Process timeout and tree kill                    │
├─────────────────────────────────────────────────────┤
│  Layer 3: AuditHooks (AfterToolCall)                │
│  ● Timing and call counting                         │
│  ● Console logging of every tool execution          │
└─────────────────────────────────────────────────────┘
```

The hook chain in `CodingAgent.CreateAsync` wires these together:

```csharp
// Simplified from CodingAgent.cs
BeforeToolCall = async (context, ct) =>
{
    // 1. SafetyHooks — can block the call
    var safetyResult = await SafetyHooks.ValidateAsync(context, config);
    if (safetyResult is { Block: true }) return safetyResult;

    // 2. Extension hooks — can observe but don't block writes
    var extensionResult = await extensionRunner.OnToolCallAsync(context, ct);
    return extensionResult;
};

AfterToolCall = async (context, ct) =>
{
    // 3. AuditHooks — logs and times the call
    var auditResult = await auditHooks.AuditAsync(context);

    // 4. Extension post-hooks
    var extensionResult = await extensionRunner.OnToolResultAsync(context, ct);

    return MergeAfterResults(auditResult, extensionResult);
};
```

---

## Path containment

### PathUtils.ResolvePath

`PathUtils.ResolvePath()` (`Utils/PathUtils.cs`) is the core security gate. Every file tool calls it before any file operation.

```csharp
public static string ResolvePath(string relative, string workingDirectory)
```

**What it does:**

1. Validates that neither input is null or empty.
2. Normalizes the relative path (collapses `./` and `..` segments).
3. Resolves to an absolute path using `Path.GetFullPath`.
4. **Checks that the resolved path starts with `workingDirectory`** — if not, throws `InvalidOperationException`.
5. **Resolves symlinks** — walks each path segment, resolving directory and file symlinks to their final targets via `ResolveFinalTargetPath`.
6. **Checks the symlink target is under the root** — if the resolved symlink target escapes the working directory, throws `UnauthorizedAccessException`.

**Example:**

```csharp
// Working directory: /home/user/project
PathUtils.ResolvePath("src/main.cs", "/home/user/project");
// → "/home/user/project/src/main.cs" ✅

PathUtils.ResolvePath("../../etc/passwd", "/home/user/project");
// → throws InvalidOperationException ❌ (escapes working directory)

PathUtils.ResolvePath("/etc/passwd", "/home/user/project");
// → throws InvalidOperationException ❌ (absolute path outside root)
```

### Platform-aware comparison

Path containment checks use **case-insensitive comparison (`OrdinalIgnoreCase`) on all platforms**. The `PathComparer` field (used for gitignore path matching) is platform-aware — case-insensitive on Windows and case-sensitive on Unix — but the `IsUnderRoot` containment check always uses `OrdinalIgnoreCase`:

```csharp
// IsUnderRoot always uses:
// StringComparison.OrdinalIgnoreCase (all platforms)
//
// PathComparer (gitignore matching) uses:
// Windows: StringComparer.OrdinalIgnoreCase
// Unix:    StringComparer.Ordinal
```

### SanitizePath

`PathUtils.SanitizePath()` normalizes paths before resolution:

```csharp
public static string SanitizePath(string path)
```

- Collapses `./` segments
- Normalizes directory separators to the OS-native separator (`\` on Windows, `/` on Unix)
- **Throws `InvalidOperationException` if `..` segments would escape the root**

### Every file tool uses ResolvePath

All built-in tools validate paths through `ResolvePath` before any operation:

| Tool | Path validation |
|---|---|
| `ReadTool` | `ResolvePath(args["path"], workingDir)` before reading |
| `WriteTool` | `ResolvePath(args["path"], workingDir)` before writing |
| `EditTool` | `ResolvePath(args["path"], workingDir)` before editing |
| `GrepTool` | `ResolvePath(args["path"], workingDir)` before searching |
| `GlobTool` | `ResolvePath(args["path"], workingDir)` before globbing |
| `ListDirectoryTool` | `ResolvePath(args["path"], workingDir)` before listing |
| `ShellTool` | Not path-based, but constrained by command filtering |

---

## Blocked paths and blocked commands

### Blocked paths

`CodingAgentConfig.BlockedPaths` defines paths that tools cannot write to or edit, even if they're within the working directory:

```json
// .botnexus-agent/config.json
{
  "blockedPaths": [
    ".env",
    "secrets/",
    "/etc/passwd"
  ]
}
```

Paths can be:
- **Relative** — resolved against the working directory (e.g., `.env` → `/project/.env`)
- **Absolute** — used as-is (e.g., `/etc/passwd`)
- **Directory prefixes** — blocks everything under that directory (e.g., `secrets/` blocks `secrets/api-key.txt`)

**Enforcement** in `SafetyHooks.ValidateAsync`:

```csharp
// Simplified from SafetyHooks.cs
public async Task<BeforeToolCallResult?> ValidateAsync(
    BeforeToolCallContext context, CodingAgentConfig config)
{
    if (context.ToolCallRequest.Name is "write" or "edit")
    {
        var path = context.ValidatedArgs["path"]?.ToString();
        var resolved = PathUtils.ResolvePath(path, config.WorkingDirectory());

        // Check blocked paths
        if (IsBlockedPath(resolved, config))
            return new BeforeToolCallResult(Block: true,
                Reason: $"Path is blocked by configuration: {path}");

        // Check escape
        // (redundant with ResolvePath, but defense-in-depth)
    }
    return null;
}
```

The `IsBlockedPath` method compares the resolved absolute path against each entry in `BlockedPaths` using a `StartsWith` check with directory boundary awareness.

### Blocked commands

`SafetyHooks` blocks hardcoded dangerous shell patterns:

```csharp
// Hardcoded blocked patterns (case-insensitive):
"rm -rf /"
"format"
"del /s /q"
```

### Allowed commands whitelist

`CodingAgentConfig.AllowedCommands` provides an optional whitelist. When set (non-empty), only commands whose prefix matches an entry are permitted:

```json
// .botnexus-agent/config.json
{
  "allowedCommands": [
    "dotnet",
    "git",
    "npm"
  ]
}
```

If `AllowedCommands` is empty (the default), all commands are allowed except the hardcoded blocked patterns.

### Payload size limits

`SafetyHooks` warns (but doesn't block) when a write tool payload exceeds 1 MB.

---

## File mutation queue

`FileMutationQueue` (`Tools/FileMutationQueue.cs`) prevents concurrent writes to the same file. This is critical when `ToolExecutionMode.Parallel` is enabled.

```csharp
public sealed class FileMutationQueue
{
    public static FileMutationQueue Shared { get; }

    public Task<T> WithFileLockAsync<T>(string path, Func<Task<T>> operation);
}
```

**How it works:**

- Maintains a `ConcurrentDictionary<string, SemaphoreSlim>` with per-path semaphores.
- Before a file operation, the tool acquires the semaphore for that path.
- The operation runs exclusively — no other tool can modify the same file simultaneously.
- After completion, the semaphore is released.

**Usage in WriteTool:**

```csharp
// Simplified from WriteTool.cs
public async Task<AgentToolResult> ExecuteAsync(
    string toolCallId, IReadOnlyDictionary<string, object?> arguments, ...)
{
    var path = PathUtils.ResolvePath(arguments["path"]!.ToString()!, _workingDirectory);
    var content = arguments["content"]!.ToString()!;

    return await FileMutationQueue.Shared.WithFileLockAsync(path, async () =>
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, cancellationToken);
        return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, $"Wrote {path}")]);
    });
}
```

**EditTool** uses the same queue to prevent race conditions during read-modify-write cycles.

---

## Shell command safety

`ShellTool` (`Tools/ShellTool.cs`) executes shell commands with multiple safety layers.

### Platform detection

```csharp
// Windows: prefers Git Bash, falls back to PowerShell with warning
// Unix/macOS: uses /bin/bash
```

On Windows, the tool checks for Git Bash at `C:\Program Files\Git\bin\bash.exe`. If unavailable, it falls back to PowerShell and logs a warning.

### Timeout enforcement

Every command has a configurable timeout (default: 120 seconds):

```csharp
// Tool parameters include:
// "timeout" (optional, int) — seconds, default: 120
```

When a command exceeds its timeout:
1. The tool captures partial output.
2. Appends `"[Output truncated at {timeout} seconds]"`.
3. **Kills the entire process tree** (not just the parent process).
4. Marks the result as `IsError = true`.

### Output truncation

Shell output is capped at:
- **2,000 lines maximum**
- **50 KB maximum**

If output exceeds these limits, it is truncated with a notification appended.

### Process tree management

When a shell command is killed (timeout or abort), the tool kills the entire process tree to prevent orphaned child processes. The result includes a `ShellToolDetails` record:

```csharp
public sealed record ShellToolDetails(
    int ExitCode,
    bool TimedOut,
    bool IsError
);
```

This is attached to the `AgentToolResult.Details` field for inspection by hooks and logging.

### Exit code capture

The `AfterToolCall` hook in `CodingAgent.cs` reads the exit code from `ShellToolDetails` for audit logging:

```csharp
// Simplified from CodingAgent.ExecuteAfterHookAsync
if (context.ToolCallRequest.Name == "bash" && context.Result.Details is ShellToolDetails details)
{
    // Exit code available for audit
}
```

---

## Audit logging

`AuditHooks` (`Hooks/AuditHooks.cs`) logs every tool execution for observability.

### How it works

```csharp
public sealed class AuditHooks
{
    public AuditHooks(bool verbose = true);

    public bool Verbose { get; }
    public int ToolCallCount { get; }  // Atomically incremented

    public void RegisterToolCallStart(string toolCallId);
    public Task<AfterToolCallResult?> AuditAsync(AfterToolCallContext context);
}
```

1. **Before execution:** `RegisterToolCallStart(toolCallId)` records the start time in a `ConcurrentDictionary`.
2. **After execution:** `AuditAsync(context)` calculates duration, increments the call counter, and logs.

### Log format

When `Verbose` is enabled, each tool call produces a console log:

```
[audit] tool=read status=succeeded durationMs=45 calls=3
[audit] tool=bash status=failed durationMs=5023 calls=4
```

### Integrating with custom logging

Since `AuditHooks` is wired as an `AfterToolCall` hook, you can replace or extend it:

```csharp
AfterToolCall = async (context, ct) =>
{
    // Your custom logging
    await myLogger.LogToolCallAsync(
        context.ToolCallRequest.Name,
        context.IsError,
        context.Result);

    // Still run the built-in audit
    return await auditHooks.AuditAsync(context);
};
```

---

## Tool output limits

All file-reading and searching tools enforce consistent output limits:

| Tool | Line limit | Size limit | Per-line limit |
|---|---|---|---|
| `ReadTool` | 2,000 lines | 50 KB | — |
| `GrepTool` | 100 matches | 50 KB | 500 chars |
| `GlobTool` | 1,000 matches | — | — |
| `ListDirectoryTool` | 500 entries | 50 KB | — |
| `ShellTool` | 2,000 lines | 50 KB | — |

These limits prevent the LLM context from being flooded with large tool outputs.

### ReadTool specifics

- Supports `offset` (1-indexed start line) and `limit` (max lines) parameters.
- Detects image files (PNG, JPEG, GIF, WebP) via magic bytes and returns base64-encoded data URIs.
- Directory paths trigger recursive listing (max depth 2).

### GrepTool specifics

- Excludes `.git/` directory.
- Filters `.gitignore`'d files via `PathUtils.GetGitIgnoredPaths`.
- Skips binary files (null-byte detection in first 4 KB).
- Continues silently on I/O errors.
- Appends warnings for truncated results.

---

## How to add safety hooks to a custom coding agent

### Step 1: Define your safety policy

```csharp
public sealed class MySecurityPolicy
{
    private readonly string _workingDirectory;
    private readonly HashSet<string> _blockedPatterns;

    public MySecurityPolicy(string workingDirectory, IEnumerable<string> blockedPatterns)
    {
        _workingDirectory = workingDirectory;
        _blockedPatterns = new HashSet<string>(blockedPatterns, StringComparer.OrdinalIgnoreCase);
    }

    public Task<BeforeToolCallResult?> ValidateAsync(
        BeforeToolCallContext context, CancellationToken ct)
    {
        switch (context.ToolCallRequest.Name)
        {
            case "write" or "edit":
                return ValidateFileAccess(context);
            case "bash":
                return ValidateShellCommand(context);
            default:
                return Task.FromResult<BeforeToolCallResult?>(null);
        }
    }

    private Task<BeforeToolCallResult?> ValidateFileAccess(BeforeToolCallContext context)
    {
        var path = context.ValidatedArgs["path"]?.ToString();
        if (string.IsNullOrEmpty(path))
            return Task.FromResult<BeforeToolCallResult?>(
                new BeforeToolCallResult(Block: true, Reason: "Path is required"));

        try
        {
            // PathUtils.ResolvePath throws if path escapes working directory
            var resolved = PathUtils.ResolvePath(path, _workingDirectory);

            // Check custom blocked paths
            foreach (var pattern in _blockedPatterns)
            {
                if (resolved.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult<BeforeToolCallResult?>(
                        new BeforeToolCallResult(Block: true, Reason: $"Blocked path: {pattern}"));
            }
        }
        catch (InvalidOperationException ex)
        {
            return Task.FromResult<BeforeToolCallResult?>(
                new BeforeToolCallResult(Block: true, Reason: ex.Message));
        }

        return Task.FromResult<BeforeToolCallResult?>(null);
    }

    private Task<BeforeToolCallResult?> ValidateShellCommand(BeforeToolCallContext context)
    {
        var command = context.ValidatedArgs["command"]?.ToString() ?? "";

        if (command.Contains("rm -rf", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult<BeforeToolCallResult?>(
                new BeforeToolCallResult(Block: true, Reason: "Destructive command blocked"));

        return Task.FromResult<BeforeToolCallResult?>(null);
    }
}
```

### Step 2: Define your audit logger

```csharp
public sealed class MyAuditLogger
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _startTimes = new();

    public void RecordStart(string toolCallId)
    {
        _startTimes[toolCallId] = DateTimeOffset.UtcNow;
    }

    public Task<AfterToolCallResult?> AuditAsync(AfterToolCallContext context, CancellationToken ct)
    {
        var duration = _startTimes.TryRemove(context.ToolCallRequest.Id, out var start)
            ? (DateTimeOffset.UtcNow - start).TotalMilliseconds
            : 0;

        Console.WriteLine(
            $"[audit] tool={context.ToolCallRequest.Name} " +
            $"status={(context.IsError ? "failed" : "ok")} " +
            $"duration={duration:F0}ms");

        return Task.FromResult<AfterToolCallResult?>(null);
    }
}
```

### Step 3: Wire hooks into AgentOptions

```csharp
var securityPolicy = new MySecurityPolicy(workingDirectory, ["secrets/", ".env"]);
var auditLogger = new MyAuditLogger();

var agent = new Agent(new AgentOptions
{
    Model = model,
    LlmClient = llmClient,
    GetApiKey = async (provider, ct) => apiKey,
    InitialState = new AgentInitialState
    {
        SystemPrompt = "You are a coding assistant.",
        Tools = [new ReadTool(workingDir), new WriteTool(workingDir), new ShellTool(workingDir)]
    },
    GenerationSettings = new SimpleStreamOptions { MaxTokens = 8192 },

    BeforeToolCall = async (context, ct) =>
    {
        return await securityPolicy.ValidateAsync(context, ct);
    },

    AfterToolCall = async (context, ct) =>
    {
        return await auditLogger.AuditAsync(context, ct);
    }
});
```

---

## Security checklist for custom tools

When implementing a custom `IAgentTool`:

- [ ] **Call `PathUtils.ResolvePath`** for any file path argument before using it
- [ ] **Use `FileMutationQueue.Shared`** for any file write operation
- [ ] **Set process timeouts** for any subprocess execution
- [ ] **Kill the process tree** when cancelling subprocesses, not just the parent
- [ ] **Truncate output** to prevent context flooding (aim for ≤50 KB)
- [ ] **Never trust LLM-provided paths** — always validate and resolve them
- [ ] **Skip binary files** when searching file contents
- [ ] **Filter `.gitignore`'d paths** to avoid leaking ignored content

---

## Further reading

- [Agent event system](agent-events.md) — hook system and event lifecycle
- [Building a coding agent](building-a-coding-agent.md) — how CodingAgent wires safety
- [Provider architecture](providers.md) — LLM communication layer
- [Glossary](05-glossary.md) — all key terms
