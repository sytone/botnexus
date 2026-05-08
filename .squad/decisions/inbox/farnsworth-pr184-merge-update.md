# Decision: PR #184 merge update from main

**Author:** Farnsworth (Platform Dev)  
**Date:** 2026-05-07  
**Status:** Implemented  
**Scope:** `fix/24-tool-timeouts` merge sync

## Context

Merging `origin/main` into `fix/24-tool-timeouts` produced conflicts in:

1. `.squad/decisions-archive.md` (add/add)
2. `tests/BotNexus.Gateway.Tests/InProcessIsolationStrategyTests.cs` (content conflict)

## Decision

1. Use `origin/main` content for `.squad/decisions-archive.md` to preserve the canonical archive history and avoid restoring placeholder text.
2. In `InProcessIsolationStrategyTests`, keep the memory-tool regression tests introduced on main and keep PR #184 timeout propagation coverage.

## Rationale

- Mainline archive content is authoritative for historical decision records.
- Keeping both test sets preserves platform guardrails (`memory_save` boundary) while retaining PR #184 behavior verification (`toolTimeoutSeconds` propagation).
