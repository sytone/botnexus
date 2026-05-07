### Memory Wave 1 — Documentation Ambiguity

**Decision Date:** 2026-05-08  
**Raised By:** Kif (Documentation Engineer)  
**Status:** Open — needs team input  

---

## Issue

The Wave 1 `memory_save` tool confines all writes to the `memory/` subdirectory under the agent workspace. The path-traversal guard (`EnsureWithinRoot`) blocks any `file_path` that resolves outside this root — including `../MEMORY.md`.

This means agents **cannot** write to `MEMORY.md` (workspace root) using the `memory_save` tool. They must use a separate tool (e.g., `write_file` or `edit_file`) to update durable long-term memory.

### Previous behavior

The old `memory_save(content, target="memory")` signature wrote directly to `MEMORY.md` under `## Notes`. That capability no longer exists in the new tool.

### Current behavior

| Call | Result |
|------|--------|
| `memory_save(content="fact")` | Appends to `memory/YYYY-MM-DD.md` ✅ |
| `memory_save(content="fact", file_path="topic.md")` | Appends to `memory/topic.md` ✅ |
| `memory_save(content="fact", file_path="../MEMORY.md")` | **Blocked** — escapes memory root ❌ |

### What I documented

I documented the current behavior accurately. The `AGENTS.md` template tells agents to "Append durable summaries to `MEMORY.md`" — but doesn't clarify that a different tool is needed to do so. I added a note in the docs that `MEMORY.md` edits happen outside `memory_save`, but the team should confirm whether this is intentional or whether a future Wave should re-add direct `MEMORY.md` write support.

## Options

1. **Accept as-is**: Agents use `write_file`/`edit_file` for `MEMORY.md` edits. Document this as the intended workflow.
2. **Extend `memory_save`**: Add a special-case for `MEMORY.md` that allows writes to the workspace root file.
3. **Move `MEMORY.md`**: Relocate durable memory into `memory/MEMORY.md` so it falls within the memory root.

## Recommendation

Option 1 for now (Wave 1 ships as-is), with Option 2 or 3 considered for a future wave if the split workflow proves confusing for agents.
