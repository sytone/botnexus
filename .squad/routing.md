# Work Routing

How to decide who handles what.

## Routing Table

| Work Type | Route To | Examples |
|-----------|----------|----------|
| Architecture & design | Leela | Extension point design, SOLID review, system boundaries, abstraction decisions |
| Core libraries & abstractions | Farnsworth | BotNexus.Core, BotNexus.Session, BotNexus.Command, BotNexus.Providers.* |
| Agent execution & channels | Bender | BotNexus.Agent, BotNexus.Channels.*, BotNexus.Gateway, BotNexus.Tools.*, BotNexus.Cron, BotNexus.Heartbeat |
| WebUI & frontend code | Fry | BotNexus.Extensions.Channels.SignalR.BlazorClient, Blazor components, Razor pages, SignalR client |
| Visual design & UX | Amy | UI mockups, component styling, design tokens, user experience, accessibility |
| Code review | Leela | Review PRs, check SOLID compliance, suggest improvements |
| Testing | Hermes | Write unit tests, integration tests, E2E tests, verify fixes |
| Consistency review | Nibbler | Cross-check docs ↔ code ↔ comments, stale reference detection, post-sprint audit |
| E2E simulation & deployment | Zapp | Multi-agent simulation, deployment lifecycle tests, scenario registry |
| Documentation | Kif | All docs — user guides, dev guides, API reference, GitHub Pages, style consistency |
| Scope & priorities | Leela | What to build next, trade-offs, decisions |
| Session logging | Scribe | Automatic — never needs routing |

## Issue Routing

| Label | Action | Who |
|-------|--------|-----|
| `squad` | Triage: analyze issue, assign `squad:{member}` label | Leela |
| `squad:leela` | Architecture and design work | Leela |
| `squad:farnsworth` | Core platform and provider work | Farnsworth |
| `squad:bender` | Runtime and channel work | Bender |
| `squad:fry` | WebUI and frontend work | Fry |
| `squad:amy` | UI design work | Amy |
| `squad:hermes` | Testing work | Hermes |

### How Issue Assignment Works

1. When a GitHub issue gets the `squad` label, **Leela** triages it — analyzing content, assigning the right `squad:{member}` label, and commenting with triage notes.
2. When a `squad:{member}` label is applied, that member picks up the issue in their next session.
3. Members can reassign by removing their label and adding another member's label.
4. The `squad` label is the "inbox" — untriaged issues waiting for Leela's review.

## Rules

1. **Eager by default** — spawn all agents who could usefully start work, including anticipatory downstream work.
2. **Scribe always runs** after substantial work, always as `mode: "background"`. Never blocks.
3. **Quick facts → coordinator answers directly.** Don't spawn an agent for "what port does the server run on?"
4. **When two agents could handle it**, pick the one whose domain is the primary concern.
5. **"Team, ..." → fan-out.** Spawn all relevant agents in parallel as `mode: "background"`.
6. **Anticipate downstream work.** If a feature is being built, spawn Hermes to write test cases from requirements simultaneously.
7. **Issue-labeled work** — when a `squad:{member}` label is applied to an issue, route to that member. Leela handles all `squad` (base label) triage.
