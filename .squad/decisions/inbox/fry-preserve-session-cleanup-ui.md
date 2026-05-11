# Decision: Cron Conversation Cleanup Preserves Session Records

**Author:** Fry (Web Dev)
**Date:** 2026-05-11
**Scope:** BlazorClient UI + API contract
**Status:** Implemented

## Context

The prior implementation routed virtual cron conversation cleanup through `DELETE /api/sessions/{sessionId}`, which permanently deletes session records and history. This was a data-loss regression — users expected "close/archive" semantics that hide conversations while preserving underlying session data.

## Decision

- Virtual cron conversation cleanup now routes through `DELETE /api/conversations/{conversationId}` (the same `ArchiveConversationAsync` path used for regular conversations).
- The conversation ID sent to the backend is the full `cron-session:{sessionId}` string, URL-encoded.
- Backend returns 204 idempotently for `cron-session:` prefixed IDs even when no backing session exists (handles stale orphans).
- `DeleteSessionAsync` removed from `IGatewayRestClient` — it was added solely for this cleanup path and has no other UI use.
- Stale orphans (no `ActiveSessionId`) still call the conversations endpoint rather than being cleaned up locally-only, since the backend handles them gracefully.

## Rationale

1. **Session preservation** — Sessions contain execution history that must survive conversation cleanup.
2. **Unified cleanup path** — All conversation types (regular, cron, legacy projections) use the same API.
3. **Idempotent backend** — No need for client-side branching logic; backend handles all cron-session: variants.
4. **Reduced surface area** — Removing DeleteSessionAsync from the UI client prevents accidental session destruction.

## Impact

- **Bender:** Backend already returns 204 for cron-session: archives. No changes needed.
- **Hermes:** Tests rewritten to assert conversations endpoint is called (not sessions) and that session delete is never invoked.
