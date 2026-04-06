# 12 — Port Audit Phase 4 Changelog

Port Audit Phase 4 (completed with 17 commits) brings refactoring, stability improvements, and new stop reason mappings to BotNexus. This document summarizes the changes, explains why they matter, and provides before/after examples for the most impactful updates.

> **Audience:** Developers maintaining providers, agents, or extensions. Understand what changed and how to update your code.

---

## What changed — at a glance

| Layer | Component | Change | Impact |
|-------|-----------|--------|--------|
| **Provider** | `AnthropicProvider` | Refactored into specialized components | Better maintainability, no API change |
| **Provider** | `StopReason` enum | Added `Refusal`, `Sensitive` mappings | Better safety filter observability |
| **Provider** | Model identity | `ModelsAreEqual` now compares Id+Provider only | Regional failover support |
| **Agent Core** | `Agent` class | New `HasQueuedMessages` property | Queue introspection for UI/logging |
| **Agent Core** | `AgentOptions` | `TransformContext` now optional | Simplified configuration |
| **Agent Core** | `AgentOptions` | `ConvertToLlm` auto-defaults | Simplified configuration |
| **Agent Core** | `StreamAccumulator` | `MessageStartEvent` deferred from state | Prevents duplicate messages on replay |
| **Agent Core** | Listeners | Exceptions logged via `OnDiagnostic` | Better error observability |
| **Coding Agent** | `EditTool` | DiffPlex integration for better diffs | Improved fuzzy match context |
| **Coding Agent** | `ShellTool` | Git Bash detection on Windows | Better shell compatibility |
| **Coding Agent** | `ShellTool` | Byte limits aligned to 50*1024 | TypeScript parity, better token budgeting |

---

## Provider Layer

### AnthropicProvider refactoring

**Before Phase 4:** `AnthropicProvider.cs` was a single 600+ line file handling:
- HTTP request building
- Message conversion
- SSE parsing
- Response accumulation

**Phase 4:** Split into four focused components:

```
BotNexus.Providers.Anthropic/
├── AnthropicProvider.cs          # Orchestrator (~310 lines)
├── AnthropicRequestBuilder.cs    # Request → JSON (~160 lines)
├── AnthropicMessageConverter.cs  # Messages → Anthropic format (~390 lines)
└── AnthropicStreamParser.cs      # SSE parsing (~320 lines)
```

**Why:** Each component now has a single responsibility, making testing, maintenance, and debugging easier. Async composition is clearer.

**Your code:** No changes needed — `AnthropicProvider` public API is identical.

---

### StopReason enum — Refusal and Sensitive

**Before Phase 4:**
```csharp
public enum StopReason
{
    Stop,        // Natural completion
    Length,      // Hit max token limit
    ToolUse,     // Model wants to call a tool
    Error,       // API error
    Aborted,     // Caller cancelled
    PauseTurn    // Agent loop boundary
    // Refusals and content flags buried in Stop/Error
}
```

**Phase 4:**
```csharp
public enum StopReason
{
    Stop,        // Natural completion
    Length,      // Hit max token limit
    ToolUse,     // Model wants to call a tool
    Error,       // API error
    Aborted,     // Caller cancelled
    Refusal,     // Model declined (safety filter) — NEW
    Sensitive    // Content flagged as sensitive — NEW
}
```

**Provider mapping (Anthropic example):**

```csharp
// Phase 4: explicit mapping for safety filters
stopReason = stopReasonStr switch
{
    "end_turn" => StopReason.Stop,
    "max_tokens" => StopReason.Length,
    "tool_use" => StopReason.ToolUse,
    "refusal" => StopReason.Refusal,              // NEW
    "content_policy" => StopReason.Sensitive,     // NEW
    "safety" => StopReason.Sensitive,             // NEW
    "sensitive" => StopReason.Sensitive,          // NEW
    _ => StopReason.Stop
};
```

**Why:** Refusals and content sensitivity flags are not errors — they're content policy signals. Treating them distinctly allows UX layers to respond appropriately (e.g., show a user-friendly message instead of an error).

**Your code — if you consume StopReason:**

```csharp
// Before
if (response.StopReason == StopReason.Stop || response.StopReason == StopReason.Error)
{
    DisplayResult(response);
}

// Phase 4: distinguish safety filters
switch (response.StopReason)
{
    case StopReason.Stop:
        DisplayResult(response);
        break;
    case StopReason.Refusal:
        ShowDialog("Your request was declined by the model's safety filter.");
        break;
    case StopReason.Sensitive:
        ShowDialog("The response was flagged as sensitive content.");
        break;
    case StopReason.Error:
        ShowError(response.ErrorMessage);
        break;
}
```

---

### Model identity — BaseUrl no longer matters

**Before Phase 4:**
```csharp
var model1 = new LlmModel(
    Id: "claude-3.5-sonnet",
    Provider: "anthropic",
    BaseUrl: "https://api.anthropic.com/v1",
    // ...
);

var model2 = new LlmModel(
    Id: "claude-3.5-sonnet",
    Provider: "anthropic",
    BaseUrl: "https://api-eu.anthropic.com/v1",  // Different region
    // ...
);

// Before Phase 4: these are different models
ModelRegistry.ModelsAreEqual(model1, model2) == false  // ❌
```

**Phase 4:** `ModelsAreEqual` compares **only `Id` and `Provider`**:

```csharp
// Phase 4: same model, different region
ModelRegistry.ModelsAreEqual(model1, model2) == true   // ✓
```

**Why:** Regional endpoints are deployment details, not model differences. This enables:
1. **Regional failover** — switch endpoints without reconfiguring agents
2. **Multi-tenant deployments** — different tenants use different endpoint URLs
3. **Testing** — swap in mocked endpoints seamlessly

**Your code — if you implement custom model comparison:**

```csharp
// If you override or cache ModelsAreEqual, update to compare only Id+Provider
private bool ModelsAreEqual(LlmModel a, LlmModel b)
    => a.Id == b.Id && a.Provider == b.Provider;  // BaseUrl ignored
```

---

## Agent Core Layer

### HasQueuedMessages property

**New in Phase 4:**
```csharp
public bool HasQueuedMessages
{
    get => _steeringQueue.HasItems || _followUpQueue.HasItems;
}
```

**Use case:** Check if steering or follow-up messages are pending without peeking at queue internals.

```csharp
// Useful for UIs and diagnostics
if (agent.HasQueuedMessages)
{
    Console.WriteLine($"Agent has pending messages (steering or follow-up)");
}

// Example: render UI queue indicator
var queueStatus = agent.HasQueuedMessages ? "▸ Queue" : "   ";
Console.WriteLine($"{agent.Status:5} {queueStatus}");
```

---

### Optional TransformContext

**Before Phase 4:**
```csharp
public record AgentOptions(
    // ...
    TransformContextDelegate TransformContext,  // always required
    // ...
);

public delegate Task<IReadOnlyList<AgentMessage>> TransformContextDelegate(
    IReadOnlyList<AgentMessage> messages,
    AgentContext context,
    CancellationToken cancellationToken);
```

**Phase 4:** `TransformContext` is now optional — defaults to identity passthrough:

```csharp
public record AgentOptions(
    // ...
    TransformContextDelegate? TransformContext = null,  // optional, defaults to identity
    // ...
);
```

**Why:** Most agents don't need context compaction. Requiring a delegate forced boilerplate:

```csharp
// Before Phase 4: always had to provide this even if unused
ConvertToLlm: (messages, ct) => Task.FromResult(messages),  // identity
```

**Your code:**

```csharp
// Phase 4: skip it entirely if you don't need context transformation
var options = new AgentOptions(
    // ... other fields ...
    TransformContext: null,  // or omit it entirely
    // ...
);

// Or supply one for context window management
var options = new AgentOptions(
    // ...
    TransformContext: async (messages, ct) =>
    {
        // Trim old messages if context window fills
        return messages.TakeLast(10).ToList();
    },
    // ...
);
```

---

### ConvertToLlm auto-default

**Before Phase 4:**
```csharp
public record AgentOptions(
    // ...
    ConvertToLlmDelegate ConvertToLlm,  // always required
    // ...
);
```

**Phase 4:** Auto-defaults to `DefaultMessageConverter` if not provided:

```csharp
public record AgentOptions(
    // ...
    ConvertToLlmDelegate? ConvertToLlm = null,  // optional
    // ...
);
// If null, uses DefaultMessageConverter.ConvertToLlm internally
```

**Why:** 90% of agents use the standard converter. This eliminates boilerplate.

**Your code:**

```csharp
// Before Phase 4
var options = new AgentOptions(
    ConvertToLlm: MessageConverter.ToProviderMessages,  // explicit
    // ...
);

// Phase 4: much simpler
var options = new AgentOptions(
    // ... (ConvertToLlm omitted, uses default)
    // ...
);

// Custom conversion still works
var options = new AgentOptions(
    ConvertToLlm: async (messages, model, ct) =>
    {
        // Custom logic
        return messages.Select(m => /* ... */).ToList();
    },
    // ...
);
```

---

### MessageStartEvent deferred from state

**Before Phase 4:**

```
Stream starts → MessageStartEvent → Added to state.Messages
                ↓
            MessageUpdateEvent (delta)
                ↓
            MessageEndEvent → Confirmed in state

Problem: If streaming is replayed/resumed, we have duplicates in state
```

**Phase 4:**

```
Stream starts → MessageStartEvent → NOT added to state yet
                ↓
            MessageUpdateEvent (delta)
                ↓
            MessageEndEvent → THEN added to state.Messages

Benefit: Clean replay/resume, no duplicates
```

**Why:** Deferred insertion prevents race conditions when streaming is interrupted. The message is only finalized when the response is complete.

**Your code — if you listen to `MessageStartEvent`:**

```csharp
// Phase 4 behavior
agent.Subscribe(async (evt, ct) =>
{
    if (evt is MessageStartEvent mse)
    {
        // mse.Message is NOT yet in agent.State.Messages
        // Use the event for UI (e.g., show "Assistant is thinking...")
        // Don't assume state.Messages contains it yet
    }
    
    if (evt is MessageEndEvent mee)
    {
        // NOW mee.Message is in agent.State.Messages
        // Safe to assume state reflects the message
    }
}, ct);
```

---

### Listener exceptions logged via OnDiagnostic

**Before Phase 4:**
```csharp
// If a listener threw an exception during abort/failure paths, it was swallowed
agent.Subscribe(async (evt, ct) =>
{
    throw new Exception("Oops!");  // Silent, lost
}, ct);

agent.PromptAsync("...");  // If failure path, exception buried
```

**Phase 4:**
```csharp
// Exceptions are now logged via OnDiagnostic
agent.Subscribe(async (evt, ct) =>
{
    throw new Exception("Oops!");  // Logged!
}, ct);

// Configure diagnostics
var diagnostics = new DiagnosticsCollector();
// ... (agent calls OnDiagnostic when listener throws)
```

**Why:** Better observability. Listener failures during cleanup/abort should be visible.

**Your code:**

```csharp
// No code change needed — exceptions are now logged automatically
// If you want to observe diagnostics:

// Example: console logging
var diagnostics = new DiagnosticsCollector();
// Pass diagnostics to agent creation pipeline (framework-dependent)
```

---

## Coding Agent Layer

### EditTool — DiffPlex integration

**Before Phase 4:**
- Exact match first
- Fuzzy match (normalize whitespace, quotes, line endings)
- Return simple matched text

**Phase 4:**
- Exact match first
- **Context-based unified diff via DiffPlex** (new)
- Fuzzy match with better visual context
- Return diff hunks for clarity

**Example — before/after:**

```csharp
// Before Phase 4
{
    "path": "src/User.cs",
    "edits": [
        {
            "oldText": "public string Name { get; set; }",
            "newText": "public string FullName { get; set; }"
        }
    ]
}

// Response (simple match):
// "OK: 1 edit applied"

// Phase 4 response (with DiffPlex context):
// OK: 1 edit applied
// --- src/User.cs (line 5)
// -public string Name { get; set; }
// +public string FullName { get; set; }
// Context (lines 3-7):
//   public class User
//   {
// -  public string Name { get; set; }
// +  public string FullName { get; set; }
//     public int Age { get; set; }
```

**Why:** When fuzzy matching is needed, context-aware diffs make debugging easier. You see exactly which occurrence was matched and how.

**Your code:** No changes — `EditTool` API is identical. Better diffs are automatic.

---

### ShellTool — Git Bash detection on Windows

**Before Phase 4:**
```csharp
// Windows always used PowerShell
// Platform contract: Windows = PowerShell, Unix = bash
if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
{
    return PowerShellPath;  // always
}
```

**Phase 4:**
```csharp
// Windows now tries Git Bash first
// If available, prefer Git Bash for better command compatibility
// Fall back to PowerShell only if Git Bash is not found

if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
{
    var bashPath = FindBashExecutable();  // Looks for bash.exe in PATH
    if (bashPath != null)
        return bashPath;  // Use Git Bash
    else
        return PowerShellPath;  // Fallback
}
```

**Why:** Many Windows developers use Git Bash (e.g., via Git for Windows). Bash commands are more portable and predictable across Windows/Unix.

**Your code — if you rely on specific shell behavior:**

```csharp
// Before: count on PowerShell-isms
bash -c "rm file.txt"  // fails on Windows (PowerShell doesn't understand bash -c)

// Phase 4: consider both shells
bash -c "rm file.txt"  // works if Git Bash is installed
# powershell.exe -NoLogo -NoProfile -NonInteractive -Command "Remove-Item file.txt"  // fallback

// Recommend: use portable commands
bash -c "rm file.txt"  // works on both if bash available
```

**If Git Bash is not available:**
```
[warning: bash not found, using PowerShell — install Git for Windows for best compatibility]
```

---

### ShellTool — Byte limits aligned (50*1024)

**Before Phase 4:**
```csharp
private const int MaxOutputBytes = /* varies by platform/context */
```

**Phase 4:**
```csharp
private const int MaxOutputBytes = 50 * 1024;  // 51,200 bytes
```

**Why:** Alignment with TypeScript implementation ensures consistent token budgeting across all platforms. Predictable output size = predictable token usage.

**Example:**

```csharp
// Phase 4 limits (still apply in Phase 5)
const int MaxOutputBytes = 50 * 1024;      // 51,200 bytes
const int MaxOutputLines = 2000;            // lines (whichever hits first)

// Phase 4 truncation suffix (superseded in Phase 5)
"[Output truncated at 51200 bytes]"  // Phase 4 format
"[Output truncated at 2000 lines]"   // Phase 4 format

// Phase 5 truncation notice (TAIL-based, appears at top of output)
"[output truncated — showing last 100 lines of 5000]"
```

**Your code — if you parse ShellTool output:**

```csharp
// Output now consistently capped at 51,200 bytes
// Phase 5: truncation keeps TAIL (last lines), notice at top
// Truncation notice format: "[output truncated — showing last N lines of TOTAL]"

var output = shellResult.Content[0].Value;
if (output.Contains("[output truncated"))
{
    Console.WriteLine("Output was truncated (showing tail)");
}

// No code changes needed — limits are transparent to consumers
```

---

## Migration Guide

### Updating your code — by scenario

#### Scenario 1: I implement a custom provider

**Required updates:**
1. Map `Refusal` and `Sensitive` stop reasons in your provider's stop reason switch:
   ```csharp
   stopReason = stopReasonStr switch
   {
       // existing mappings...
       "refusal" => StopReason.Refusal,
       "sensitive" => StopReason.Sensitive,
       _ => StopReason.Stop
   };
   ```

#### Scenario 2: I consume StopReason

**Recommended updates:**
1. Check for `Refusal` and `Sensitive` separately:
   ```csharp
   // Before
   if (msg.StopReason != StopReason.Error)
       DisplayResult(msg);

   // Phase 4
   switch (msg.StopReason)
   {
       case StopReason.Refusal:
       case StopReason.Sensitive:
           ShowSafetyMessage(msg.StopReason);
           break;
       case StopReason.Error:
           ShowError(msg);
           break;
       default:
           DisplayResult(msg);
           break;
   }
   ```

#### Scenario 3: I create an Agent with AgentOptions

**Optional simplifications:**
1. Omit `TransformContext` if you don't need context compaction
2. Omit `ConvertToLlm` if you use the default converter
3. Use new `HasQueuedMessages` for diagnostics

   ```csharp
   // Before
   var options = new AgentOptions(
       ConvertToLlm: DefaultMessageConverter.ToProviderMessages,
       TransformContext: (msgs, ctx, ct) => Task.FromResult(msgs),
       // ...
   );

   // Phase 4
   var options = new AgentOptions(
       ConvertToLlm: null,  // or omit
       TransformContext: null,  // or omit
       // ...
   );

   // Later: introspect queue
   if (agent.HasQueuedMessages)
       Console.WriteLine("Pending messages");
   ```

#### Scenario 4: I listen to MessageStartEvent

**No required changes**, but be aware:
1. `MessageStartEvent` payload is **not yet** in `agent.State.Messages`
2. Use it for UI updates only (e.g., "thinking..." indicator)
3. Wait for `MessageEndEvent` to read from state

#### Scenario 5: I rely on specific shell behavior on Windows

**Recommended:** Use portable commands that work in both PowerShell and bash:
```csharp
// Good: works in both
"echo hello"
"ls -la"
"grep pattern file"

// Risky: PowerShell-specific
"Get-ChildItem"
"Write-Host"
```

---

## Summary of benefits

| Change | Benefit |
|--------|---------|
| AnthropicProvider refactoring | Easier to maintain, test, and extend |
| StopReason `Refusal`/`Sensitive` | Better safety filter observability; can show context-specific UX |
| Model identity (Id+Provider) | Regional failover, multi-tenant support |
| `HasQueuedMessages` | Queue introspection for UI/diagnostics |
| Optional `TransformContext` | Less boilerplate for simple agents |
| ConvertToLlm auto-default | Simpler agent creation |
| MessageStartEvent deferral | Clean streaming replay/resume, no duplicates |
| EditTool DiffPlex | Better debugging when fuzzy matching is needed |
| ShellTool Git Bash | Better cross-platform command compatibility |
| Aligned byte limits | Consistent token budgeting across platforms |

---

## See also

- [Provider system](01-providers.md) — updated with Phase 4 AnthropicProvider structure
- [Agent core](02-agent-core.md) — updated with new properties and streaming behavior
- [Coding agent](03-coding-agent.md) — updated EditTool and ShellTool documentation
- [Provider development guide](11-provider-development-guide.md) — updated stop reason mapping examples

---

# Phase 5 Changelog

Port Audit Phase 5 (completed with 14 commits) brings validation, configuration, and cross-provider compatibility improvements to BotNexus. This section documents the changes and provides before/after examples.

> **Audience:** Developers maintaining providers, agents, or extensions. Understand what changed and how to update your code.

---

## What changed — at a glance

| Layer | Component | Change | Impact |
|-------|-----------|--------|--------|
| **Coding Agent** | `ShellTool` | TAIL truncation (keep last lines) | Better tail-based error visibility |
| **Coding Agent** | `ShellTool` | Configurable timeout (`DefaultShellTimeoutSeconds`) | More control over long-running commands |
| **Coding Agent** | `ShellTool` | Per-call timeout override via `timeout` arg | Runtime command-specific timeouts |
| **Coding Agent** | `ListDirectoryTool` | Show 2 levels deep (children + grandchildren) | Better directory structure visibility |
| **Coding Agent** | `ContextFileDiscovery` | Ancestor walk from cwd to git root | Hierarchical AGENTS.md discovery |
| **Provider Core** | `ToolCallValidator` | New validation before dispatch | Catch schema violations early |
| **Provider Core** | `ShortHash` | Utility for cross-provider ID normalization | Consistent tool call ID hashing |
| **Agent Core** | `MessageTransformer` | Normalizer receives `(callId, sourceModel, targetProviderId)` | Provider-aware ID normalization |
| **Agent Core** | `AgentLoopRunner` | Per-retry context transform | Overflow compaction visible per attempt |
| **Agent Core** | `CompactForOverflow` | Fixed list aliasing bug | Clean overflow recovery |

---

## Coding Agent Layer

### ShellTool — TAIL truncation

**Before Phase 5:**
```csharp
// Output was truncated at the HEAD (kept first lines)
// Errors at the end were often missing
```

**Phase 5:**
```csharp
// Output is truncated at the TAIL (keeps last lines)
// Truncation notice appears at the top
[output truncated — showing last 100 lines of 5000]
... (last 100 lines) ...
```

**Why:** Errors and results are typically at the end of command output (logs, build output, test results). Keeping the tail means you see the actual error without scrolling up.

**Your code — if you parse ShellTool output:**

```csharp
// Before Phase 5: errors might be at the top
var output = shellResult.Content[0].Value;
if (output.Contains("Error") || output.Contains("Exception"))
{
    // Might miss errors at the tail
}

// Phase 5: check tail for errors
if (output.Contains("[output truncated"))
{
    // Output was truncated; the error is likely in the visible tail
}
```

---

### ShellTool — Configurable timeout

**Before Phase 5:**
```csharp
// Timeout hardcoded to 120 seconds (or ~arbitrary based on platform)
// No way to configure per-command
```

**Phase 5:**
```csharp
// Default via CodingAgentConfig.DefaultShellTimeoutSeconds (600s default)
// Per-call override via "timeout" argument
```

**Configure default:**

```csharp
var config = CodingAgentConfig.Load(workingDirectory);
config.DefaultShellTimeoutSeconds = 600;  // or set in config.json
```

**Override per-call:**

```csharp
var toolCall = new ToolCallContent(
    Name: "bash",
    Arguments: new Dictionary<string, object?>
    {
        { "command", "npm run build" },
        { "timeout", 1800 }  // 30 minutes for this build
    });
```

**Tool definition:**

```json
{
  "type": "object",
  "properties": {
    "command": { "type": "string", "description": "Shell command text" },
    "timeout": { "type": "integer", "description": "Optional timeout in seconds" }
  },
  "required": ["command"]
}
```

**Why:** Different workloads have different runtimes. Long builds, CI/CD, or system provisioning may need 30+ minutes. Short commands should fail fast if they hang. Per-call override lets you tune without changing config.

---

### ListDirectoryTool — 2 levels deep

**Before Phase 5:**
```csharp
// Listed only direct children, flat
ls: directory/
  file1.txt
  file2.txt
  subdir (not expanded)
```

**Phase 5:**
```csharp
// Shows children + grandchildren (2 levels)
directory/
├── file1.txt
├── file2.txt
├── subdir/
│   ├── nested1.txt
│   └── nested2.txt
```

**Why:** One level is often insufficient to understand structure. Two levels gives you the mental model without overwhelming output.

**Your code:** No changes — the output format is automatic.

---

### ContextFileDiscovery — Ancestor walk

**Before Phase 5:**
```csharp
// Looked for AGENTS.md and copilot-instructions.md only in cwd
// Missed context from parent directories
```

**Phase 5:**
```csharp
// Walks from cwd → parent → ... → git root
// Discovers the first AGENTS.md and copilot-instructions.md at each level
// Stops at git root or filesystem root
```

**Discovery order:**
1. Check `cwd/.github/copilot-instructions.md`
2. Check `cwd/AGENTS.md`
3. Move to parent directory, repeat
4. Stop at git root (first `.git` found) or filesystem root

**Why:** Monorepos often have project-level and org-level context files. Walking the hierarchy ensures you get the most specific context first.

**Example:**

```
project/
├── .git/                           (git root)
├── .github/copilot-instructions.md (org-level)
├── AGENTS.md                       (org-level)
├── services/
│   └── api/
│       ├── .github/copilot-instructions.md (project-level — discovered first)
│       ├── AGENTS.md               (project-level — discovered first)
│       └── src/
└── services/
    └── web/
        ├── .github/copilot-instructions.md
        ├── AGENTS.md
        └── src/
```

When running from `services/api/src/`, discovery finds:
1. `services/api/.github/copilot-instructions.md`
2. `services/api/AGENTS.md`
3. Stops (both found)

**Your code:** No changes — discovery is automatic. If you have multiple `AGENTS.md` files, the closest one is used.

---

## Provider Core Layer

### ToolCallValidator

**New in Phase 5:** Pre-dispatch validation of tool call arguments against JSON Schema.

```csharp
using BotNexus.Providers.Core.Validation;

public static (bool IsValid, string[] Errors) Validate(
    JsonElement arguments,
    JsonElement parameterSchema)
```

**Example:**

```csharp
var toolCall = assistantMessage.Content
    .OfType<ToolCallContent>()
    .First();

var tool = state.Tools.FirstOrDefault(t => t.Name == toolCall.Name);
if (tool is null)
{
    return ToolError("Unknown tool");
}

var (isValid, errors) = ToolCallValidator.Validate(
    toolCall.Arguments,
    tool.Definition.ParameterSchema);

if (!isValid)
{
    return ToolError($"Invalid arguments: {string.Join("; ", errors)}");
}

// Safe to execute
return await tool.ExecuteAsync(callId, toolCall.Arguments, ct);
```

**Checks:**
- **Required properties:** Missing required fields generate errors
- **Type matching:** Arguments must match schema type (string, number, object, etc.)
- **Enum validation:** Fields with `enum` constraint must be one of the allowed values

**Why:** Schema violations should be caught before tool execution. Clear error feedback helps the LLM correct its arguments.

**Your code — if you build custom tool executors:**

```csharp
// Integrate validation before tool dispatch
var (isValid, errors) = ToolCallValidator.Validate(arguments, parameterSchema);
if (!isValid)
{
    return new ToolResultMessage(
        ToolCallId: toolCall.Id,
        ToolName: toolCall.Name,
        Content: [new TextContent($"Validation error: {string.Join("; ", errors)}")],
        IsError: true);
}

// Execute tool
```

---

### ShortHash utility

**New in Phase 5:** Deterministic hashing for tool call ID normalization.

```csharp
using BotNexus.Providers.Core.Utilities;

public static string Generate(string input)
```

**Example:**

```csharp
var callId = "tool_call_12345";
var normalized = ShortHash.Generate(callId);
// Output: "a1b2c3d4" (deterministic, lowercase base-36)
```

**Properties:**
- **Deterministic:** Same input always produces same hash
- **Cross-provider:** Matches pi-mono's `shortHash()` behavior
- **Lowercase base-36:** Compact alphanumeric output
- **Non-cryptographic:** Fast, not for security

**Why:** Tool call IDs vary by provider. When transforming messages across providers, consistent hashing ensures tool results map to the correct tool calls.

**Your code — in MessageTransformer normalizer:**

```csharp
// Normalize tool call IDs when switching providers
normalizeToolCallId: (callId, sourceModel, targetProviderId) =>
{
    if (!string.Equals(sourceModel.Provider, targetProviderId, StringComparison.Ordinal))
    {
        return ShortHash.Generate(callId);
    }
    return callId;
}
```

---

## Agent Core Layer

### MessageTransformer — Normalizer signature

**Before Phase 5:**

```csharp
public static List<Message> TransformMessages(
    IReadOnlyList<Message> messages,
    LlmModel targetModel,
    Func<string, string>? normalizeToolCallId = null)
    //                    ^^^ just (callId) -> normalizedId
```

**Phase 5:**

```csharp
public static List<Message> TransformMessages(
    IReadOnlyList<Message> messages,
    LlmModel targetModel,
    Func<string, LlmModel, string, string>? normalizeToolCallId = null)
    //  ^^^ (callId, sourceModel, targetProviderId) -> normalizedId
```

**Why:** The normalizer now has context about both the source and target models. This enables provider-aware ID schemes.

**Your code — if you provide a normalizer:**

```csharp
// Before Phase 5
normalizeToolCallId: (callId) => ShortHash.Generate(callId)

// Phase 5: update signature
normalizeToolCallId: (callId, sourceModel, targetProviderId) =>
{
    // Normalize only when switching providers
    if (!string.Equals(sourceModel.Provider, targetProviderId, StringComparison.Ordinal))
    {
        return ShortHash.Generate(callId);
    }
    return callId;  // Same provider, keep ID unchanged
}
```

**Callback parameters:**
- **`callId`** — Original tool call ID from source model
- **`sourceModel`** — Model that produced the tool call (with Provider, Api, Id, etc.)
- **`targetProviderId`** — Provider ID of the target model (e.g., `"anthropic"`, `"openai"`)

---

### AgentLoopRunner — Per-retry context transform

**Before Phase 5:**

```csharp
// Transform ran once before retries
var transformedMessages = config.TransformContext is null
    ? messages
    : await config.TransformContext(messages, ct);

while (attempt < maxAttempts)
{
    try
    {
        // Retry with same transformed messages
    }
    catch (ContextOverflow)
    {
        // Compact and retry
    }
}
```

**Phase 5:**

```csharp
while (attempt < maxAttempts)
{
    // Re-run transform per attempt
    var transformedMessages = config.TransformContext is null
        ? messages
        : await config.TransformContext(messages, ct);

    try
    {
        // Attempt with fresh transform
    }
    catch (ContextOverflow)
    {
        // Compact source, re-transform and retry
    }
}
```

**Why:** If overflow triggers compaction, the transform should see the compacted messages, not the original. This makes overflow recovery visible to custom transforms.

**Your code — if you implement context transforms:**

```csharp
// Before Phase 5: transform ran once
config = config with
{
    TransformContext: async (msgs, ct) =>
    {
        // This ran once; you didn't see overflow compaction
        return msgs.TakeLast(10).ToList();
    }
};

// Phase 5: transform runs per attempt
config = config with
{
    TransformContext: async (msgs, ct) =>
    {
        // This runs per retry attempt
        // You see overflow compaction if it happens
        Console.WriteLine($"Transform: {msgs.Count} messages");
        return msgs.TakeLast(10).ToList();
    }
};
```

---

### CompactForOverflow — List aliasing fix

**Before Phase 5:**

```csharp
private static IReadOnlyList<AgentMessage> CompactForOverflow(IReadOnlyList<AgentMessage> messages)
{
    if (messages.Count <= 12)
    {
        return messages;  // ❌ Returns same reference
    }

    var keep = Math.Max(8, messages.Count / 3);
    return messages.Skip(messages.Count - keep).ToList();
}

// Bug: If messages <= 12, it returns the same list reference
// Later, messages.Clear() also clears the compacted result
```

**Phase 5:**

```csharp
private static IReadOnlyList<AgentMessage> CompactForOverflow(IReadOnlyList<AgentMessage> messages)
{
    if (messages.Count <= 12)
    {
        return messages.ToList();  // ✓ Returns a copy
    }

    var keep = Math.Max(8, messages.Count / 3);
    return messages.Skip(messages.Count - keep).ToList();
}

// Fixed: Always returns a fresh list, no aliasing
```

**Why:** The caller does `messages.Clear(); messages.AddRange(compacted)`. If both point to the same list, the compaction is lost.

**Your code:** No changes needed — this is an internal fix.

---

## Migration Guide

### Updating your code — by scenario

#### Scenario 1: I provide a MessageTransformer normalizer

**Required update:**

```csharp
// Before Phase 5
TransformMessages(messages, targetModel, 
    normalizeToolCallId: (callId) => ShortHash.Generate(callId))

// Phase 5: update signature
TransformMessages(messages, targetModel,
    normalizeToolCallId: (callId, sourceModel, targetProviderId) =>
    {
        if (!string.Equals(sourceModel.Provider, targetProviderId, StringComparison.Ordinal))
        {
            return ShortHash.Generate(callId);
        }
        return callId;
    })
```

#### Scenario 2: I implement a custom tool executor

**Recommended update:**

```csharp
// Add validation before tool dispatch
var (isValid, errors) = ToolCallValidator.Validate(arguments, tool.Definition.ParameterSchema);
if (!isValid)
{
    return ToolError(string.Join("; ", errors));
}

return await tool.ExecuteAsync(callId, arguments, ct);
```

#### Scenario 3: I parse ShellTool output

**Recommended:**

Recognize the new truncation format:

```csharp
// Before Phase 5: truncation was at head
// [command exited with code 1]
// (error output mostly missing)

// Phase 5: truncation is at tail
// [output truncated — showing last 100 lines of 5000]
// ... (error output at the end, visible!)
```

#### Scenario 4: I have multiple AGENTS.md files

**No code changes:** Discovery now walks the hierarchy. The closest AGENTS.md is used.

#### Scenario 5: I have long-running commands

**Recommended:** Set timeout in config or per-call.

```csharp
// In config.json
{
    "defaultShellTimeoutSeconds": 1800  // 30 minutes for builds
}

// Or override per-call
{
    "command": "npm run build",
    "timeout": 3600  // 1 hour for this build
}
```

---

## Summary of benefits

| Change | Benefit |
|--------|---------|
| ShellTool TAIL truncation | Errors and results visible (usually at tail) |
| Configurable timeout | Control long-running commands, fail fast on hangs |
| ListDirectory 2 levels | Better structure visibility without clutter |
| Context discovery ancestor walk | Hierarchical context (monorepo support) |
| ToolCallValidator | Schema violations caught early, clear error feedback |
| ShortHash | Consistent cross-provider tool call ID hashing |
| MessageTransformer normalizer context | Provider-aware ID normalization |
| Per-retry context transform | Overflow compaction visible to transforms |
| CompactForOverflow fix | Clean overflow recovery, no data loss |

---

## See also

- [Migration guide](13-phase5-migration-guide.md) — quick reference for breaking changes
- [Providers](01-providers.md) — updated MessageTransformer and validation
- [Agent core](02-agent-core.md) — retry and context transform behavior
- [Coding agent](03-coding-agent.md) — ShellTool and ContextFileDiscovery updates

