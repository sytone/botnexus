### 2026-04-06T16-30-16: User directive (system prompt loading)
**By:** Jon Bullen (via Copilot)
**What:** systemPromptFile should be an ordered array of files (systemPromptFiles). The array order determines load order. If empty/missing, use default load order: AGENTS.md, SOUL.md, TOOLS.md, BOOTSTRAP.md (removed after first run), IDENTITY.md, USER.md. All resolved from agent workspace directory.
**Why:** User request - flexible prompt composition with sensible defaults
