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
// Phase 4 limits
const int MaxOutputBytes = 50 * 1024;      // 51,200 bytes
const int MaxOutputLines = 2000;            // lines (whichever hits first)

// Line truncation suffix
"[Output truncated at 51200 bytes]"  // marks where output was cut (bytes)
"[Output truncated at 2000 lines]"   // marks where output was cut (lines)
```

**Your code — if you parse ShellTool output:**

```csharp
// Output now consistently capped at 51,200 bytes
// Truncated output includes "[Output truncated at ...]" suffix

var output = shellResult.Content[0].Value;
if (output.Contains("[Output truncated at"))
{
    Console.WriteLine("Output was truncated");
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
