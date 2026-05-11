# Hermes Decision Inbox — No Session Delete Regression Guard

- **Date:** 2026-05-11
- **Author:** Hermes (QA)
- **Scope:** Blazor cron virtual cleanup + Gateway virtual cron archive contract

## Recommendation

Lock regression coverage to this invariant:

1. **Blazor client (`AgentInteractionService`)** must always route cron virtual cleanup via `ArchiveConversationAsync` (`DELETE /api/conversations/{cron-session-id}`) and must never call `DeleteSessionAsync`.
2. **Gateway (`ConversationsController`)** must continue returning **204 No Content** for `cron-session:{sessionId}` in linked, orphan, and missing-session cases, while sealing/preserving existing session records when present.

## Why this matters

Session deletion breaks conversation history guarantees and can remove persisted records users expect to retain. Archive-path cleanup keeps sidebar cleanup behavior while preserving history for reopen and audit scenarios.

## Evidence

- `tests/extensions/BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests/AgentInteractionServiceTests.cs`
- `tests/extensions/BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests/GatewayRestClientTests.cs`
- `tests/gateway/BotNexus.Gateway.Tests/ConversationsControllerHistoryTests.cs`
