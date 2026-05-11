# Hermes Decision — Conversation Cleanup Test Strategy

- Date: 2026-05-11
- Decision: Treat cron virtual conversation cleanup as **session lifecycle** (DELETE /api/sessions/{id}) instead of conversation archive.
- Why: Cron rows in the Blazor sidebar are virtual projections from session summaries; archiving by conversation ID cannot remove them reliably.
- Test impact:
  - Blazor client tests verify cleanup affordance remains available for cron rows and routes through session deletion.
  - Gateway tests verify archiving a conversation closes its active session and clears active session linkage.
  - Existing router tests continue to validate archived conversations reopen on subsequent inbound activity with bindings preserved.
