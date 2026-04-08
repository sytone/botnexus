# Skills guide

Skills are modular knowledge packages that enhance agent capabilities without code changes. They follow the open [Agent Skills specification](https://agentskills.io/specification) and contain domain-specific instructions, conventions, and reference material that agents load on demand.

## Table of contents

1. [What are skills?](#what-are-skills)
2. [Quick start](#quick-start)
3. [SKILL.md format](#skillmd-format)
4. [Skill directory structure](#skill-directory-structure)
5. [Skill placement](#skill-placement)
6. [Agent configuration](#agent-configuration)
7. [How skills load](#how-skills-load)
8. [Agent skill tool](#agent-skill-tool)
9. [Best practices](#best-practices)
10. [Complete example](#complete-example)

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

---

## Skill directory structure

A skill is a directory containing a `SKILL.md` and optional supporting files, per the [Agent Skills spec](https://agentskills.io/specification):

```
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

```
references/
├── REFERENCE.md      # Detailed technical reference
├── api-patterns.md   # API-specific guidance
└── error-codes.md    # Error handling lookup
```

### assets/

Static resources like templates, schemas, lookup tables, and example files.

---

## Skill placement

BotNexus discovers skills from three locations, scanned in priority order:

### 1. Global skills

Available to all agents. Stored in `~/.botnexus/skills/`.

```
~/.botnexus/skills/
├── git-workflow/SKILL.md
├── testing-standards/SKILL.md
└── security-checklist/SKILL.md
```

Use global skills for team-wide standards, shared conventions, and reusable best practices.

### 2. Per-agent skills

Available only to a specific agent. Stored in `~/.botnexus/agents/{agent-id}/skills/`.

```
~/.botnexus/agents/code-reviewer/skills/
├── review-criteria/SKILL.md
└── approval-process/SKILL.md
```

Use per-agent skills for role-specific knowledge, custom methodologies, and domain expertise unique to that agent.

### 3. Workspace skills

Scoped to a project workspace. Stored in `{workspace}/skills/`.

```
my-project/skills/
├── project-conventions/SKILL.md
└── deploy-process/SKILL.md
```

Use workspace skills for project-specific conventions that travel with the codebase.

### Priority and merging

When multiple locations define a skill with the same name, higher-priority sources override lower ones:

**Workspace** (highest) → **Per-agent** → **Global** (lowest)

For example, if both `~/.botnexus/skills/security/SKILL.md` and `my-project/skills/security/SKILL.md` exist, the workspace version is used.

---

## Agent configuration

Configure skills per-agent in `~/.botnexus/config.json` under `agents.{agent-id}.skills`:

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

```
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

```
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

```
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

```
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

```
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

```
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
- [Architecture overview](architecture.md) — how skills fit into the BotNexus pipeline
