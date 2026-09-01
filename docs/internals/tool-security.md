# Tool security model

> **Audience:** Developers building coding agents or custom tools who need to understand how BotNexus enforces security boundaries.
> **Prerequisites:** C#/.NET, familiarity with [agent events](agent-events.md) and [provider system](01-providers.md).
> **Source code:** `src/gateway/BotNexus.Tools/` (the built-in tools and `Utils/PathUtils.cs`),
> `src/gateway/BotNexus.Gateway/Security/` (path policy enforcement),
> `src/agent/BotNexus.Agent.Core/Hooks/` (the tool-call hook contracts).

## What you'll learn

1. Path containment (how tools are sandboxed)
2. Symlink resolution and escape prevention
3. The file access policy (allowed and denied paths)
4. File mutation queue (serialized file access)
5. Shell command safety
6. Audit logging
7. How to add safety hooks to a custom agent

---

## Security architecture overview

BotNexus enforces a defense-in-depth model with three layers:

```
┌─────────────────────────────────────────────────────┐
│  Layer 1: BeforeToolCall hook                       │
│  ● Policy interception before execution             │
│  ● Can Block, or return Indeterminate (fails closed)│
├─────────────────────────────────────────────────────┤
│  Layer 2: Tool-level enforcement                    │
│  ● IPathValidator.ValidateAndResolve in file tools  │
│  ● PathUtils.ResolvePath for workspace containment  │
│  ● FileMutationQueue for serialized writes          │
│  ● Output truncation (2000 lines, 50 KB)            │
│  ● Process timeout and tree kill                    │
├─────────────────────────────────────────────────────┤
│  Layer 3: AfterToolCall hook + IToolAuditSink       │
│  ● Result transformation and redaction              │
│  ● Durable tool-audit rows in session history       │
└─────────────────────────────────────────────────────┘
```

The hook contracts live in `src/agent/BotNexus.Agent.Core/Hooks/` and are invoked by
`src/agent/BotNexus.Agent.Core/Loop/ToolExecutor.cs`. A hook is a delegate on `AgentOptions`:

```csharp
// BeforeToolCallContext / BeforeToolCallResult, verbatim shapes:
public record BeforeToolCallContext(
    AssistantAgentMessage AssistantMessage,
    ToolCallContent ToolCallRequest,
    IReadOnlyDictionary<string, object?> ValidatedArgs,
    AgentContext AgentContext);

public record BeforeToolCallResult(bool Block, string? Reason = null)
{
    public bool IsIndeterminate { get; init; }
    public bool IsUnambiguousAllow => !Block && !IsIndeterminate;
    public static BeforeToolCallResult Indeterminate(string? reason = null);
}
```

Returning `null` means "no opinion, allow". `Block: true` is a positive deny. `Indeterminate` is the
*absence* of a decision — introduced by issue #2476 so an approval provider whose quorum failed
cannot have its silence coerced into an auto-approve. The executor fails closed on it.

---

## Path containment

### PathUtils.ResolvePath

`PathUtils.ResolvePath()` (`src/gateway/BotNexus.Tools/Utils/PathUtils.cs`) is the workspace
containment gate. Every file tool calls it before any file operation.

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
PathUtils.ResolvePath("notes/todo.txt", "/home/user/project");
// → "/home/user/project/notes/todo.txt" ✅

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
| `ShellTool` | Not path-based, but constrained by the working directory and timeouts |

Every one of those six tools also calls `IPathValidator.ValidateAndResolve` — that is the policy
gate, described next. `ResolvePath` answers "is this inside the workspace"; `ValidateAndResolve`
answers "does the configured policy permit this access".

---

## File access policy

### The policy record

Path permissions are carried by `FileAccessPolicy`
(`src/domain/BotNexus.Domain/Gateway/Security/FileAccessPolicy.cs`), which has exactly three members:

```csharp
public sealed record FileAccessPolicy
{
    public IReadOnlyList<string> AllowedReadPaths { get; init; } = [];
    public IReadOnlyList<string> AllowedWritePaths { get; init; } = [];
    public IReadOnlyList<string> DeniedPaths { get; init; } = [];
}
```

It is attached to an agent via `AgentDescriptor` and surfaced through the platform config
(`src/gateway/BotNexus.Gateway.Configuration/`). Workspace-relative entries are re-anchored onto the
agent's workspace path, so a policy keeps its meaning when copied onto a different agent.

### Enforcement

`DefaultPathValidator` (`src/gateway/BotNexus.Gateway/Security/DefaultPathValidator.cs`) implements
`IPathValidator`:

```csharp
public interface IPathValidator
{
    bool CanRead(string absolutePath);
    bool CanWrite(string absolutePath);
    string? ValidateAndResolve(string rawPath, FileAccessMode mode);
}
```

`ValidateAndResolve` returns `null` on refusal — there is no exception to swallow, so a caller that
ignores the result silently loses the check. Its two phases matter:

1. **Lexical resolution and policy check.** `Path.GetFullPath` collapses `..` segments, then
   `CanRead`/`CanWrite` tests the result against the allow lists and `DeniedPaths`.
2. **Link-following re-check.** `Path.GetFullPath` collapses `..` *lexically, without resolving
   links*, so `<symlink>/../<target>` can appear to stay inside the root while the OS resolves the
   link first and lands outside it. The validator re-walks the raw path segment by segment,
   following links as the OS would, and re-checks containment on the real final target.

When no policy is supplied (or the policy is empty), the validator operates in **workspace-only**
mode: the agent's workspace is the entire permitted surface.

Path comparison is platform-aware here — `OrdinalIgnoreCase` on Windows, `Ordinal` on Unix.

---

## File mutation queue

`FileMutationQueue` (`src/gateway/BotNexus.Tools/FileMutationQueue.cs`) prevents concurrent writes to
the same file. This is critical when `ToolExecutionMode.Parallel` is enabled.

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

`ShellTool` (`src/gateway/BotNexus.Tools/ShellTool.cs`) executes shell commands with multiple safety
layers.

### Platform detection

The shell is chosen by a `ShellPreference`, which also determines the tool's advertised name
(`shell` for `Pwsh`, `bash` otherwise):

| `ShellPreference` | Behaviour |
|---|---|
| `Auto` (default) | Tries bash first; falls back to pwsh with a warning |
| `Pwsh` | Uses PowerShell Core (`pwsh`) on all platforms |
| `Bash` | Always bash; falls back to pwsh with a warning if not found |

On Windows the bash probe checks `C:\Program Files\Git\bin\bash.exe` and the `(x86)` variant. If
neither is present, it falls back to pwsh and prefixes the output with a warning.

### Timeout enforcement

Every command has a configurable timeout (default: 600 seconds), clamped to a ceiling of
`ShellTool.DefaultMaxTimeoutSeconds` (3600):

```csharp
// Tool parameters include:
// "timeout" (optional, int) — seconds, default: 600, ceiling: 3600
```

When a command exceeds its timeout:
1. The tool captures partial output.
2. Appends `"[Output truncated at {timeout} seconds]"`.
3. **Kills the entire process tree** (not just the parent process).
4. Marks the result as `IsError = true`.

### Output truncation

Shell output is capped at `MaxOutputLines` and `MaxOutputBytes`:
- **2,000 lines maximum**
- **51,200 bytes (50 KB) maximum**

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

An `AfterToolCall` hook reads the exit code from `ShellToolDetails`, which is attached to
`AgentToolResult.Details`:

```csharp
if (context.Result.Details is ShellToolDetails details)
{
    // details.ExitCode, details.TimedOut, details.IsError
}
```

Note the tool's name is `shell` under `ShellPreference.Pwsh` and `bash` otherwise, so a hook that
matches on the name must handle both.

---

## Audit logging

Durable tool auditing is the responsibility of `IToolAuditSink`
(`src/gateway/BotNexus.Gateway/Audit/IToolAuditSink.cs`), introduced by issue #2614. It is the
**single execution-layer sink** both transports write through:

- **Streaming callers** use the start/result/incomplete renderers as events arrive.
- **Blocking `PromptAsync` callers** hand the settled `ToolInvocationRecord` timeline (the #2613
  shared record) to `ProjectBlockingRun`.

Both routes emit `MessageRole.Tool` session-history rows of the same shape, so removing the sink call
from either path removes the audit record entirely — which is exactly what the #2614 mutation tests
assert. Before #2614 the audit guarantee depended on which transport the caller happened to pick.

Write-ahead behaviour (so an interrupted invocation still leaves a record) lives in
`ToolAuditWriteAhead.cs` alongside it. A tool may be exempted from auditing with
`ToolAuditExemptAttribute`.

### Adding your own observability

The audit sink is a gateway concern and should not be replaced. To add per-call logging on top,
attach an `AfterToolCall` hook — it can observe, transform, filter or redact the result before it
reaches the LLM, and returning `null` leaves the result untouched:

```csharp
AfterToolCall = async (context, ct) =>
{
    await myLogger.LogToolCallAsync(
        context.ToolCallRequest.Name,
        context.IsError,
        context.Result);

    return null; // do not alter the result
};
```

---

## Tool output limits

All file-reading and searching tools enforce consistent output limits:

| Tool | Line limit | Size limit | Per-line limit |
|---|---|---|---|
| `ReadTool` | 2,000 lines | 50 KB | — |
| `GrepTool` | 100 matches (default), 1,000 max | 50 KB | 500 chars |
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

## How to add safety hooks to a custom agent

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

- [ ] **Call `IPathValidator.ValidateAndResolve`** for any file path argument, and treat a `null`
      return as a refusal — it is the only signal you get
- [ ] **Call `PathUtils.ResolvePath`** for workspace containment before using a path
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
- [Building your own agent](04-building-your-own.md) — how an agent wires safety hooks
- [Provider system](01-providers.md) — LLM communication layer
- [Glossary](05-glossary.md) — all key terms
