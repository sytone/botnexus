# Phase 5 Migration Guide

**For teams upgrading from Phase 4 to Phase 5**

This guide covers breaking changes and new integration points introduced in Phase 5.

> **Audience:** Developers integrating BotNexus providers, building custom agents, or extending the core framework.

---

## Breaking Changes

### MessageTransformer normalizer signature

**Before Phase 4:**
```csharp
Func<string, string>? normalizeToolCallId  // just (callId) -> normalizedId
```

**Phase 5:**
```csharp
Func<string, LlmModel, string, string>? normalizeToolCallId  
// (callId, sourceModel, targetProviderId) -> normalizedId
```

**Why:** Tool call ID normalization now has context: the source model and target provider. This enables provider-specific ID schemes and cross-provider tool call tracking.

**Your code — if you provide a normalizer:**

```csharp
// Before Phase 4
normalizeToolCallId: (callId) => ShortHash.Generate(callId)

// Phase 5: Update to receive source model and target provider
normalizeToolCallId: (callId, sourceModel, targetProviderId) =>
{
    // Example: normalize only when switching providers
    if (!string.Equals(sourceModel.Provider, targetProviderId, StringComparison.Ordinal))
    {
        return ShortHash.Generate(callId);
    }
    return callId;
}
```

If you don't provide a normalizer (pass `null`), no change is required — tool call IDs pass through unchanged.

---

## New Integration Points

### ToolCallValidator

**New in Phase 5:** Validate tool call arguments before dispatch.

```csharp
using BotNexus.Agent.Providers.Core.Validation;

var arguments = JsonDocument.Parse("""{"command": "ls -la"}""").RootElement;
var schema = tool.Definition.ParameterSchema;

var (isValid, errors) = ToolCallValidator.Validate(arguments, schema);
if (!isValid)
{
    Console.WriteLine($"Validation failed: {string.Join(", ", errors)}");
    // Return error ToolResult instead of dispatching to tool
}
else
{
    // Safe to execute tool
    var result = await tool.ExecuteAsync(callId, arguments, ct);
}
```

**When to use:** Integrate into your tool execution pipeline to catch schema violations early and provide clear error feedback to the LLM.

### ShortHash utility

**New in Phase 5:** Deterministic, cross-provider tool call ID hashing.

```csharp
using BotNexus.Agent.Providers.Core.Utilities;

var normalizedId = ShortHash.Generate(callId);
// Output: stable lowercase base-36 string (e.g., "a1b2c3d4")
```

**When to use:** In `MessageTransformer` normalizer callbacks or when you need consistent tool call ID schemes across provider boundaries.

### ShellTool timeout configuration

**New in Phase 5:** Per-call timeout override.

```csharp
// Tool definition now includes "timeout" optional argument
{
    "command": "ls -la",
    "timeout": 30  // Override default 600s timeout for this call
}
```

Configure the default via `CodingAgentConfig`:

```csharp
var config = new CodingAgentConfig { DefaultShellTimeoutSeconds = 600 };
// Or override per-call via tool arguments
```

---

## Recommended Upgrades (Optional)

### ListDirectory depth parameter

**Phase 5:** `ListDirectoryTool` now shows 2 levels deep (children + grandchildren) instead of flat.

If you parse the output, update your expectations:

```
directory/
├── file1.txt
├── file2.txt
├── subdir/
│   ├── nested1.txt
│   └── nested2.txt
```

No code changes needed — it's automatic. If you want the old flat behavior, post-process the output.

### ShellTool TAIL truncation

**Phase 5:** Output now keeps the LAST lines (tail) instead of the first (head). Truncation notice at the top.

```
[output truncated — showing last 100 lines of 5000]
... last 100 lines ...
```

If you parse shell output for errors/results, this is generally better — errors are often at the end of logs.

### Context file discovery ancestor walk

**Phase 5:** Discovery now walks from cwd to git root, finding `AGENTS.md` and `.github/copilot-instructions.md` at each level.

This is automatic. If you have multiple `AGENTS.md` files in the hierarchy, the closest one (to cwd) is discovered first.

---

## Summary

| Change | Migration effort | Impact |
|--------|------------------|--------|
| MessageTransformer normalizer signature | Medium | **Breaking** if you provide a normalizer |
| ToolCallValidator integration | Low | Optional; recommended for better error handling |
| ShortHash utility | Low | Optional; useful for cross-provider ID schemes |
| ShellTool timeout config | Low | Transparent; default is 600s |
| ListDirectory depth | None | Automatic improvement |
| ShellTool TAIL truncation | None | Automatic improvement |
| Context discovery ancestor walk | None | Automatic improvement |

---

## Checklist

- [ ] If you provide a `MessageTransformer` normalizer, update the signature to `(callId, sourceModel, targetProviderId) => ...`
- [ ] Consider integrating `ToolCallValidator` in your tool execution pipeline
- [ ] Update any code that parses shell output to handle TAIL-based truncation (errors at bottom)
- [ ] Test context file discovery if you have multiple `AGENTS.md` files in your hierarchy
- [ ] Review ShellTool timeout configuration if you have long-running commands

---

## See also

- [Port Audit Phase 5 Changelog](12-port-audit-changelog.md) — detailed change documentation
- [Providers](01-providers.md) — MessageTransformer and validation reference
- [Agent core](02-agent-core.md) — retry and context transform behavior
- [Building your own agent](04-building-your-own.md) — integration points for custom agents
