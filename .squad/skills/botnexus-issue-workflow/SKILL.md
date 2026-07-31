---
name: botnexus-issue-workflow
description: Use when creating GitHub issues, opening worktrees, naming branches, or managing the full lifecycle of a work item on sytone/botnexus. Enforces consistent issue titles, label application, worktree creation, branch naming, PR linking, and cleanup. Triggers on: "create an issue", "open a worktree", "start work on", "file a bug", "add an issue for", "new feature issue", "track this as an issue".
metadata:
  domain: workflow
  confidence: high
  repo: sytone/botnexus
  fork: larry-fox-lobster/botnexus
---

# BotNexus Issue Workflow

## Issue Title Convention

Format: `[Area] Short imperative description`

**Area prefixes:**

| Area | When to use |
|---|---|
| `[Portal]` | Blazor UI, SignalR client, chat panel, sidebar |
| `[Gateway]` | Gateway API, routing, session management, SignalR hub |
| `[CLI]` | CLI commands, `botnexus` tool |
| `[Sessions]` | Session store, compaction, lifecycle |
| `[Conversations]` | Conversation model, bindings, history |
| `[Agents]` | Agent execution, supervisor, sub-agents |
| `[Providers]` | LLM providers (Copilot, Ollama, Anthropic, OpenAI) |
| `[Extensions]` | Extension loading, MCP, tools |
| `[Cron]` | Scheduled tasks |
| `[Memory]` | Memory persistence, indexing |
| `[Config]` | Platform config, hot-reload |
| `[Docs]` | Documentation, API reference, architecture |
| `[Tests]` | Test infrastructure, coverage |
| `[Infra]` | Scripts, CI, sync, deployment |

**Examples:**
- `[Portal] Conversation-first sidebar and chat UI`
- `[Sessions] SQLite store global lock blocks multi-agent concurrency`
- `[CLI] Global --target option for all commands`
- `[Gateway] Tool execution timeout and stuck-turn recovery`

## Labels

Always apply these three labels when creating issues. The fork (`larry-fox-lobster`) lacks write permission — remind the user to apply labels in GitHub UI if the CLI call fails.

| Label | Apply when |
|---|---|
| `type:bug` | Something broken |
| `enhancement` | New feature or improvement |
| `squad` | Routed to the squad for implementation |
| `go:yes` | Ready to implement (design complete) |
| `go:needs-research` | Needs investigation before work starts |
| `release:backlog` | Not yet targeted to a milestone |
| `release:v0.x.0` | Targeted to a specific version |

Minimum labels on every issue: one type label + `squad` + one release label.

## Creating an Issue

```bash
gh issue create --repo sytone/botnexus \
  --title "[Area] Short imperative description" \
  --label "type:bug,squad,release:backlog" \
  --body "$(cat path/to/spec.md)"
```

If label application fails (permissions), note the issue number and remind the user:
> Issue #N created. Please apply labels manually: `type:bug`, `squad`, `release:backlog`.

Capture the issue number from the URL in the output — it drives the branch and worktree names.

## Worktree and Branch Naming

Always use worktrees for new work. Never branch off a feature branch.

```bash
# Format
git worktree add ../botnexus-wt-<N> -b <type>/<N>-<short-slug>

# Types: feat | fix | improvement | refactor | docs
# N = GitHub issue number
# short-slug = 2-4 word kebab-case summary

# Examples
git worktree add ../botnexus-wt-37 -b feat/37-portal-conversation-ui
git worktree add ../botnexus-wt-23 -b fix/23-sqlite-session-lock
git worktree add ../botnexus-wt-36 -b improvement/36-cli-global-target
```

**Main repo stays on `main` always.** Worktrees are the only place feature/fix branches live.

## Spawning Squad Agents on a Worktree

Always pass `TEAM_ROOT` and `REPO` pointing at the worktree, not the main repo:

```
TEAM_ROOT=/home/larry/projects/botnexus-wt-<N>
REPO=/home/larry/projects/botnexus-wt-<N>
BRANCH=<type>/<N>-<short-slug>
ISSUE=<N>
```

## Commit Convention

Reference the issue number in commit messages:

```
feat(portal): conversation-first sidebar and chat UI (#37)
fix(sessions): remove global lock from SqliteSessionStore (#23)
```

PRs opened with `--body` should include `Closes #<N>` so the issue auto-closes on merge.

## PR Creation

```bash
gh pr create \
  --repo sytone/botnexus \
  --title "[Area] Short imperative description (#N)" \
  --base main \
  --head larry-fox-lobster:<branch> \
  --body "Closes #<N>

## What changed
...

## Test results
..."
```

## Worktree Cleanup

After a PR merges:

```powershell
cd ~/projects/botnexus
git pull origin main --ff-only
# Helper: bounded retry, structured 'locked' outcome, deletes the local branch
# only when the worktree removal actually succeeded.
pwsh -NoProfile -File scripts/repo/Remove-Worktree.ps1 -WorktreePath ../botnexus-wt-<N> -DeleteBranch
git push fork --delete <branch> # remote branch on fork
```

Use the hardened helper `scripts/repo/Remove-Worktree.ps1`: it retries boundedly, returns a structured `locked` outcome when Windows file locks hold the directory, and never deletes the branch unless removal succeeded (issue #2104). Never chain `git worktree remove ...` straight into `git branch -d/-D ...` - on a failed removal that orphans the directory and strands the commits.

## Full Lifecycle Checklist

- [ ] Issue created with `[Area]` prefix title
- [ ] Labels applied (or user notified to apply)
- [ ] Worktree created: `../botnexus-wt-<N>`
- [ ] Branch: `<type>/<N>-<short-slug>`
- [ ] Squad agents spawned with worktree as `TEAM_ROOT`
- [ ] PR opened with `Closes #<N>`
- [ ] Worktree removed after merge

## Existing Issues Reference

See `references/open-issues.md` for the current open issue list with numbers, areas, and status.
