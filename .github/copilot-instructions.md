# Copilot Instructions — BotNexus

## Build Warnings

**All compiler warnings must be fixed before tasks are complete.** Warnings will be treated as errors via `TreatWarningsAsErrors=true` in `Directory.Build.props`. Do not ignore, suppress, or work around warnings — fix the underlying issue (nullable checks, async/await, unused code, etc.).

## Git Workflow

- **All file modifications and commits must happen in a dedicated worktree, never directly on `main`.** Create a worktree for every task. Keep the local `main` branch clean and always aligned to `origin/main`.
- **If local changes appear on `main`**, stop work immediately, create a worktree for the branch containing those changes, then continue:
  ```bash
  git worktree add ../botnexus-temp -b <type>/<issue>-<slug>
  cd ../botnexus-temp
  git cherry-pick <commits> # move changes from main, or reset main and reset worktree to origin/main
  cd ../botnexus && git reset --hard origin/main
  ```
- Then delete the temporary worktree after merging/pushing the PR.

## Dev Environment

- **BotNexus user config:** `C:\Users\<ALIAS>\.botnexus\config.json`
  This file contains gateway settings, provider configuration, agent definitions, session store paths, and compaction settings. Read it when you need to understand or modify the local BotNexus runtime configuration.

## Planning & Design Specs

- **Location:** `docs/planning/` (active) and `docs/planning/archived/` (done)
- **Index:** `docs/planning/INDEX.md` — master list of all specs with status
- **Skill:** `.github/skills/planning-management/SKILL.md` — full template, lifecycle, naming, and workflows
- **Key rule:** Load the planning-management skill before creating or managing specs
