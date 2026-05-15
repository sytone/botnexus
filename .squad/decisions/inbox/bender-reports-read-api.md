# Bender Decision Note: Reports Read API (Issue #245 Phase 3)

## Scope Delivered
- Added `ReportsController` with read-only endpoints:
  - `GET /api/agents/{agentId}/reports`
  - `GET /api/agents/{agentId}/reports/{**name}`
- Scoped reads to `{workspace}/reports` only.
- Enforced markdown-only report names (`.md`) and blocked rooted/segmented/invalid names.
- Kept phase read-only (no PUT/DELETE/tool integration/SignalR).

## Security Model
- Reused `DefaultPathValidator` workspace-jail checks from Phase 2.
- Extracted shared path/symlink resolution into `WorkspacePathSecurity` and reused in `WorkspaceController` + `ReportsController`.
- Added explicit containment check that final resolved report target remains under resolved reports root (prevents symlink escape from reports folder).

## API Contract
- List response DTO: `ReportsListResponse { reports: ReportListItemDto[] }`.
- Content response DTO: `ReportContentResponse` (name, size, lastModifiedUtc, content, encoding).

## Validation
- `dotnet build BotNexus.slnx --nologo --tl:off -warnaserror` ✅
- `dotnet test tests\gateway\BotNexus.Gateway.Tests\BotNexus.Gateway.Tests.csproj --no-build --nologo --tl:off --filter "FullyQualifiedName~ReportsControllerTests|FullyQualifiedName~ReportsControllerIntegrationTests|FullyQualifiedName~WorkspacePathSecurityTests|FullyQualifiedName~WorkspaceControllerIntegrationTests"` ✅
- `dotnet test BotNexus.slnx --no-build --nologo --tl:off` ✅

## Notes / Deferrals
- No workspace write APIs, report publishing tool, or realtime report events in this phase.
