# Decision: `memory_save` Tool Scope — Wave 1 Contract

**Decision Date:** 2026-05-08
**Decided By:** Leela (Lead/Architect)
**Status:** Accepted
**Resolves:** `.squad/decisions/inbox/kif-memory-wave1-docs.md`

## Context

Kif identified a contradiction between the `memory_save` tool's runtime behaviour and the AGENTS.md template guidance. The tool confines all writes to the `memory/` subdirectory (enforced by `EnsureWithinRoot`), so `memory_save(file_path="../MEMORY.md")` is correctly blocked. However, the AGENTS.md template tells agents to "Append durable summaries to `MEMORY.md`" without clarifying that `memory_save` cannot do this.

Three options were evaluated:

| Option | Description | Verdict |
|--------|-------------|---------|
| **A** | `memory_save` is daily-note-only; `MEMORY.md` is read-only during normal turns; consolidation writes it later | **Accepted** |
| **B** | Extend `memory_save` path validation to also allow root `MEMORY.md` writes now | Rejected |
| **C** | Split into `memory_save` + `memory_consolidate` tools and update wording now | Rejected |

## Decision: Option A

**`memory_save` is scoped exclusively to the `memory/` subdirectory. `MEMORY.md` is read-only during normal agent turns. Only the future consolidation/dreaming process (Wave 5) will write to `MEMORY.md`.**

### Rationale

1. **Security boundary is correct.** The `EnsureWithinRoot` guard prevents path traversal out of `memory/`. Punching a hole for `MEMORY.md` weakens a safety invariant for a convenience that Wave 1 does not need. If we allow one exception, the boundary erodes.

2. **Matches the phased design.** The `improvement-memory-lifecycle` spec explicitly places `MEMORY.md` consolidation in Phase 4 (dreaming). Wave 1 is daily authoring only. Shipping write access to `MEMORY.md` now front-loads complexity from a future wave without the consolidation logic to make it useful.

3. **Prevents agent self-corruption.** Agents writing ad-hoc fragments to `MEMORY.md` during normal turns — without consolidation logic — will produce a messy, append-only dump that degrades context quality over time. Consolidation should be a deliberate, structured process (cron/dreaming), not opportunistic appending.

4. **Option B adds risk without value.** Extending path validation adds a special case to security-critical code for a feature that has no consumer yet. The cost is small, but the benefit is zero in Wave 1.

5. **Option C is premature.** Defining a `memory_consolidate` tool contract now, before the dreaming architecture is designed, would create a speculative API that will likely need revision. Better to wait until Wave 5 design is underway.

### What changes

- **AGENTS.md template**: Remove the "Append durable summaries to `MEMORY.md`" guidance. Replace with accurate Wave 1 wording that tells agents to use `memory_save` for daily notes only, and that `MEMORY.md` is loaded at startup but not written to during turns.
- **No production code changes.** The tool's current behaviour is the intended contract.
- **Kif's docs**: Should reflect that `MEMORY.md` is read-only context, not a write target, during Wave 1.

### Future contract (Wave 5+)

When dreaming/consolidation is designed, the consolidation process — not normal agent turns — will own `MEMORY.md` writes. This may be a cron job, a dedicated tool, or a platform-level process. That decision is deferred to the Wave 5 design.

## Impact

- **MemorySaveTool.cs**: No changes. Current path validation is the intended contract.
- **AGENTS.md template**: Wording update needed (see below).
- **Kif's documentation**: Align with this decision — `memory_save` is daily-note-only, `MEMORY.md` is read context.
- **Design spec**: No updates needed — Phase 4 already covers this correctly.

## AGENTS.md Template — Updated Wording

```markdown
## Memory Notes

- Use `memory_save` to record notes — writes go to `memory/YYYY-MM-DD.md` daily files.
- `MEMORY.md` is loaded at session start for long-term context but is read-only during turns.
- Future consolidation (dreaming) will update `MEMORY.md` from daily notes automatically.
```
