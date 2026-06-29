# Git worktree config hardening (#1602)

The canonical clone of BotNexus runs with several worktrees under `Q:\repos\botnexus-wt`
and heavy automation (maintenance/grooming/build crons). `main` is fast-forward-only and
**all** development happens in worktrees, so nothing in this repo ever legitimately needs
`Q:\repos\botnexus/.git/config` to be `core.bare = true`.

## The bug

`.git/config` intermittently flips to `core.bare = true` — sometimes also gaining a
`core.worktree = …` line and/or identity pollution (`user.email = test@example.com`,
`user.name = test`). Because git refuses to operate a repo that is both bare and has a
work tree, **every** git command then fails:

```
warning: core.bare and core.worktree do not make sense
fatal: this operation must be run in a work tree
```

The nastiest manifestation: a flip during a *timed-out pre-commit hook* relocated a
branch tip onto another open PR's commit and leaked its files — an integrity risk, not
just a nuisance.

## Root cause (investigated 2026-06-29)

Every in-repo writer was eliminated with file:line evidence and reproductions:

- **pre-commit hook** — only `dotnet build`/`dotnet test`; no `git config`, no worktree, no clone.
- **`test-impacted.ps1`** — no `git config`.
- **`ci-pr-sync-main.ps1`** — worktree add/remove target a `GetTempPath()` dir, all `-C`-correct.
- **E2E `NewUserExperienceFixture`** — a temp `BOTNEXUS_HOME` sandbox; never touches `Q:\repos\botnexus/.git`.
- **Product code** — invokes `git` **zero** times; `UpdateCommand`'s runner is read-only (`fetch`/`rev-list`/`log`/`pull`/`rev-parse`) and always `-C "{repoRoot}"`.

Stress tests (8 parallel jobs × 80 iterations of concurrent `config core.bare false`,
`remote set-url`, `worktree add`/`remove`, and raw config copy-rename) never produced
`bare=true`, and *removing* the line yields `is-bare=false`, not true. **Benign
concurrency cannot create the flip — it requires an explicit external writer** (e.g. an
IDE git extension rescanning a worktree-heavy, branch-bloated config; the canonical clone
carried 50+ stale `[branch]` stanzas, a large rewrite surface). That writer is outside the
repo, so the durable fix makes the symptom **impossible to persist** rather than chasing it.

## The hardening

| Component | Role |
|-----------|------|
| `scripts/repo/guard-bare.ps1` | Idempotent detect/repair: resets `core.bare`, drops spurious `core.worktree`, repairs `test@example.com`/`test` identity (to `BOTNEXUS_GIT_EMAIL`/`BOTNEXUS_GIT_NAME`), `-Prune` removes stale `[branch]` stanzas. Exit 1 only if unrepairable. Leaves *real* bare repos alone. |
| `scripts/repo/githooks/pre-commit` | Runs the guard **before** building a commit (so a bare flip can't capture another branch's tip), then build + unit tests. |
| `scripts/repo/githooks/pre-push` | Runs the guard before a push touches the shared config — the path #1602 rides. |
| `scripts/repo/install-hooks.ps1` | Points `core.hooksPath` at the in-tree hooks (versioned, reviewed, shared by all worktrees). |

Automation calls `guard-bare.ps1` as a first-class step before git work, so a flip is reset
(and logged as an incident) instead of breaking the cycle.

```powershell
pwsh -NoProfile -File scripts/repo/install-hooks.ps1   # once per clone
pwsh -NoProfile -File scripts/repo/guard-bare.ps1 -Prune  # guard + shrink rewrite surface
```
