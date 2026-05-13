---
id: improvement-blazor-configuration-ui
title: "Blazor Configuration UI — Canvas Views, Root Config, Locations"
type: improvement
priority: medium
status: partially-delivered
created: 2025-07-17
updated: 2026-04-23
tags: [blazor, configuration, ux, locations]
---

# Improvement: Blazor Configuration UI

**Status:** partially-delivered
**Priority:** medium
**Updated:** 2026-04-23

## Delivery Status

### ✅ Delivered (Issue #12, 2026-04-23)
- **A. Config Section → Canvas View** — Configuration sections now open in the main canvas. Delivered as part of the Blazor redesign.
- **B. Root Configuration Node** — Clicking "Configuration" opens world-level settings. The new "🌍 World Settings" section (added in issue #12) shows `agents.defaults` and `cron.enabled`.

### ⏳ Still Needed
- **C. Locations Config Section** — No UI for managing locations despite the backend being shipped. Users cannot view or edit locations from the config UI.
- **D. Per-Agent Location Configuration** — Agent config panel has no locations section; no agent-level location overrides.

## Remaining Requirements

## Design

### UI Layout

```
┌─────────────────┬──────────────────────────────────┐
│ Config Sidebar   │  Main Canvas (detail view)       │
│                  │                                   │
│ ▸ Configuration  │  [World-level settings]           │
│   ▸ Providers    │  — or —                           │
│   ▸ Agents       │  [Selected section detail]        │
│   ▸ Locations    │                                   │
│   ▸ Extensions   │                                   │
│                  │                                   │
└─────────────────┴──────────────────────────────────┘
```

### Implementation Phases

**Phase 1:** Canvas routing — clicking any sidebar section opens its view in the main canvas.
**Phase 2:** Root config node → world-level settings view.
**Phase 3:** Add Locations section (CRUD UI wired to existing APIs).
**Phase 4:** Per-agent location configuration (schema extension + UI).

## Scope

- Blazor UI: sidebar navigation, canvas routing, new Locations components
- Backend: agent config schema extension for per-agent locations
- No changes to core location resolution logic (that's already shipped)

### C. Locations Config Section
- Add "Locations" as a new section in the configuration sidebar tree.
- List all configured locations with name, path, and description.
- Support add/edit/delete of locations.
- Wire to the existing `feature-location-management` backend APIs.

### D. Per-Agent Location Configuration
- In the agent configuration view, add a locations section.
- Allow assigning/overriding locations at the agent level.
- Agent-level locations should merge with or override world-level locations.
- Backend: extend agent config schema to support `locations` property.

## Scope (remaining)
- Blazor UI: new Locations config section component, per-agent location fields in AgentConfigPanel
- Backend: agent config schema extension for `locations` property
- No changes to core location resolution logic (already shipped)
