---
id: bug-edit-tool-diffplex-missing
title: "Edit Tool Fails — DiffPlex Assembly Not Found"
type: bug
priority: high
status: draft
created: 2026-04-17
tags: [bug, tools, edit, dependency]
---

# Bug: Edit Tool Fails — DiffPlex Assembly Not Found

**Status:** draft
**Priority:** high
**Created:** 2026-04-17

## Problem

The `edit` tool fails with a missing assembly error:

```
Could not load file or assembly 'DiffPlex, Version=1.9.0.0, Culture=neutral, PublicKeyToken=1d35e91d1bd7bc0f'. The system cannot find the file specified.
```

Observed by agent **Aurum** when attempting to edit `MEMORY.md`. The edit payload was valid — the failure is a runtime dependency resolution issue, not a usage error.

## Impact

- **All agents** are likely affected — the `edit` tool is a core tool, not agent-specific
- Agents fall back to `write` (full file replacement) which is riskier for partial edits
- High priority since `edit` is one of the most frequently used tools

## Reproduction

Any `edit` tool call should reproduce. Example:

```json
{
  "path": "any-file.md",
  "edits": [
    {
      "oldText": "some existing text",
      "newText": "replacement text"
    }
  ]
}
```

## Likely Cause

`DiffPlex 1.9.0` is referenced by the edit tool implementation but the assembly isn't being deployed/loaded correctly. Possible causes:

- Missing from the build output / publish folder
- Not included in the extension's assembly load context
- Version mismatch (newer version deployed but binding redirect expects 1.9.0 exactly)

## Notes

- Reported by: Aurum agent, 2026-04-17
- Nova's edit tool appears to work (this file was created successfully) — may be intermittent or agent-specific load context issue
