---
updated_at: 2026-04-06T00:00:00Z
focus_area: Post-Audit Stabilization — P1 Triage + AgentSession Design
active_issues: [15 remaining P1s, 24 P2s, AgentSession design, provider conformance tests]
status: between_sprints
---

# What We're Focused On

**Post-audit stabilization.** Port audit Phase 2 sprint is complete — all 15 P0s resolved, 14 P1s fixed alongside, 372 tests passing, 0 build errors. Architecture grade upgraded to A. Next sprint focuses on P1 triage, AgentSession design (AD-1 composition constraint locked), and building provider conformance tests.

## Current Status

✅ **Sprint:** Phase 2 complete — retrospective done  
✅ **Build:** 0 errors, 0 warnings  
✅ **Tests:** 372 passing (125 Core + 53 Anthropic + 41 OpenAI + 24 OpenAICompat + 15 Copilot + 33 AgentCore + 81 CodingAgent)  
✅ **P0s:** 25/25 closed across both audit phases  
📋 **Decisions:** 8 architecture decisions locked (AD-1 through AD-8)

## Completed — Phase 2 Sprint Results (18 commits, 5 agents)

- **Farnsworth:** 6 commits — OAuth stealth mode, strict mode fix, detectCompat refactor, thinking budgets, ExtraHigh guard, metadata defaults
- **Bender:** 7 commits — handleRunFailure, queue priority, tool lookup, compaction trigger, cut-point validation, convertToLlm mapping
- **Hermes:** 3 commits — 13 regression tests across 3 test suites
- **Kif:** 1 commit — training docs updated (~180KB, 6 modules)
- **Leela:** 1 commit — ExtraHigh clamping test/code alignment fix

## Remaining Backlog

### P1 — Next Sprint Candidates (15 items)
- Streaming error recovery and retry-after handling
- Model capability metadata per provider
- Context window pressure tracking with thresholds
- Compaction quality scoring
- Tool timeout configuration
- Session restore edge cases
- isStreaming semantics refinement
- Provider-level error categorization
- Rate limit backoff coordination
- Tool result size limits
- Agent.Subscribe cleanup on dispose
- Hook ordering under concurrent access
- ContinueAsync steering deduplication
- OpenAI Responses API streaming gaps
- Anthropic cache_control TTL optimization

### Deferred Architecture
- **AgentSession wrapper** — AD-1 composition constraint locked. Dedicated sprint needed.
- **Proxy implementation** — New project. Backlogged.
- **Provider conformance test suite** — Action item from both Phase 1 and Phase 2 retros.

## Next Sprint Priorities
1. P1 triage — rank by user-facing impact
2. Provider conformance test suite (quality gate investment)
3. AgentSession design sprint (AD-1 constraint ready)
4. Streaming error recovery (top P1)

## Key Architecture Decisions (Locked)

| ID | Decision | Rationale |
|----|----------|-----------|
| AD-1 | AgentSession = composition wrapper over Agent + SessionManager | Keep Agent as pure execution engine, no persistence concerns |
| AD-2 | No IsRunning property — use `Status == Running` | Single source of truth via existing enum |
| AD-3 | Compaction via Agent.Subscribe on TurnEndEvent | Mode-agnostic; fixes non-interactive compaction gap |
| AD-4 | detectCompat: dictionary + registration hook | Data-driven, extensible without editing detector |
| AD-5 | Strict mode: simple value flip | Bug not design gap |
| AD-6 | Thinking budgets aligned to pi-mono + Opus guard | Quality + safety |
| AD-7 | Cut-point walks backward to respect tool pairs | Prevents hallucinated tool results |
| AD-8 | convertToLlm maps SystemAgentMessage | Compaction summaries must survive |

## What's Done

### Port Audit Phase 2 (18 commits, 5 agents)
- 79 findings triaged (15 P0, 29 P1, 24 P2, 11 P3)
- All 15 P0s resolved, 14 P1s fixed alongside
- 8 architecture decisions locked pre-sprint
- 372 tests passing, 0 errors, 0 warnings
- Architecture grade: **A**

### Port Audit Phase 1 (12 commits, ~1,550 lines changed)
- 3-way parallel deep audit: 10 P0, 22 P1, 19 P2 found
- All 10 P0s fixed across 10 commits
- 101 regression tests added, 350 total tests green
- Training docs shipped (4,300+ lines)
- Architecture grade: **A−**

### Previous Milestones
- ✓ Archive phase (old projects moved)
- ✓ CodingAgent built (4 sprints, 25 commits)
- ✓ Skills system implemented
- ✓ OAuth token resilience + config safety

## Key Artifacts

- `.squad/decisions/inbox/leela-retro-port-audit-sprint-2.md` — Phase 2 retrospective
- `.squad/decisions/inbox/leela-design-review-port-audit-2.md` — Phase 2 architecture decisions
- `.squad/decisions/inbox/leela-port-audit-sprint-complete.md` — Phase 1 completion
- `docs/training/` — 6-module training guide with glossary

## Team

Farnsworth (Platform), Bender (Runtime), Hermes (Tests), Kif (Docs), Leela (Lead)
