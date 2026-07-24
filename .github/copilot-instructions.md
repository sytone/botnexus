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

## PR and Commit Messages

- **The PR title is the squash-commit subject** and must be a valid Conventional Commit: `<type>(<scope>): <lowercase imperative, no trailing period>`.
- **PR bodies must follow `.github/pull_request_template.md`** — Summary (+ `Closes #N`), Root cause (fixes), Changes, Anti-reinvention, Tests, Validation, Risk & rollback, Merge notes.
- **Validation evidence must be numeric** (`Gateway.Tests 4026/0/1`), never "all tests pass".
- **Never weaken CI** to go green — no removed, renamed, or skipped tests and no lowered thresholds.
- Full rules, squash-body trailers (`Refs`, `Validated-by`, `Co-authored-by`), and the reviewer inspection order: [`docs/development/pr-and-commit-conventions.md`](../docs/development/pr-and-commit-conventions.md).

## Dev Environment

- **BotNexus user config:** `C:\Users\<ALIAS>\.botnexus\config.json`
  This file contains gateway settings, provider configuration, agent definitions, session store paths, and compaction settings. Read it when you need to understand or modify the local BotNexus runtime configuration.

## Planning & Design Specs

- **Location:** `docs/planning/` (active) and `docs/planning/archived/` (done)
- **Index:** `docs/planning/INDEX.md` — master list of all specs with status
- **Skill:** `.github/skills/planning-management/SKILL.md` — full template, lifecycle, naming, and workflows
- **Key rule:** Load the planning-management skill before creating or managing specs
