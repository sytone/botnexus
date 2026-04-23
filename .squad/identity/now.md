---
updated_at: 2026-04-22T01:13:00Z
focus_area: Issue #12 World Defaults — PR #13 open for review
active_issues: [12]
status: pr_open
---

# What We're Focused On

**Issue #12 — World Settings and Defaults — PR #13 open for review (2026-04-22).**
5-wave delivery: Leela (triage + design review) → Farnsworth (config schema + `AgentConfigMerger`) → Hermes (tests wave 1) → Bender (effective-config API) → Fry (UI badges) → Hermes (API integration tests + UI wiring). 2024 tests passing, 0 failures. `agents.defaults` block, field-level merge, nullable presence tracking, `toolIds` replacement, cron world-level default, `GET /api/config/agents/{agentId}/effective` with provenance.

**Previous:** Sub-Agent Completion Wakeup Bug Fix delivered (2026-04-20 23:30Z). Fixed two root causes preventing sub-agent completion signals from waking parent session. 2,584 total tests passing, zero failures.

## Deferred

- Config validation (warn on typo keys) — follow-up improvement
- FileAccess deep merge alignment — separate improvement
