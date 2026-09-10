# Skills guide

Skills are modular knowledge packages that enhance agent capabilities without code changes. They follow the open [Agent Skills specification](https://agentskills.io/specification) and contain domain-specific instructions, conventions, and reference material that agents load on demand.

## Table of contents

1. [What are skills?](#what-are-skills)
2. [Quick start](#quick-start)
3. [Skills that ship with BotNexus](#skills-that-ship-with-botnexus)
4. [SKILL.md format](#skillmd-format)
5. [Skill directory structure](#skill-directory-structure)
6. [Skill placement](#skill-placement)
7. [Agent configuration](#agent-configuration)
8. [How skills load](#how-skills-load)
9. [Agent skill tool](#agent-skill-tool)
10. [Best practices](#best-practices)
11. [Complete example](#complete-example)

---

## What are skills?

Skills are **instructional markdown files** that teach agents domain knowledge. Unlike tools (which execute actions), skills provide context that shapes how an agent thinks and responds.

| Aspect | Skills | Tools |
|--------|--------|-------|
| **Purpose** | Provide knowledge and context | Execute actions |
| **Format** | Markdown with YAML frontmatter | Executable code or API |
| **Loading** | On demand or auto-loaded into prompt | Called dynamically by agent |
| **Scope** | Reasoning, planning, conventions | Runtime execution |

Good candidates for skills:

- Git workflow conventions and commit practices
- Code review criteria and standards
- Project-specific naming conventions
- Testing strategies and patterns
- Security best practices for your domain
- Documentation writing guidelines

---

## Quick start

Create your first skill in 30 seconds:

```bash
# 1. Create the skill directory
mkdir -p ~/.botnexus/skills/git-workflow

# 2. Write the SKILL.md file
cat > ~/.botnexus/skills/git-workflow/SKILL.md << 'EOF'
---
name: git-workflow
description: "Git conventions: commit format, branch naming, and PR process"
---

# Git workflow

Use conventional commits with imperative mood:

- `feat: add user search endpoint`
- `fix: prevent null reference in parser`
- `docs: update skills guide`

Always run `dotnet test` before committing.
EOF
```

The skill is now discoverable. Agents can list it with the `skills` tool and load it when they need git guidance.

---

## Skills that ship with BotNexus

Two skills live in this repository. Neither is installed automatically — skills live under
`~/.botnexus/`, which is user data, so an operator decides whether an agent gets one. Each has an
install script that copies it into place:

| Skill | What it does | Install |
|---|---|---|
| `botnexus-guide` | Teaches an agent the platform it is running on — agents, conversations vs sessions, channels, tools, cron, extensions and configuration. Its reference files are copies of `docs/user-guide`. | `scripts/install-guide-skill.sh` |
| `get-to-know-you` | Interviews the person the agent works for and records what it learns to agent memory, so the agent stops asking the same questions. | `scripts/install-interview-skill.sh` |

Both take `--home <dir>` to install into a non-default BotNexus home. Only
`install-interview-skill.sh` also takes `--agent <agent-id>`:

```bash
# Global - every agent can load it
scripts/install-interview-skill.sh

# Scoped to one agent instead
scripts/install-interview-skill.sh --agent my-assistant

# Install into a non-default home
scripts/install-guide-skill.sh --home /srv/botnexus
```

**Prefer `--agent` for `get-to-know-you`.** Installed globally it is loadable by every agent,
including unattended cron workers that have nobody to interview.

**`botnexus-guide` is a copy, not a link.** Its reference files are snapshots of the user guide
taken at install time, so re-run `scripts/install-guide-skill.sh` after upgrading or the agent will
answer from the previous version's documentation.

---

## SKILL.md format

Every skill requires a `SKILL.md` file containing YAML frontmatter and a markdown body.

### Frontmatter fields

| Field | Required | Constraints | Description |
|-------|----------|-------------|-------------|
| `name` | Yes | Max 64 chars. Lowercase `a-z`, digits, hyphens. No leading/trailing/consecutive hyphens. Must match directory name. | Unique skill identifier. |
| `description` | Yes | 1–1024 chars, non-empty. | What the skill does and when to use it. Include keywords that help agents find it. |
| `license` | No | — | License name or reference to a bundled license file. |
| `compatibility` | No | 1–500 chars if provided. | Environment requirements (intended product, system packages, network access). |
| `metadata` | No | String key → string value map. | Arbitrary key-value data for client-specific extensions. |
| `parameters` | No | String key → string value map. Keys are slot names, matched case-insensitively. | Values that vary between runs. Each key is substituted into the body wherever `{{key}}` appears, and the value is the description a caller reads. Every declared parameter is required at load time. |
| `allowed-tools` | No | Space-delimited tool names. | Pre-approved tools the skill may use. Experimental — support varies by agent. |
| `disable-model-invocation` | No | Boolean. Default `false`. | BotNexus extension. When `true`, the skill is excluded from model context (used for agent-internal skills). |

### Example frontmatter

```yaml
---
name: code-review
description: "Code review checklist and approval criteria for pull requests"
license: MIT
compatibility: "Requires access to GitHub API"
metadata:
  team: platform
  priority: high
allowed-tools: gh git
---
```

### Body content

The markdown body after frontmatter contains the skill instructions. Write whatever helps agents perform the task. Recommended content:

- Step-by-step instructions
- Concrete examples of inputs and outputs
- Common edge cases and how to handle them

The agent loads the entire body when it activates a skill. For large skills, move detailed content into [reference files](#skill-directory-structure).

### Parameterised skills

A skill that is the same procedure each time but a different *value* each time can declare those
values instead of hard-coding them:

```markdown
---
name: add-film
description: Adds a film to Radarr and reports when it lands.
parameters:
  title: "The film to add; different every run."
---
1. POST {{title}} to Radarr at http://nas:7878.
2. Poll the queue until it clears.
```

The caller supplies values when it loads the skill, and `{{title}}` is replaced before the
instructions reach the model:

```json
{ "action": "load", "skillName": "add-film", "parameters": { "title": "Dune" } }
```

**Every declared parameter is required.** A load that omits one is refused rather than substituted
blank — instructions that quietly lose a value produce a run that looks like it worked and is wrong.
The `list` action names each skill's parameters, so callers do not discover them by being refused.

Note what stayed literal in the example: the Radarr host is part of the skill, and the film is not.
That distinction is a judgement about intent, not something derivable from the text — which is why
the `skill_record` tool asks an operator to confirm it rather than inferring it. See
[the Skills extension reference](extensions/skills.md#skill_record).

---

## Skill directory structure

A skill is a directory containing a `SKILL.md` and optional supporting files, per the [Agent Skills spec](https://agentskills.io/specification):

```text
my-skill/
├── SKILL.md          # Required — metadata + instructions
├── scripts/          # Optional — executable code agents can run
├── references/       # Optional — detailed docs loaded on demand
└── assets/           # Optional — templates, schemas, data files
```

### scripts/

Executable code that agents can run. Scripts should be self-contained, include helpful error messages, and handle edge cases. Supported languages depend on the agent implementation.

### references/

Additional documentation loaded when needed. Keep individual files focused — agents load these on demand, so smaller files mean less context usage.

```text
references/
├── REFERENCE.md      # Detailed technical reference
├── api-patterns.md   # API-specific guidance
└── error-codes.md    # Error handling lookup
```

### assets/

Static resources like templates, schemas, lookup tables, and example files.

---

## Skill placement

BotNexus discovers skills from four sources, scanned in priority order:

### 0. Plugin skills

Shipped by an installed plugin, under that plugin's own `skills/` directory (#2684). You do not
place these by hand — installing the plugin puts them there and removing it takes them away, and a
machine with no plugins installed simply has none.

They are scanned **first**, which is what gives them the *lowest* priority: the resolver fills a
dictionary keyed by skill name and a later source overwrites an earlier one. So a plugin shares the
global tier but loses a name collision with anything you wrote yourself.

### 1. Global skills

Available to all agents. Stored in `~/.botnexus/skills/`.

```text
~/.botnexus/skills/
├── git-workflow/SKILL.md
├── testing-standards/SKILL.md
└── security-checklist/SKILL.md
```

Use global skills for team-wide standards, shared conventions, and reusable best practices.

> Global skills are visible to every agent, so editing one has a wide blast radius. The `skill_manage` tool can only write here when `AllowSharedSkillManagement` is enabled (default false) and the request uses `scope: shared`; deletion also needs `AllowSkillDeletion`.

### 2. Per-agent skills

Available only to a specific agent. Stored in `~/.botnexus/agents/{agent-id}/skills/`.

```text
~/.botnexus/agents/code-reviewer/skills/
├── review-criteria/SKILL.md
└── approval-process/SKILL.md
```

Use per-agent skills for role-specific knowledge, custom methodologies, and domain expertise unique to that agent.

### 3. Workspace skills

Scoped to a project workspace. Stored in `{workspace}/skills/`.

```text
my-project/skills/
├── project-conventions/SKILL.md
└── deploy-process/SKILL.md
```

Use workspace skills for project-specific conventions that travel with the codebase.

### Priority and merging

When multiple locations define a skill with the same name, higher-priority sources override lower ones:

**Workspace** (highest) → **Per-agent** → **Global** → **Plugin** (lowest)

For example, if both `~/.botnexus/skills/security/SKILL.md` and `my-project/skills/security/SKILL.md` exist, the workspace version is used.

Plugin skills sit at the bottom deliberately: a plugin is code you installed rather than content you
wrote, so a skill you author at any level wins over one it ships.

---

## Agent configuration

Configure skills per-agent in `~/.botnexus/config.json` under `agents.{agent-id}.skills`. As
everywhere else, `config.json` is a flat top-level document with `camelCase` keys and **no**
`BotNexus` wrapper — see
[Canonical document shape and location](configuration.md#canonical-shape):

```json
{
  "agents": {
    "code-reviewer": {
      "provider": "copilot",
      "model": "gpt-4.1",
      "skills": {
        "enabled": true,
        "autoLoad": ["git-workflow", "review-criteria"],
        "disabled": ["experimental-skill"],
        "allowed": ["git-workflow", "review-criteria", "testing-standards"],
        "maxLoadedSkills": 20,
        "maxSkillContentChars": 100000
      }
    }
  }
}
```

### Configuration fields

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `enabled` | boolean | `true` | Master switch. When `false`, the skills system is completely disabled for this agent. |
| `autoLoad` | string[] | `null` | Skill names to load automatically at session start. These are injected into the prompt without the agent requesting them. |
| `disabled` | string[] | `null` | Skill names explicitly denied. These are never loaded, regardless of other settings. Uses exact name matching — no wildcards. |
| `allowed` | string[] | `null` | Allowlist of skill names. When set, only these skills can load. When `null`, all discovered skills are allowed. |
| `maxLoadedSkills` | int | `20` | Maximum number of skills that can be loaded simultaneously into the prompt. |
| `maxSkillContentChars` | int | `100000` | Maximum total characters of skill content in the prompt. Prevents context window exhaustion. |

### How allow and deny interact

The resolver applies filters in this order:

1. If `disabled` contains the skill name → **denied** (always wins)
2. If `allowed` is set and does not contain the skill name → **denied**
3. Otherwise → **eligible** for loading

A skill is only loaded if it is in `autoLoad` or explicitly loaded by the agent at runtime. Eligible skills that aren't auto-loaded appear in the "available" list.

> **Note:** The `disabled` and `allowed` lists use **exact name matching** only. Wildcard patterns are not supported.

---

## How skills load

The full pipeline from disk to agent prompt:

```text
┌─────────────┐    ┌──────────────┐    ┌──────────────┐    ┌──────────────────┐
│  Discovery   │ →  │  Validation   │ →  │  Resolver     │ →  │ Prompt injection  │
│ 3-path scan  │    │ name, desc,   │    │ allow/deny,   │    │ active + available │
│ + merge      │    │ format checks │    │ autoLoad,     │    │ sections           │
│              │    │              │    │ budget limits  │    │                    │
└─────────────┘    └──────────────┘    └──────────────┘    └──────────────────┘
```

### 1. Discovery

`SkillDiscovery` scans three directories for subdirectories containing `SKILL.md`:

- `~/.botnexus/skills/` (global)
- `~/.botnexus/agents/{agent-id}/skills/` (per-agent)
- `{workspace}/skills/` (workspace)

Same-named skills from higher-priority sources override lower ones.

### 2. Validation

Each skill is validated before inclusion:

- **Name** must match the directory name exactly, be 1–64 lowercase alphanumeric characters with hyphens, and contain no consecutive hyphens (`--`).
- **Description** must be non-empty and at most 1024 characters.
- **Compatibility**, if provided, must be at most 500 characters.
- Skills with `disable-model-invocation: true` are excluded from model context.

Skills that fail validation are silently skipped.

### 3. Resolver

`SkillResolver` applies the agent's `SkillsConfig` to determine which skills to load:

- Skills in `disabled` are denied.
- If `allowed` is set, skills not in the list are denied.
- Among eligible skills, those in `autoLoad` or explicitly loaded by the agent are activated.
- Loading stops when `maxLoadedSkills` or `maxSkillContentChars` is reached.

The resolver produces three lists: **loaded**, **available** (eligible but not loaded), and **denied**.

### 4. Prompt injection

`SkillPromptBuilder` generates a prompt section wrapped in sentinel markers:

```text
<!-- SKILLS_CONTEXT -->
## Active Skills
The following skills are loaded and active:
- **git-workflow**: Git conventions: commit format, branch naming, and PR process

## Skill: git-workflow

[full skill content here]

## Skills Available (not loaded)
Use the `skills` tool with action `load` to activate a skill when needed.
- **testing-standards**: Unit and integration testing patterns
<!-- END_SKILLS_CONTEXT -->
```

Skill content is sanitized to strip sentinel markers, preventing prompt injection.

---

## Agent skill tool

Agents interact with skills through the `skills` tool, which supports two actions.

### List available skills

The agent calls the `skills` tool with `action: "list"` to see what's available:

```text
Agent: I need to check what skills are available.

→ Tool call: skills { "action": "list" }

← Tool response:
## Loaded Skills
- **git-workflow**: Git conventions: commit format, branch naming, and PR process

## Available Skills (not loaded)
Use `skills` tool with action `load` and the skill name to activate.
- **testing-standards**: Unit and integration testing patterns
- **security-checklist**: Security review checklist for PRs
```

### Load a skill

The agent calls the `skills` tool with `action: "load"` and `skillName` to activate a skill:

```text
Agent: I need testing guidance for this PR.

→ Tool call: skills { "action": "load", "skillName": "testing-standards" }

← Tool response:
## Skill: testing-standards

# Testing standards

Write unit tests for all public methods...
[full skill content]
```

Once loaded, the skill content is available in the agent's context for the rest of the session.

### Error cases

- **Skill not found:** `Skill 'unknown-skill' not found. Use action 'list' to see available skills.`
- **Already loaded:** `Skill 'git-workflow' is already loaded.`
- **Denied by config:** `Skill 'experimental' is not available for this agent.`
- **Budget exceeded:** `Skill 'large-reference' cannot be loaded (budget exceeded).`

---

## Best practices

### Keep skills focused

One skill = one domain. Avoid bloated multi-purpose skills.

✅ Good: `git-workflow`, `testing-strategy`, `security-checklist`
❌ Bad: `general-knowledge`, `everything-you-need`

### Write for agent clarity

Agents are LLMs that benefit from explicit, structured content:

- Use clear step-by-step instructions
- Organize with descriptive headings
- Include concrete, copy-pasteable examples
- Prefer lists over dense paragraphs

### Include trigger keywords in descriptions

The `description` field helps agents decide when to load a skill. Include keywords that match likely tasks:

```yaml
description: "Git conventions: commit format, branch naming, PR process, merge strategy"
```

### Keep SKILL.md under 500 lines

Per the Agent Skills spec, keep the main `SKILL.md` concise. Move detailed reference material to `references/` files that agents load on demand:

```markdown
See [the API reference](references/api-patterns.md) for detailed endpoint documentation.
```

### Use references/ for detailed content

Split large skills into a concise SKILL.md (overview + key instructions) and reference files (detailed lookups, tables, examples):

```text
my-skill/
├── SKILL.md                    # ~200 lines: overview + core instructions
└── references/
    ├── error-codes.md          # Loaded only when agent needs error details
    └── migration-guide.md      # Loaded only during migration tasks
```

### Use descriptive directory names

Skill directory names must match the `name` field and should clearly indicate purpose:

✅ Good: `code-review-criteria`, `git-workflow`, `security-checklist`
❌ Bad: `skill1`, `my-knowledge`, `temp`

---

## Complete example

A production-ready skill with frontmatter, content, and a references directory.

### Directory layout

```text
~/.botnexus/agents/code-reviewer/skills/review-standards/
├── SKILL.md
└── references/
    └── checklist-details.md
```

### SKILL.md

```markdown
---
name: review-standards
description: "Code review checklist, approval criteria, and common issues for pull requests"
license: MIT
metadata:
  team: platform
---

# Review standards

Apply this checklist when reviewing pull requests.

## Quick checklist

1. **Correctness** — Does the code do what the PR description says?
2. **Tests** — Are new paths covered by unit tests?
3. **Style** — Does it follow project conventions?
4. **Security** — No hardcoded secrets, proper input validation?
5. **Performance** — No obvious regressions (N+1 queries, unbounded loops)?
6. **Docs** — API changes documented? README updated?

## Approval criteria

Approve when all checklist items are addressed (or marked N/A), tests pass, and no blocking issues remain.

## Common issues to flag

- Missing error handling for external calls
- Inconsistent naming or style
- Breaking API changes without migration path
- Insufficient test coverage for edge cases

See [detailed checklist](references/checklist-details.md) for expanded criteria.
```

### references/checklist-details.md

```markdown
# Detailed review checklist

## Functionality
- [ ] Code implements the described feature or fix
- [ ] Logic handles edge cases (nulls, empty collections, boundary values)
- [ ] Changes don't break existing behavior

## Testing
- [ ] Unit tests for new public methods
- [ ] Test names describe what they verify
- [ ] Edge cases have dedicated tests
- [ ] All tests pass locally with `dotnet test`

## Security
- [ ] No hardcoded secrets or credentials
- [ ] Input validation on all external data
- [ ] No SQL injection or path traversal vulnerabilities
- [ ] Secure defaults used throughout
```

---

## Further reading

- [Agent Skills specification](https://agentskills.io/specification) — the open standard for SKILL.md format
- [Configuration guide](configuration.md) — full `config.json` reference including agent settings
- [Architecture overview](architecture/overview.md) — how skills fit into the BotNexus pipeline
