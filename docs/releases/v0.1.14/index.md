---
title: "Release v0.1.14"
description: "Release notes for BotNexus v0.1.14"
date: "2026-06-01"
---

# Release v0.1.14

> **Released:** 2026-06-01
>
> **Full diff:** [v0.1.13...v0.1.14](https://github.com/sytone/botnexus/compare/v0.1.13...v0.1.14)

## [0.1.14] - 2026-06-01

### ✨ Features

- **domain:** Introduce Citizen abstraction and migrate SessionParticipant (#522)
- **domain:** Add Citizen registry foundation (User, ChannelIdentity, IUserRegistry, ICitizenRegistry) (#525)
- **gateway:** Typed CitizenId Sender on InboundMessage + channel-boundary resolution (#528)
- **gateway:** Add Conversation.Initiator + ListForCitizenAsync (Phase 2b) (#530)
- **gateway:** Convert compaction to mark-not-delete with SessionEntry.IsHistory (#531) (#533)
- **gateway:** Canonical IConversationResetService + delete systemPromptInitialized flag (#536, #537) (#538)
- **scenarios:** Add VirtualWorld harness + first-wave bug-probing citizen scenarios (#543)
- **sessions:** Add ISessionStore.ListByConversationAsync + fix FileSessionStore orphan-on-restart bug (Phase 4.3 / F-7) (#546)
- **subagents:** Require ConversationId + eager pin (Phase 4 / F-6) (#547)
- **agents:** Route named↔named exchanges through IConversationStore (Phase 4 / F-3) (#548)
- **domain:** Add AgentKind on AgentDescriptor + route sub-agent isolation gate via typed property (Phase 5 / F-6 step 1) (#556)
- **gateway:** Migrate GatewayHost.ResolveSessionType off SessionId.IsSubAgent substring (Phase 5 / F-6 step 2) (#557)
- **gateway:** Migrate SessionsController.Seal off SessionId.IsSubAgent substring (Phase 5 / F-6 step 2b) (#559)
- **gateway:** SubAgentSpawnMode = Embody | Mirror discriminated union (#563)
- **agents:** Add integration-mock LLM provider with scripted responses (#588)
- **cli+tests:** Non-interactive provider commands, integration-mock provider, and CLI integration test harness (#594)
- **domain:** Add WorldId to Conversation (#613 P9-A) (#614)
- **sessions:** Backfill orphan sessions to per-agent legacy conversations (#616)
- **gateway:** Auto-archive A↔A conversations on exchange end (P9-C) (#625)
- **portal:** Update browser tab title to agent - conversation name (#610)
- **portal:** Show conversation ID in title tooltip for debug and e2e tests (#609)
- **gateway:** Flip Session.ConversationId to non-nullable (P9-B-2, closes #627) (#641)
- **cron:** Invert CronJob ↔ Conversation ownership (P9-D, closes #643 closes #640) (#644)
- Collapse SessionType — delete Cron/Soul/Heartbeat values (P9-E, closes #645) (#646)
- **channels:** Replace string conversationId with typed ChannelStreamTarget on stream adapter methods (PR1 of W-5, #677) (#679)
- **cli:** Add branded banner and refresh readme (#683)
- **channels:** Add ConversationId to ChannelStreamTarget; route SignalR by conversation (#682) (#684)
- **channels:** Drop hardcoded RequestedSessionId from TuiChannelAdapter (#691) (#695)

### 🐛 Bug Fixes

- **portal:** Add missing comma after resetTextareaHeight in chat.js (#469)
- **agents:** Inherit parent conversation id in sub-agent sessions (#468) (#470)
- **gateway:** Rebase concurrent additions over compaction (closes #532) (#540)
- **gateway:** Add caller authorization to Delete / Suspend / Resume session endpoints (closes #558) (#561)
- **gateway:** Per-session lock on cross-world relay write→prompt→reload window (#564)
- **gateway:** Cancellation must not seal cross-world relay session (#553) (#565)
- **gateway:** Heartbeat ack-prune must not clobber concurrent session activity (#573) (#574)
- **compaction:** Fix rogue cron conversation, add user notification, and evict stale handle (#602)
- **portal:** Cron sessions bleed into new conversation session ID (#607)
- **portal:** Replace literal x buttons with icons; title-case agent ID fallback (#639)

### 📖 Documentation

- **isolation:** Frame strategies as user-protection security boundary (#511)

### 🔨 Refactor

- **domain:** Migrate AgentId to Vogen value object (#513)
- **domain:** Migrate ConversationId and SessionId to Vogen value objects (#517)
- **gateway:** Centralise LLM-visible session-history projection (#534) (#535)
- **domain:** Remove ThreadId; fold native threads into ChannelAddress (#539)
- **domain:** Introduce JobId/RunId Vogen value objects (closes #501) (#541)
- **gateway:** Route cross-world receiver through IConversationStore + delete dead communicator + remove SessionId factories (#549)
- **gateway:** Replace IsObjectiveMet substring heuristic with finish_agent_exchange tool (#550)
- **gateway:** Close GatewaySession proxy gap; ban reach-through (#570)
- **gateway:** Extract SessionStreamReplay from GatewaySession (#575) (#576)
- **dispatching:** Lift InboundMessage overrides into typed InboundMessageContext (#580) (#581)
- **gateway:** Migrate router + host to typed InboundMessage routing hints (#582) (#585)
- **gateway:** Delete legacy InboundMessage routing fields; promote typed RoutingHints (#586) (#593)
- **gateway:** Unify compaction paths behind ISessionCompactionCoordinator (#608)
- **gateway:** Move Participants from Session to Conversation (P9-F, closes #657) (#659)
- **gateway:** List conversations by participant for responder-side visibility (P9-G, closes #661) (#663)
- **gateway:** Delete Session.AgentId; Conversation owns agent identity (P9-H, closes #662) (#664)
- **gateway:** Hydrate Session AgentId from Conversation; drop legacy agent_id column (P9-I, closes #674) (#675)

### 🧪 Testing

- **scenarios:** Add citizen scenario suite and virtual channel adapter (foundation) (#515)
- **channels:** Add SignalR reliability scenario suite (regression pins) (#542)
- **integration:** Add Copilot device-code probe + mock provider list verification (#597)
- **e2e:** CLI audit fixes + new BotNexus.Integration.E2E.Tests project (#600)
- **e2e:** Phase 1 — portal coverage expansion (#631)
- **e2e:** Phase 2 - portal settings, page title, tabs, config pages, AskUser, panels, header actions, agent dashboard (#638)
- **e2e:** Phase 3 - cron session isolation, parallel execution, session history tests (#642)
- **e2e:** Implement Playwright user-journey flows + portal data-testid hooks (#598) (#601)
- **e2e:** Phase 4 - compaction continuity tests (issue #655) (#658)

### ⚙️ Miscellaneous

- **gateway:** Rename single-shot CompletionReason "objectiveMet" to "singleShot" (#552) (#569)
- **tests:** Omit unvalidated loose tests/ projects from slnx (#617) (#619)
- **ci:** Add TIA script and reorganize workflows (#660)

[0.1.14]: https://github.com/sytone/botnexus/compare/v0.1.13...v0.1.14

