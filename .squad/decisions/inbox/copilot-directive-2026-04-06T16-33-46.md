### 2026-04-06T16-33-46: User directive (workspace file templates)
**By:** Jon Bullen (via Copilot)
**What:** Workspace scaffold files (AGENTS.md, SOUL.md, TOOLS.md, BOOTSTRAP.md, IDENTITY.md, USER.md) should have default templates, not be created empty. Templates should be stored under the Gateway project as embedded resources that are easy to edit and update. Gateway pulls them at runtime when scaffolding a new agent workspace.
**Why:** User request - new agents should start with useful default content, templates maintained in the codebase
