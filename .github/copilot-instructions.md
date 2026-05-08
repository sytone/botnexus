# Copilot Instructions — BotNexus

## Git Workflow

- **Worktrees are allowed when explicitly requested.** If a user asks you to create or use a worktree, do so. Otherwise, work directly on the current branch without creating a worktree.

## Dev Environment

- **BotNexus user config:** `C:\Users\<ALIAS>\.botnexus\config.json`
  This file contains gateway settings, provider configuration, agent definitions, session store paths, and compaction settings. Read it when you need to understand or modify the local BotNexus runtime configuration.

## Planning & Design Specs

- **Location:** `docs/planning/` (active) and `docs/planning/archived/` (done)
- **Index:** `docs/planning/INDEX.md` — master list of all specs with status
- **Skill:** `.github/skills/planning-management/SKILL.md` — full template, lifecycle, naming, and workflows
- **Key rule:** Load the planning-management skill before creating or managing specs
