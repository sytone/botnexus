---
id: bug-tool-argument-type-mismatch
title: "Tool Argument Type Mismatch Research"
type: research
created: 2026-04-14
author: nova
---

# Research: Tool Argument Type Mismatch

## Discovery

During the planning audit session (2026-04-14), two tools were found to be completely broken:
- `exec` - fails 100% with "must be a string array"
- `edit` - fails 100% with "Each edits entry must be an object"

Both tools were working in earlier sessions, suggesting a regression in the argument parsing pipeline.

## Argument Flow

```
LLM Response (JSON string)
    |
    v
Provider Stream Parser (Anthropic/OpenAI)
    |
    v
StreamingJsonParser.Parse(jsonString) -> Dictionary<string, object?>
    |
    v
ToolCallContent.Arguments (Dictionary<string, object?>)
    |
    v
ToolCallValidator.Validate() - re-serializes to JsonElement, validates schema
    |
    v
tool.PrepareArgumentsAsync(arguments) - tool-specific parsing
    |
    v
tool.ExecuteAsync(preparedArgs)
```

### Key insight: double conversion

The arguments go through TWO conversions:
1. `StreamingJsonParser.Parse()` converts JSON string -> `Dictionary<string, object?>` with CLR types
2. `ToolCallValidator.Validate()` re-serializes to `JsonElement` for schema validation

But after validation, the ORIGINAL `Dictionary<string, object?>` (from step 1) is passed to the tool. The `JsonElement` from step 2 is discarded. So tools receive CLR types, not `JsonElement` values.

### The type mismatch table

| JSON value | StreamingJsonParser CLR type | Tool expects |
|-----------|---------------------------|-------------|
| `"hello"` | `string` | `string` or `JsonElement` |
| `42` | `long` (if fits) or `double` | `int`, `long`, or `JsonElement` |
| `true` | `bool` | `bool` or `JsonElement` |
| `["a","b"]` | `List<object?>` containing `string` items | `IReadOnlyList<string>` or `JsonElement` (array) |
| `[{...}]` | `List<object?>` containing `Dictionary<string,object?>` items | `JsonElement` (array) or `IEnumerable<object?>` |
| `{"k":"v"}` | `Dictionary<string, object?>` | `IReadOnlyDictionary<string,object?>` or `JsonElement` (object) |

The mismatches are:
- `List<object?>` vs `IReadOnlyList<string>` - covariance prevents match even when all items are strings
- `List<object?>` might match `IEnumerable<object?>` in C# pattern matching, but individual items as `Dictionary<string,object?>` vs `IReadOnlyDictionary<string,object?>` - SHOULD match but needs verification

## Provider-specific investigation

### Anthropic (github-copilot with claude model)
- `AnthropicStreamParser.cs` line 301: `var args = StreamingJsonParser.Parse(accumulated);`
- This is the active provider for Nova (runtime: `provider=github-copilot | model=claude-opus-4.6`)
- Confirmed: arguments arrive as `Dictionary<string, object?>` with `List<object?>` for arrays

### OpenAI (streaming)
- `OpenAIStreamProcessor.cs` lines 289, 311: `StreamingJsonParser.Parse(state.Args.ToString())`
- Same conversion path, same issue

### All providers affected
Every provider that does streaming (all of them for tool calls) routes through `StreamingJsonParser`.

## ExecTool specific

`ExecTool.ReadStringArray()` handles:
1. `IReadOnlyList<string>` - direct match (never happens from StreamingJsonParser)
2. `JsonElement { Array }` - from raw JsonElement (never happens because args are pre-converted)
3. Default - throws "must be a string array"

The `List<object?>` from StreamingJsonParser doesn't match either case because:
- `List<object?>` is NOT `IReadOnlyList<string>` (even if all elements are strings, the generic parameter is `object?`)
- It's not a `JsonElement`

## EditTool specific

`EditTool.ParseEditObject()` handles:
1. `JsonElement` - from raw JsonElement (never happens)
2. `IReadOnlyDictionary<string, object?>` - from pre-converted dict

Case 2 SHOULD match `Dictionary<string, object?>` which implements `IReadOnlyDictionary<string, object?>`. But the error occurs, suggesting either:
- The flow isn't reaching `ParseEditObject` (the list wrapper fails first)
- Or there's a runtime type issue that needs debugging with actual breakpoints

## Potential regression timeline

This may have worked before if:
- An earlier version used a different parsing path (raw JsonElement passthrough)
- The streaming parser was updated to eagerly convert
- Or tools were updated to use JsonElement patterns without updating for CLR types

The `StreamingJsonParser` comment says it's a "Port of pi-mono's utils/json-parse.ts" which suggests it's been this way since porting. The tools may have been written assuming `JsonElement` input (matching the OpenAI/Anthropic SDK patterns) without accounting for the pre-conversion.
