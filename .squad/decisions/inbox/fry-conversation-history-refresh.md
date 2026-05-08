### Fry Decision: Conversation History Tail-Fetch Pattern

**Date:** 2026-05-08  
**Status:** Implemented

When loading conversation history for display, both `PortalLoadService` and `AgentInteractionService` now perform a tail-fetch: if the API reports `totalCount > limit`, a second request is made with `offset = totalCount - limit` to retrieve the newest entries.

**Why:** The history API paginates chronologically from offset 0 (oldest first). Long conversations (>200 entries) had their most recent messages cut off after UI refresh.

**Trade-off:** One extra HTTP call for conversations exceeding 200 entries. Acceptable for correctness.

**Backend recommendation for Bender:** Add server-side `order=desc` or `anchor=latest` parameter to the history API so clients can request newest entries in a single call.
