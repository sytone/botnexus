### 2026-04-06T16-12-30: User directives (agent config architecture)
**By:** Jon Bullen (via Copilot)
**What:**
1. Agent configs should be part of the main config.json, not scattered in separate files. There should be ONE source of truth for config.
2. Each agent should have a clear directory structure under ~/.botnexus/agents/{agent-id}/:
   - workspace/ - working context (SOUL.md, IDENTITY.md, USER.md, MEMORY.md, instructions)
   - sessions/ and other internal data folders
3. The workspace has working context/instructions. Internal folders have sessions and operational data.
4. All agent information should be easy to find - workspace for context, internal folders for sessions/data.
**Why:** User request - architectural clarity and single source of truth
