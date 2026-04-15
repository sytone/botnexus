# Research: EditTool Double-Parse Bug

## Discovery

Found during verification of `bug-tool-argument-type-mismatch` fix. After the `StreamingJsonParser` fix was deployed (commit `5066bfb`), `exec` started working but `edit` remained broken with the same error message.

## Investigation Trail

### Initial hypothesis: Binary not rebuilt
The gateway DLLs (last modified 22:37) predated the fix commit (22:50). Gateway was rebuilt and restarted.

### Post-restart: Still broken
After restart, `exec` confirmed working — arrays now arrive as `JsonElement` from `StreamingJsonParser.ConvertElement`. But `edit` still failed with `"Each edits entry must be an object."`

### False positive: "Works on workspace files"
Initial testing suggested `edit` worked on workspace files but not external paths. This was a false positive — the gateway was restarted mid-test with the old binary. Re-testing confirmed `edit` fails on ALL paths.

### Root cause isolation
Traced the `ToolExecutor` pipeline:

```
ToolExecutor.PrepareAsync()
  → tool.PrepareArgumentsAsync(rawArgs)     # rawArgs has edits as JsonElement
  → returns preparedArgs                     # preparedArgs has edits as IReadOnlyList<EditEntry>

ToolExecutor.ExecutePreparedToolCallAsync()
  → tool.ExecuteAsync(prepared.ValidatedArgs)  # ValidatedArgs = preparedArgs from above
```

Key finding: `EditTool.ExecuteAsync` calls `ReadEdits(arguments)` on the **prepared** arguments, not the raw ones. The prepared dict has `edits` as `IReadOnlyList<EditEntry>`, which `ParseEdits` matches as `IEnumerable<object?>`, then `ParseEditObject` fails because `EditEntry` matches neither `JsonElement` nor `IReadOnlyDictionary<string, object?>`.

### Why exec doesn't have this bug
`ExecTool` is an extension tool (`extensions/tools/exec/`). Its `ExecuteAsync` reads `command` directly:
```csharp
var command = ReadStringArray(arguments, "command");
```
But its `PrepareArgumentsAsync` stores the already-parsed `IReadOnlyList<string>`, and `ReadStringArray` handles `IReadOnlyList<string>` as its first branch. EditTool's `ParseEditObject` doesn't have an equivalent passthrough for `EditEntry`.

### Code flow detail

```
PrepareArgumentsAsync:
  rawArgs["edits"] = JsonElement (Array kind)          ← from StreamingJsonParser
  ReadEdits → ParseEdits:
    value is JsonElement ✅ → EnumerateArray → ParseEditElement → EditEntry
  returns dict with edits = IReadOnlyList<EditEntry>

ExecuteAsync:
  preparedArgs["edits"] = IReadOnlyList<EditEntry>     ← from PrepareArgumentsAsync
  ReadEdits → ParseEdits:
    value is JsonElement ❌ (it's IReadOnlyList<EditEntry>)
    value is IEnumerable<object?> ✅ (matches)
    → ParseEditObject(EditEntry):
      EditEntry is JsonElement ❌
      EditEntry is IReadOnlyDictionary<string, object?> ❌
      _ => throw "Each edits entry must be an object." 💥
```

## Other Tools: Same Pattern?

Checked whether other tools double-parse in ExecuteAsync:

| Tool       | Double-parse? | Status |
|------------|---------------|--------|
| EditTool   | Yes — ReadEdits in both Prepare and Execute | **BROKEN** |
| ReadTool   | No — reads path string only | OK |
| WriteTool  | No — reads path + content strings only | OK |
| GrepTool   | No — reads simple types only | OK |
| GlobTool   | No — reads simple types only | OK |
| ExecTool   | Yes — but ReadStringArray handles IReadOnlyList<string> | OK |
| ShellTool  | No — reads command string only | OK |

Only `EditTool` has this issue because it's the only built-in tool with complex nested argument types (array of objects) that doesn't handle the prepared type in its parser.

## Fix Validation

The recommended fix (Option A: direct cast in ExecuteAsync) was validated by tracing the code:
- `PrepareArgumentsAsync` always runs before `ExecuteAsync` (enforced by `ToolExecutor`)
- The prepared dict always has `edits` as `IReadOnlyList<EditEntry>` (no other code path)
- Direct cast is safe and eliminates the redundant parse entirely
