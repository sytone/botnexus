# Decision: OpenClaw Memory Alignment — Resolved Design Questions

**Date:** 2026-05-08  
**Decided by:** Sytone (Product Owner)  
**Recorded by:** Kif (Documentation Engineer)  
**Spec:** `docs/planning/botnexus-openclaw-memory-alignment/design-spec.md` §7

---

## Summary

Six open design questions in the OpenClaw Memory Alignment spec have been resolved. These decisions constrain the implementation across all five waves.

## Decisions

| # | Topic | Decision |
|---|-------|----------|
| 1 | Daily notes format | Plain Markdown only — no YAML frontmatter requirement |
| 2 | File storage path | Default canonical path + optional per-agent override in config |
| 3 | Embedding provider | Embeddings optional; support local + cloud behind `IEmbeddingProvider` abstraction |
| 4 | Consolidation trigger | Automatic schedule (cron) + manual override; no agent-initiated |
| 5 | Index rebuild / embedding cache | < 30s FTS rebuild acceptable; cache embeddings by content hash in SQLite |
| 6 | AGENTS.md generation | Minimal memory authoring instructions (3–5 lines); detailed contract stays in tool descriptions |

## Implications for Implementers

- **Wave 1**: Daily note creation must not inject YAML frontmatter. Memory path resolver must check agent config for override before falling back to default.
- **Wave 2**: FTS index rebuild target is 30s. Embedding cache table should be created in Wave 2 schema even if embeddings aren't used until Wave 3.
- **Wave 3**: `IEmbeddingProvider` is optional at DI registration. All search paths must degrade gracefully to FTS-only when no provider is registered.
- **Wave 5**: Consolidation service needs both a cron trigger (configurable interval) and a CLI/system-action manual trigger.
- **AGENTS.md template**: Keep memory section ≤ 5 lines. Do not duplicate tool parameter docs.
