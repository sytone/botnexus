---
name: "squad-lifecycle"
description: "Team setup, casting, member management, and integration flows. Read when .squad/ needs initialization or roster changes."
domain: "orchestration"
confidence: "high"
source: "extracted from squad.agent.md v0.9.1"
---

## Configuration Check

Run this check when the coordinator references this skill:

1. Does `.squad/team.md` exist? (fall back to `.ai-team/team.md` for repos migrating from older installs)
2. Does it have entries under `## Members`?

| Result | Action |
|--------|--------|
| No `team.md` found | → Run Init Mode (Phase 1 below) |
| `team.md` exists but `## Members` is empty | → Run Init Mode (treat as unconfigured) |
| `team.md` exists with roster entries but no `.squad/casting/` | → Run Casting Migration (see below) |
| `team.md` exists with roster entries and casting state | → ✅ Configured. Return to coordinator — Team Mode is ready. |

If GitHub workflows are not installed, note it:
- Check: Do files matching `.github/workflows/squad-*.yml` exist?
- If not, inform the user: *"Squad GitHub workflows are not installed. Copy them from `.squad/templates/workflows/` to `.github/workflows/` to enable issue triage, label sync, and heartbeat automation."*
- This is informational — don't block on it. Setup can proceed without workflows.

---

## Init Mode — Phase 1: Propose the Team

No team exists yet. Propose one — but **DO NOT create any files until the user confirms.**

1. **Identify the user.** Run `git config user.name` to learn who you're working with. Use their name in conversation (e.g., *"Hey Brady, what are you building?"*). Store their name (NOT email) in `team.md` under Project Context. **Never read or store `git config user.email` — email addresses are PII and must not be written to committed files.**
2. Ask: *"What are you building? (language, stack, what it does)"*
3. **Cast the team.** Before proposing names, run the Casting & Persistent Naming algorithm (see that section):
   - Determine team size (typically 4–5 + Scribe).
   - Determine assignment shape from the user's project description.
   - Derive resonance signals from the session and repo context.
   - Select a universe. Allocate character names from that universe.
   - Scribe is always "Scribe" — exempt from casting.
   - Ralph is always "Ralph" — exempt from casting.
4. Propose the team with their cast names. Example (names will vary per cast):

```
🏗️  {CastName1}  — Lead          Scope, decisions, code review
⚛️  {CastName2}  — Frontend Dev  React, UI, components
🔧  {CastName3}  — Backend Dev   APIs, database, services
🧪  {CastName4}  — Tester        Tests, quality, edge cases
📋  Scribe       — (silent)      Memory, decisions, session logs
🔄  Ralph        — (monitor)     Work queue, backlog, keep-alive
```

5. Use the `ask_user` tool to confirm the roster. Provide choices so the user sees a selectable menu:
   - **question:** *"Look right?"*
   - **choices:** `["Yes, hire this team", "Add someone", "Change a role"]`

**⚠️ STOP. Your response ENDS here. Do NOT proceed to Phase 2. Do NOT create any files or directories. Wait for the user's reply.**

---

## Init Mode — Phase 2: Create the Team

**Trigger:** The user replied to Phase 1 with confirmation ("yes", "looks good", or similar affirmative), OR the user's reply to Phase 1 is a task (treat as implicit "yes").

> If the user said "add someone" or "change a role," go back to Phase 1 step 3 and re-propose. Do NOT enter Phase 2 until the user confirms.

6. Create the `.squad/` directory structure (see `.squad/templates/` for format guides or use the standard structure: team.md, routing.md, ceremonies.md, decisions.md, decisions/inbox/, casting/, agents/, orchestration-log/, skills/, log/).

**Casting state initialization:** Copy `.squad/templates/casting-policy.json` to `.squad/casting/policy.json` (or create from defaults). Create `registry.json` (entries: persistent_name, universe, created_at, legacy_named: false, status: "active") and `history.json` (first assignment snapshot with unique assignment_id).

**Seeding:** Each agent's `history.md` starts with the project description, tech stack, and the user's name so they have day-1 context. Agent folder names are the cast name in lowercase (e.g., `.squad/agents/ripley/`). The Scribe's charter includes maintaining `decisions.md` and cross-agent context sharing.

**Team.md structure:** `team.md` MUST contain a section titled exactly `## Members` (not "## Team Roster" or other variations) containing the roster table. This header is hard-coded in GitHub workflows (`squad-heartbeat.yml`, `squad-issue-assign.yml`, `squad-triage.yml`, `sync-squad-labels.yml`) for label automation. If the header is missing or titled differently, label routing breaks.

**Merge driver for append-only files:** Create or update `.gitattributes` at the repo root to enable conflict-free merging of `.squad/` state across branches:
```
.squad/decisions.md merge=union
.squad/agents/*/history.md merge=union
.squad/log/** merge=union
.squad/orchestration-log/** merge=union
```
The `union` merge driver keeps all lines from both sides, which is correct for append-only files. This makes worktree-local strategy work seamlessly when branches merge — decisions, memories, and logs from all branches combine automatically.

7. Say: *"✅ Team hired. Try: '{FirstCastName}, set up the project structure'"*

8. **Post-setup input sources** (optional — ask after team is created, not during casting):
   - PRD/spec: *"Do you have a PRD or spec document? (file path, paste it, or skip)"* → If provided, follow PRD Mode flow
   - GitHub issues: *"Is there a GitHub repo with issues I should pull from? (owner/repo, or skip)"* → If provided, follow GitHub Issues Mode flow
   - Human members: *"Are any humans joining the team? (names and roles, or just AI for now)"* → If provided, add per Human Team Members section
   - Copilot agent: *"Want to include @copilot? It can pick up issues autonomously. (yes/no)"* → If yes, follow Copilot Coding Agent Member section and ask about auto-assignment
   - These are additive. Don't block — if the user skips or gives a task instead, proceed immediately.

---

## Casting & Persistent Naming

Agent names are drawn from a single fictional universe per assignment. Names are persistent identifiers — they do NOT change tone, voice, or behavior. No role-play. No catchphrases. No character speech patterns. Names are easter eggs: never explain or document the mapping rationale in output, logs, or docs.

### Universe Allowlist

**On-demand reference:** Read `.squad/templates/casting-reference.md` for the full universe table, selection algorithm, and casting state file schemas. Only loaded during Init Mode or when adding new team members.

**Rules (always loaded):**
- ONE UNIVERSE PER ASSIGNMENT. NEVER MIX.
- 15 universes available (capacity 6–25). See reference file for full list.
- Selection is deterministic: score by size_fit + shape_fit + resonance_fit + LRU.
- Same inputs → same choice (unless LRU changes).

### Name Allocation

After selecting a universe:

1. Choose character names that imply pressure, function, or consequence — NOT authority or literal role descriptions.
2. Each agent gets a unique name. No reuse within the same repo unless an agent is explicitly retired and archived.
3. **Scribe is always "Scribe"** — exempt from casting.
4. **Ralph is always "Ralph"** — exempt from casting.
5. **@copilot is always "@copilot"** — exempt from casting. If the user says "add team member copilot" or "add copilot", this is the GitHub Copilot coding agent. Do NOT cast a name — follow the Copilot Coding Agent Member section instead.
5. Store the mapping in `.squad/casting/registry.json`.
5. Record the assignment snapshot in `.squad/casting/history.json`.
6. Use the allocated name everywhere: charter.md, history.md, team.md, routing.md, spawn prompts.

### Overflow Handling

If agent_count grows beyond available names mid-assignment, do NOT switch universes. Apply in order:

1. **Diegetic Expansion:** Use recurring/minor/peripheral characters from the same universe.
2. **Thematic Promotion:** Expand to the closest natural parent universe family that preserves tone (e.g., Star Wars OT → prequel characters). Do not announce the promotion.
3. **Structural Mirroring:** Assign names that mirror archetype roles (foils/counterparts) still drawn from the universe family.

Existing agents are NEVER renamed during overflow.

### Casting State Files

**On-demand reference:** Read `.squad/templates/casting-reference.md` for the full JSON schemas of policy.json, registry.json, and history.json.

The casting system maintains state in `.squad/casting/` with three files: `policy.json` (config), `registry.json` (persistent name registry), and `history.json` (universe usage history + snapshots).

### Migration — Already-Squadified Repos

When `.squad/team.md` exists but `.squad/casting/` does not:

1. **Do NOT rename existing agents.** Mark every existing agent as `legacy_named: true` in the registry.
2. Initialize `.squad/casting/` with default policy.json, a registry.json populated from existing agents, and empty history.json.
3. For any NEW agents added after migration, apply the full casting algorithm.
4. Optionally note in the orchestration log that casting was initialized (without explaining the rationale).

---

## Team Member Management

### Adding Team Members

If the user says "I need a designer" or "add someone for DevOps":
1. **Allocate a name** from the current assignment's universe (read from `.squad/casting/history.json`). If the universe is exhausted, apply overflow handling (see Casting & Persistent Naming → Overflow Handling).
2. **Check plugin marketplaces.** If `.squad/plugins/marketplaces.json` exists and contains registered sources, browse each marketplace for plugins matching the new member's role or domain (e.g., "azure-cloud-development" for an Azure DevOps role). Use the CLI: `squad plugin marketplace browse {marketplace-name}` or read the marketplace repo's directory listing directly. If matches are found, present them: *"Found '{plugin-name}' in {marketplace} — want me to install it as a skill for {CastName}?"* If the user accepts, copy the plugin content into `.squad/skills/{plugin-name}/SKILL.md` or merge relevant instructions into the agent's charter. If no marketplaces are configured, skip silently. If a marketplace is unreachable, warn (*"⚠ Couldn't reach {marketplace} — continuing without it"*) and continue.
3. Generate a new charter.md + history.md (seeded with project context from team.md), using the cast name. If a plugin was installed in step 2, incorporate its guidance into the charter.
4. **Update `.squad/casting/registry.json`** with the new agent entry.
5. Add to team.md roster.
6. Add routing entries to routing.md.
7. Say: *"✅ {CastName} joined the team as {Role}."*

### Removing Team Members

If the user wants to remove someone:
1. Move their folder to `.squad/agents/_alumni/{name}/`
2. Remove from team.md roster
3. Update routing.md
4. **Update `.squad/casting/registry.json`**: set the agent's `status` to `"retired"`. Do NOT delete the entry — the name remains reserved.
5. Their knowledge is preserved, just inactive.

### Plugin Marketplace

**On-demand reference:** Read `.squad/templates/plugin-marketplace.md` for marketplace state format, CLI commands, installation flow, and graceful degradation when adding team members.

**Core rules (always loaded):**
- Check `.squad/plugins/marketplaces.json` during Add Team Member flow (after name allocation, before charter)
- Present matching plugins for user approval
- Install: copy to `.squad/skills/{plugin-name}/SKILL.md`, log to history.md
- Skip silently if no marketplaces configured

---

## Integration Flows

### GitHub Issues Mode

Squad can connect to a GitHub repository's issues and manage the full issue → branch → PR → review → merge lifecycle.

#### Prerequisites

Before connecting to a GitHub repository, verify that the `gh` CLI is available and authenticated:

1. Run `gh --version`. If the command fails, tell the user: *"GitHub Issues Mode requires the GitHub CLI (`gh`). Install it from https://cli.github.com/ and run `gh auth login`."*
2. Run `gh auth status`. If not authenticated, tell the user: *"Please run `gh auth login` to authenticate with GitHub."*
3. **Fallback:** If the GitHub MCP server is configured (check available tools), use that instead of `gh` CLI. Prefer MCP tools when available; fall back to `gh` CLI.

#### Triggers

| User says | Action |
|-----------|--------|
| "pull issues from {owner/repo}" | Connect to repo, list open issues |
| "work on issues from {owner/repo}" | Connect + list |
| "connect to {owner/repo}" | Connect, confirm, then list on request |
| "show the backlog" / "what issues are open?" | List issues from connected repo |
| "work on issue #N" / "pick up #N" | Route issue to appropriate agent |
| "work on all issues" / "start the backlog" | Route all open issues (batched) |

### PRD Mode

Squad can ingest a PRD and use it as the source of truth for work decomposition and prioritization.

**On-demand reference:** Read `.squad/templates/prd-intake.md` for the full intake flow, Lead decomposition spawn template, work item presentation format, and mid-project update handling.

#### Triggers

| User says | Action |
|-----------|--------|
| "here's the PRD" / "work from this spec" | Expect file path or pasted content |
| "read the PRD at {path}" | Read the file at that path |
| "the PRD changed" / "updated the spec" | Re-read and diff against previous decomposition |
| (pastes requirements text) | Treat as inline PRD |

**Core flow:** Detect source → store PRD ref in team.md → spawn Lead (sync, premium bump) to decompose into work items → present table for approval → route approved items respecting dependencies.

### Human Team Members

Humans can join the Squad roster alongside AI agents. They appear in routing, can be tagged by agents, and the coordinator pauses for their input when work routes to them.

**On-demand reference:** Read `.squad/templates/human-members.md` for triggers, comparison table, adding/routing/reviewing details.

**Core rules (always loaded):**
- Badge: 👤 Human. Real name (no casting). No charter or history files.
- NOT spawnable — coordinator presents work and waits for user to relay input.
- Non-dependent work continues immediately — human blocks are NOT a reason to serialize.
- Stale reminder after >1 turn: `"📌 Still waiting on {Name} for {thing}."`
- Reviewer rejection lockout applies normally when human rejects.
- Multiple humans supported — tracked independently.

### Copilot Coding Agent Member

The GitHub Copilot coding agent (`@copilot`) can join the Squad as an autonomous team member. It picks up assigned issues, creates `copilot/*` branches, and opens draft PRs.

**On-demand reference:** Read `.squad/templates/copilot-agent.md` for adding @copilot, comparison table, roster format, capability profile, auto-assign behavior, lead triage, and routing details.

**Core rules (always loaded):**
- Badge: 🤖 Coding Agent. Always "@copilot" (no casting). No charter — uses `copilot-instructions.md`.
- NOT spawnable — works via issue assignment, asynchronous.
- Capability profile (🟢/🟡/🔴) lives in team.md. Lead evaluates issues against it during triage.
- Auto-assign controlled by `<!-- copilot-auto-assign: true/false -->` in team.md.
- Non-dependent work continues immediately — @copilot routing does not serialize the team.

---

## Worktree Lifecycle Management

When worktree mode is enabled, the coordinator creates dedicated worktrees for issue-based work. This gives each issue its own isolated branch checkout without disrupting the main repo.

**Worktree mode activation:**
- Explicit: `worktrees: true` in project config (squad.config.ts or package.json `squad` section)
- Environment: `SQUAD_WORKTREES=1` set in environment variables
- Default: `false` (backward compatibility — agents work in the main repo)

**Creating worktrees:**
- One worktree per issue number
- Multiple agents on the same issue share a worktree
- Path convention: `{repo-parent}/{repo-name}-{issue-number}`
  - Example: Working on issue #42 in `C:\src\squad` → worktree at `C:\src\squad-42`
- Branch: `squad/{issue-number}-{kebab-case-slug}` (created from base branch, typically `main`)

**Dependency management:**
- After creating a worktree, link `node_modules` from the main repo to avoid reinstalling
- Windows: `cmd /c "mklink /J {worktree}\node_modules {main-repo}\node_modules"`
- Unix: `ln -s {main-repo}/node_modules {worktree}/node_modules`
- If linking fails (permissions, cross-device), fall back to `npm install` in the worktree

**Reusing worktrees:**
- Before creating a new worktree, check if one exists for the same issue
- `git worktree list` shows all active worktrees
- If found, reuse it (cd to the path, verify branch is correct, `git pull` to sync)
- Multiple agents can work in the same worktree concurrently if they modify different files

**Cleanup:**
- After a PR is merged, the worktree should be removed
- `git worktree remove {path}` + `git branch -d {branch}`
- Ralph heartbeat can trigger cleanup checks for merged branches

### Pre-Spawn: Worktree Setup

When spawning an agent for issue-based work (user request references an issue number, or agent is working on a GitHub issue):

**1. Check worktree mode:**
- Is `SQUAD_WORKTREES=1` set in the environment?
- Or does the project config have `worktrees: true`?
- If neither: skip worktree setup → agent works in the main repo (existing behavior)

**2. If worktrees enabled:**

a. **Determine the worktree path:**
   - Parse issue number from context (e.g., `#42`, `issue 42`, GitHub issue assignment)
   - Calculate path: `{repo-parent}/{repo-name}-{issue-number}`
   - Example: Main repo at `C:\src\squad`, issue #42 → `C:\src\squad-42`

b. **Check if worktree already exists:**
   - Run `git worktree list` to see all active worktrees
   - If the worktree path already exists → **reuse it**:
     - Verify the branch is correct (should be `squad/{issue-number}-*`)
     - `cd` to the worktree path
     - `git pull` to sync latest changes
     - Skip to step (e)

c. **Create the worktree:**
   - Determine branch name: `squad/{issue-number}-{kebab-case-slug}` (derive slug from issue title if available)
   - Determine base branch (typically `main`, check default branch if needed)
   - Run: `git worktree add {path} -b {branch} {baseBranch}`
   - Example: `git worktree add C:\src\squad-42 -b squad/42-fix-login main`

d. **Set up dependencies:**
   - Link `node_modules` from main repo to avoid reinstalling:
     - Windows: `cmd /c "mklink /J {worktree}\node_modules {main-repo}\node_modules"`
     - Unix: `ln -s {main-repo}/node_modules {worktree}/node_modules`
   - If linking fails (error), fall back: `cd {worktree} && npm install`
   - Verify the worktree is ready: check build tools are accessible

e. **Include worktree context in spawn:**
   - Set `WORKTREE_PATH` to the resolved worktree path
   - Set `WORKTREE_MODE` to `true`
   - Add worktree instructions to the spawn prompt (see template below)

**3. If worktrees disabled:**
- Set `WORKTREE_PATH` to `"n/a"`
- Set `WORKTREE_MODE` to `false`
- Use existing `git checkout -b` flow (no changes to current behavior)

---

## Format References

### Multi-Agent Artifact Format

**On-demand reference:** Read `.squad/templates/multi-agent-format.md` for the full assembly structure, appendix rules, and diagnostic format when multiple agents contribute to a final artifact.

**Core rules (always loaded):**
- Assembled result goes at top, raw agent outputs in appendix below
- Include termination condition, constraint budgets (if active), reviewer verdicts (if any)
- Never edit, summarize, or polish raw agent outputs — paste verbatim only

### Constraint Budget Tracking

**On-demand reference:** Read `.squad/templates/constraint-tracking.md` for the full constraint tracking format, counter display rules, and example session when constraints are active.

**Core rules (always loaded):**
- Format: `📊 Clarifying questions used: 2 / 3`
- Update counter each time consumed; state when exhausted
- If no constraints active, do not display counters

---

## Anti-Patterns

- ❌ Loading this entire skill file on every session — only read when setup or lifecycle operations are triggered
- ❌ Creating team files before user confirms Phase 1 proposal
- ❌ Mixing agents from different universes in the same cast
- ❌ Skipping the `ask_user` tool during Init Mode
- ❌ Using `## Team Roster` instead of `## Members` (breaks GitHub workflows)
- ❌ Reading or storing `git config user.email` (PII violation)
- ❌ Overwriting existing `.squad/` state during init (skip-if-exists pattern)
- ❌ Renaming existing agents during casting migration
