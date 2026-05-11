# Fry Decision: Virtual Cron Cleanup Routes Through Session Deletion

**Date:** 2026-05-11
**Author:** Fry (Web Dev)
**Scope:** BlazorClient — AgentInteractionService
**Status:** Implemented

## Context

Deleting old cron conversations from the sidebar returned 404 because `ArchiveConversationAsync` sent `DELETE /api/conversations/{virtualId}` using the UI-only virtual key (e.g. `cron-session:cron:20260509002033:...`). These IDs don't exist as real conversations — they're projections created by `MergeVirtualCronSessions` from session summaries.

## Decision

- Virtual cron sessions now route cleanup to `DELETE /api/sessions/{ActiveSessionId}` via the existing `DeleteSessionAsync` REST method.
- Stale orphan projections (where `ActiveSessionId` is null) are cleaned up locally without any API call — the virtual row is simply removed from the sidebar.
- A legacy fallback also attempts session deletion when a non-virtual conversation archive returns failure and the conversation ID matches the `cron-session:` pattern.
- Normal (non-virtual) conversations continue using `DELETE /api/conversations/{id}` unchanged.

## Test Coverage

- `ArchiveConversationAsync_ForVirtualCronConversation_DeletesSessionAndRemovesConversation` — verifies session deletion routing
- `ArchiveConversationAsync_ForVirtualCronWithColonsInId_DeletesCorrectSession` — verifies URL encoding of colon-heavy IDs
- `ArchiveConversationAsync_ForStaleOrphanCronWithNoSession_RemovesLocallyWithoutApiCall` — verifies orphan cleanup
- `ArchiveConversationAsync_ForNormalConversation_StillUsesConversationArchive` — regression guard
- `GatewayRestClientTests` — new tests verify encoded session IDs and 404 handling

## Impact

- **Bender:** Backend also added gateway-side handling for legacy `cron-session:` IDs as a safety net.
- **Hermes:** Old test replaced with 4 new tests covering the routing, encoding, orphan, and regression scenarios.
