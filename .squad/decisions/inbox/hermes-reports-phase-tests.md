# Hermes QA Note: Reports Phase Test Contract

Date: 2026-08-03  
Owner: Hermes (QA)
Issue: #245 Phase 3

## Decision Context
- Current implementation and tests align on `GET /api/agents/{agentId}/reports` and `GET /api/agents/{agentId}/reports/{name}`.
- Report list payload contract is object-wrapped: `{ reports: [...] }`.
- Report content contract uses `ReportContentDto` (`name`, `size`, `lastModifiedUtc`, `content`, `encoding`).

## QA Outcome
- Added/validated test coverage for:
  - backend reports list + content + missing + invalid extension/name + directory-as-report + traversal/symlink escape
  - Blazor ReportsPanel loading/empty/error/select/render + safe plain-text fallback + mobile CSS hooks/back button
  - client contract tests for report URL encoding and DTO naming

## Follow-up
- Prior design note referenced `/workspace/reports`; tests now lock the live `/reports` route contract to prevent accidental regressions.
