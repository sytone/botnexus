# Common Projects Rules

## Dependency boundary

Projects in `src/common/` should ideally depend only on `src/domain/`. However, several projects currently have dependencies on agent and gateway layers due to their nature as agent tool providers.

**Allowed dependencies:**
- `src/domain/` — domain primitives
- NuGet packages

**Known violations (tech debt):**

| Project | Violation | Reason |
|---------|-----------|--------|
| `BotNexus.Cron` | refs Agent.Core, Agent.Providers.Core, Gateway.Contracts | Provides cron job scheduling that creates agent tools |
| `BotNexus.Memory` | refs Agent.Core, Gateway.Contracts | Provides memory tools for agents |
| `BotNexus.Tools` | refs Agent.Core, Agent.Providers.Core, Gateway.Contracts | Provides core agent tools (exec, process, etc.) |
| `BotNexus.Prompts` | ✅ compliant | Only refs Domain |

These projects are closer to gateway infrastructure than true "common" utilities. A future refactor may move them to `src/gateway/` or restructure to extract pure abstractions.

## Project structure

| Project | Purpose |
|---------|---------|
| `BotNexus.Cron` | Cron job scheduling and tool contributions |
| `BotNexus.Memory` | Agent memory store and search tools |
| `BotNexus.Prompts` | System prompt building and context file management |
| `BotNexus.Tools` | Core agent tools (workspace, sub-agent, session management) |
