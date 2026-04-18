---
id: bug-tool-argument-type-mismatch
title: "Tool Argument Type Mismatch Between StreamingJsonParser and Tool Parsers"
type: bug
priority: high
status: done
created: 2026-04-14
updated: 2026-04-14
author: nova
tags: [tools, parsing, exec, edit, arguments, streaming-json]
---

# Bug: Tool Argument Type Mismatch Between StreamingJsonParser and Tool Parsers

**Type**: Bug
**Priority**: High (breaks exec tool entirely, edit tool for all providers using StreamingJsonParser)
**Status**: In Progress
**Author**: Nova

## Problem

The `exec` tool fails on every invocation with "Argument 'command' must be a string array." The `edit` tool fails with "Each edits entry must be an object." Both failures are caused by the same root issue: the types produced by `StreamingJsonParser.ConvertElement()` don't match the types expected by tool argument parsers.

### Repro

```
exec(command: ["echo", "hello"])
-> ERROR: Invalid arguments for 'exec': Argument 'command' must be a string array.

edit(path: "test.md", edits: [{"oldText": "foo", "newText": "bar"}])
-> ERROR: Each edits entry must be an object.
```

Both fail 100% of the time regardless of arguments.

## Root Cause

### StreamingJsonParser output types

`StreamingJsonParser.ConvertElement()` converts JSON to CLR types:

```csharp
// StreamingJsonParser.cs
private static object? ConvertElement(JsonElement element) => element.ValueKind switch
{
    JsonValueKind.String => element.GetString(),          // -> string
    JsonValueKind.Number => element.TryGetInt64(...),     // -> long or double
    JsonValueKind.Array => element.EnumerateArray()
        .Select(ConvertElement).ToList(),                 // -> List<object?>
    JsonValueKind.Object => ElementToDictionary(element), // -> Dictionary<string, object?>
    ...
};
```

Key conversions:
- JSON arrays -> `List<object?>` (containing strings, numbers, or nested dicts)
- JSON objects -> `Dictionary<string, object?>`

### ExecTool expects different types

```csharp
// ExecTool.ReadStringArray()
return value switch
{
    IReadOnlyList<string> list => list,     // List<object?> does NOT match this
    JsonElement { ValueKind: Array } => ..., // Not a JsonElement anymore
    _ => throw new ArgumentException("must be a string array")
};
```

`List<object?>` does not match `IReadOnlyList<string>` because the generic type parameter is `object?`, not `string` (even though all elements happen to be strings). And it's not a `JsonElement` because `StreamingJsonParser` already converted it.

### EditTool expects different types

```csharp
// EditTool.ParseEditObject()
return value switch
{
    JsonElement element => ParseEditElement(element),          // Not a JsonElement
    IReadOnlyDictionary<string, object?> dict => new EditEntry(...), // Should match Dictionary<string, object?>
    _ => throw new ArgumentException("Each edits entry must be an object.")
};
```

For edit, `Dictionary<string, object?>` DOES implement `IReadOnlyDictionary<string, object?>` in .NET 6+. So the pattern should match... unless:
1. The runtime is doing something unexpected with nullable reference type covariance
2. The actual type at runtime is different from expected
3. The edits value isn't going through `StreamingJsonParser` at all for this provider

**Needs debugging** to determine the exact runtime type. The fix is the same regardless.

## Analysis: Why tools work for some argument types but not others

| Argument type | StreamingJsonParser output | Tool expects | Match? |
|---------------|--------------------------|--------------|--------|
| `string` | `string` | `string` | Yes |
| `integer` | `long` | `int` | Depends on parser (most handle both) |
| `boolean` | `bool` | `bool` | Yes |
| `object` | `Dictionary<string, object?>` | `JsonElement` or `IReadOnlyDictionary<string, object?>` | Partial |
| `array of strings` | `List<object?>` | `IReadOnlyList<string>` or `JsonElement` | **NO** |
| `array of objects` | `List<object?>` containing `Dictionary<string, object?>` | `JsonElement` array or `IEnumerable<object?>` | Partial |

The fundamental issue: `StreamingJsonParser` converts everything eagerly, but tools were written expecting either raw `JsonElement` values OR strongly-typed collections. The `List<object?>` from array conversion matches neither.

## Which providers are affected?

| Provider | Argument parsing | Affected? |
|----------|-----------------|-----------|
| Anthropic (streaming) | `StreamingJsonParser.Parse()` | **YES** |
| OpenAI Completions (streaming) | `StreamingJsonParser.Parse()` | **YES** |
| OpenAI Responses (streaming) | `StreamingJsonParser.Parse()` | **YES** |
| OpenAI Compat (streaming) | Uses `OpenAIStreamProcessor` -> `StreamingJsonParser.Parse()` | **YES** |

All providers use `StreamingJsonParser` for tool call argument parsing. All are affected.

## Fix Options

### Option A: Fix StreamingJsonParser to preserve JsonElement (recommended)

Instead of eagerly converting to CLR types, keep the `JsonElement` values intact. Tools already handle `JsonElement` well.

```csharp
// Return raw JsonElement instead of converting
private static object? ConvertElement(JsonElement element) => element.ValueKind switch
{
    JsonValueKind.Null => null,
    JsonValueKind.True => true,
    JsonValueKind.False => false,
    JsonValueKind.String => element.GetString(),
    JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
    // CHANGE: Keep arrays and objects as JsonElement
    JsonValueKind.Array => element.Clone(),
    JsonValueKind.Object => ElementToDictionary(element), // Keep this for top-level
    _ => element.ToString()
};
```

This way, the top-level arguments are still a `Dictionary<string, object?>`, but nested arrays/objects remain as `JsonElement` which tools already handle.

**Risk**: Low. Only changes how nested complex values are stored. Top-level dict structure unchanged. All tool `ReadStringArray`/`ParseEdits` methods already have `JsonElement` branches.

### Option B: Fix each tool's argument parsers

Add `List<object?>` handling to every tool that takes arrays or complex types:

```csharp
// ExecTool.ReadStringArray - add case
IReadOnlyList<string> list => list,
List<object?> objectList => objectList.Select(o => o?.ToString() ?? "").ToList(),
JsonElement { ValueKind: JsonValueKind.Array } element => ...
```

```csharp
// EditTool.ParseEdits - add case  
List<object?> list => list.Select(ParseEditObject).ToList(),
```

**Risk**: Higher. Must update every tool individually. Easy to miss edge cases.

### Option C: Add conversion layer in ToolExecutor

Before passing arguments to `PrepareArgumentsAsync`, convert `List<object?>` values to `JsonElement` equivalently. Centralized fix.

**Risk**: Medium. Works but adds a conversion step that shouldn't be needed.

**Recommendation**: Option A. Fix at the source — `StreamingJsonParser` should produce types that tools expect. Changing one method fixes all tools.

## Files to Change

### Option A (recommended)
| File | Change |
|------|--------|
| `src/agent/BotNexus.Agent.Providers.Core/Utilities/StreamingJsonParser.cs` | `ConvertElement` returns `element.Clone()` for arrays instead of `List<object?>` |

### Option B (if A is rejected)
| File | Change |
|------|--------|
| `extensions/tools/exec/.../ExecTool.cs` | Add `List<object?>` case to `ReadStringArray` |
| `src/tools/BotNexus.Tools/EditTool.cs` | Add `List<object?>` case to `ParseEdits` and `ParseEditObject` |
| Any future tool with array/object params | Must also handle `List<object?>` |

## Testing

### Exec tool
1. `exec(command: ["echo", "hello"])` — should return "hello"
2. `exec(command: ["echo", "hello world", "foo bar"])` — spaces in args preserved
3. `exec(command: ["echo"], background: true)` — returns PID
4. `exec(command: ["echo"], env: {"FOO": "bar"})` — env vars work (object param)

### Edit tool
1. `edit(path: "test.md", edits: [{"oldText": "foo", "newText": "bar"}])` — single edit
2. `edit(path: "test.md", edits: [{"oldText": "a", "newText": "b"}, {"oldText": "c", "newText": "d"}])` — multiple edits
3. Verify `edit` still works with all providers (Anthropic, OpenAI, Compat)

### Regression
4. Tools with simple string/int/bool params continue to work
5. Tools with object params (exec env) continue to work
6. Streaming tool calls (partial JSON) still parse correctly
