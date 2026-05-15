# Fry Decision Inbox — Reports Tab UI (Issue #245 Phase 3)

**Author:** Fry (Web Dev)  
**Date:** 2026-07-30  
**Branch:** `dev/botnexus/feature-portal-reports-tab`

## Decision Summary

The Reports tab is implemented as a read-only `ReportsPanel` that:

1. Lists reports from `GET /api/agents/{agentId}/reports`
2. Loads selected report content from `GET /api/agents/{agentId}/reports/{name}`
3. Renders markdown through `BotNexus.renderMarkdown` (marked + DOMPurify)
4. Falls back to escaped plain-text preview if markdown JS render is unavailable

## Why

- Aligns frontend contract with Bender's dedicated reports API and DTO shape (`{ reports: [...] }`, `ReportContentResponse`).
- Preserves the Phase 3 security boundary: read-only access, no writes, no realtime coupling, no report tool dependency.
- Reuses existing safe markdown helper instead of introducing raw HTML rendering.

## UX/Behavior Notes

- Mobile behavior mirrors workspace panel patterns (`mobile-list` / `mobile-viewer`, back button).
- Empty, loading, and error states are explicit in-list and in-viewer.
- Long content is truncated client-side with an explanatory notice.
- Conversation, Workspace, and Canvas tab behavior remain unchanged.

## Test Coverage Added

- `ReportsPanelTests` for loading, empty, selection/rendering, fallback, error, and mobile back behavior.
- `GatewayRestClientTests` for reports listing/content endpoint URLs and request path handling.
- `AgentPanelVerticalSliceTests` now verifies mounted reports panel in tab shell.
