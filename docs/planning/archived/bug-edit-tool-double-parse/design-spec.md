---
id: bug-edit-tool-double-parse
title: "EditTool Double-Parses edits Argument — ExecuteAsync Re-parses Already-Prepared EditEntry Objects"
type: bug
priority: high
status: done
created: 2026-04-14
updated: 2026-04-14
author: nova
tags: [tools, edit, parsing, arguments, double-parse]
---

# Bug: EditTool Double-Parses `edits` Argument

## Summary

`EditTool` is **100% broken**. Every `edit` call fails with `"Each edits entry must be an object."` because `ExecuteAsync` re-parses the `edits` argument that `PrepareArgumentsAsync` already converted from `JsonElement` to `IReadOnlyList<EditEntry>`.

## Symptoms

- **Every** `edit` call fails regardless of path (workspace or external)
- Error: `Each edits entry must be an object.`
- `write`, `read`, `grep`, `glob`, `exec` all work fine — only `edit` is broken

## Root Cause

The `ToolExecutor` pipeline calls tools in two phases:

1. `PrepareArgumentsAsync(rawArgs)` → validates and transforms raw LLM arguments
2. `ExecuteAsync(preparedArgs)` → executes with the prepared arguments

`EditTool` calls `ReadEdits()` in **both** phases:

### Phase 1: `PrepareArgumentsAsync` (line ~89)
```csharp
IReadOnlyDictionary<string, object?> prepared = new Dictionary<string, object?>(StringComparer.Ordinal)
{
    ["path"] = ReadRequiredString(arguments, "path"),
    ["edits"] = ReadEdits(arguments)  // ← parses JsonElement → IReadOnlyList<EditEntry> ✅
};
```

### Phase 2: `ExecuteAsync` (line ~107)
```csharp
var edits = ReadEdits(arguments);  // ← re-parses IReadOnlyList<EditEntry> 💥
```

In Phase 2, `ReadEdits` → `ParseEdits` receives an `IReadOnlyList<EditEntry>` (not `JsonElement`):
- `value is JsonElement` → **no** (it's `IReadOnlyList<EditEntry>`)
- `value is IEnumerable<object?>` → **yes** (matches, iterates)
- Each item is `EditEntry`, goes to `ParseEditObject`:
  - `EditEntry` is not `JsonElement` → skip
  - `EditEntry` is not `IReadOnlyDictionary<string, object?>` → skip
  - Falls through to `_ => throw new ArgumentException("Each edits entry must be an object.")` 💥

## Affected Code

**File:** `src/tools/BotNexus.Tools/EditTool.cs`

**Lines:**
- ~89: `PrepareArgumentsAsync` stores `ReadEdits(arguments)` as `IReadOnlyList<EditEntry>`
- ~107: `ExecuteAsync` calls `ReadEdits(arguments)` again on the already-prepared dict
- ~211: `ParseEditObject` has no branch for `EditEntry` type

## Fix Options

### Option A: Cast in ExecuteAsync (recommended — one-line fix)

Replace line ~107 in `ExecuteAsync`:
```csharp
// Before:
var edits = ReadEdits(arguments);

// After:
var edits = (IReadOnlyList<EditEntry>)arguments["edits"]!;
```

Since `ExecuteAsync` always receives prepared arguments from `PrepareArgumentsAsync`, the `edits` value is guaranteed to be `IReadOnlyList<EditEntry>`.

**Pros:** Minimal change, clear intent, no unnecessary re-parsing.
**Cons:** Tight coupling to PrepareArgumentsAsync output type.

### Option B: Add passthrough in ParseEdits

Add an early return in `ParseEdits`:
```csharp
private static List<EditEntry> ParseEdits(object value)
{
    if (value is IReadOnlyList<EditEntry> already)
        return already.ToList();
    // ... existing JsonElement and IEnumerable<object?> branches
}
```

**Pros:** Defensive, handles both raw and prepared args.
**Cons:** Allocates a new list unnecessarily.

### Option C: Store edits as field during PrepareArgumentsAsync

Cache edits in a private field during prepare, skip re-parsing in execute.

**Pros:** Clean separation.
**Cons:** Adds mutable state to a tool, may conflict with concurrent calls.

## Recommendation

**Option A** — it's a one-line fix, makes the contract explicit (ExecuteAsync trusts PrepareArgumentsAsync), and is consistent with how other tools consume prepared arguments.

## Testing

After fix, verify:
1. `edit` on workspace files works
2. `edit` on allowed external paths works (e.g., `Q:\repos\botnexus\...`)
3. `edit` with multiple edits in one call works
4. `edit` with invalid path still returns proper error

## Context

This bug was exposed by the `StreamingJsonParser` fix (commit `5066bfb`) which correctly changed array conversion from `List<object?>` to `element.Clone()`. The StreamingJsonParser fix is correct — the double-parse in EditTool is a pre-existing issue that was masked when arrays arrived as `List<object?>` (which also failed, but at a different point).

The old bug `bug-tool-argument-type-mismatch` is archived — it correctly identified the StreamingJsonParser issue but missed this secondary bug in EditTool.
