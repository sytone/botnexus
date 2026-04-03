# Skills Guide

Skills are modular knowledge packages that enhance agent reasoning without requiring code changes. They contain domain-specific instructions, conventions, best practices, and contextual information that agents can reference while operating.

## Table of Contents

1. [What Are Skills?](#what-are-skills)
2. [How Skills Work](#how-skills-work)
3. [Creating a Skill](#creating-a-skill)
4. [SKILL.md Format](#skillmd-format)
5. [Skill Placement](#skill-placement)
6. [Skill Resolution Order](#skill-resolution-order)
7. [Disabling Skills](#disabling-skills)
8. [Using the Skills API](#using-the-skills-api)
9. [Best Practices](#best-practices)
10. [Example: Code Review Skill](#example-code-review-skill)

---

## What Are Skills?

Skills are **modular knowledge packages** designed to improve agent decision-making. Unlike tools (which execute actions), skills provide instructional content, patterns, conventions, and best practices that agents read and understand.

**Skills vs Tools:**

| Aspect | Skills | Tools |
|--------|--------|-------|
| **Purpose** | Provide knowledge and context | Execute actions |
| **Format** | Markdown with YAML frontmatter | Executable code |
| **Usage** | Injected into agent system prompt | Called dynamically by agent |
| **Scope** | Reasoning and planning | Runtime execution |

**Examples of good skills:**
- Git workflow conventions and commit practices
- Code review criteria and standards
- Documentation writing templates
- Testing strategies and patterns
- Security best practices for your domain
- Project-specific naming conventions
- Performance optimization guidelines

---

## How Skills Work

1. **Discovery**: BotNexus scans global and per-agent skill directories for `SKILL.md` files
2. **Parsing**: Each `SKILL.md` is parsed for YAML frontmatter (metadata) and markdown body (content)
3. **Merging**: Global and per-agent skills are merged (agent skills override global ones with the same name)
4. **Filtering**: Skills matching `DisabledSkills` patterns are excluded
5. **Injection**: Remaining skills are injected into the agent's system prompt in a dedicated `## SKILLS.md` section

When an agent operates, all loaded skills become visible in its context window, allowing it to reference and apply the knowledge directly.

---

## Creating a Skill

### Directory Structure

Skills live in two locations:

**Global skills** (available to all agents):
```
~/.botnexus/skills/{skill-name}/SKILL.md
```

**Per-agent skills** (available only to specific agent):
```
~/.botnexus/agents/{agent-name}/skills/{skill-name}/SKILL.md
```

### Basic Skill Creation

```bash
# Create a global skill
mkdir -p ~/.botnexus/skills/my-skill

# Create the skill file
cat > ~/.botnexus/skills/my-skill/SKILL.md << 'EOF'
---
description: "Brief description of what this skill teaches"
version: "1.0.0"
---

# Skill Name

Your markdown content here. This is what the agent reads.

## Section 1

Instructions, patterns, conventions...

## Section 2

More context...
EOF
```

```bash
# Create an agent-specific skill
mkdir -p ~/.botnexus/agents/my-agent/skills/my-skill

cat > ~/.botnexus/agents/my-agent/skills/my-skill/SKILL.md << 'EOF'
---
description: "Agent-specific knowledge"
---

# Agent-Specific Skill

Content specific to this agent...
EOF
```

---

## SKILL.md Format

Each skill is a single markdown file with optional YAML frontmatter.

### Complete Example

```markdown
---
name: "git-workflow"
description: "Git workflow and commit conventions for BotNexus"
version: "1.0.0"
always: false
---

# Git Workflow

This skill documents the git conventions used in the BotNexus project.

## Commit Types

Use conventional commits:

- **feat**: New feature
  - Example: `feat: add skill filtering by wildcard`
  
- **fix**: Bug fix
  - Example: `fix: resolve memory leak in SkillsLoader`

- **docs**: Documentation changes
  - Example: `docs: update skills guide`

- **test**: Test additions or modifications
  - Example: `test: add SkillsLoader unit tests`

- **refactor**: Code refactoring
  - Example: `refactor: simplify skill merging logic`

## Before Committing

1. Run `dotnet build` — ensure clean compilation
2. Run `dotnet test` — all tests must pass
3. Run `dotnet format` — apply code style
4. Verify changes with `git diff`

## Commit Message Format

```
<type>: <subject>

<body (optional)>

<footer (optional)>
```

**Subject line rules:**
- Use imperative mood ("add" not "added")
- Don't capitalize
- No period at the end
- Max 50 characters
```

### Frontmatter Fields

| Field | Type | Required | Default | Purpose |
|-------|------|----------|---------|---------|
| `description` | string | No | Auto-generated | Human-readable skill description shown in UIs and API responses |
| `version` | string | No | (empty) | Semantic version for tracking skill updates |
| `always` | boolean | No | false | (Reserved) Future flag for unconditional skill injection |

**Notes:**
- YAML frontmatter is optional — skills work without it
- If no frontmatter, skill description defaults to `"Skill: {skill-name}"`
- Markdown body can be any valid markdown — tables, code blocks, lists, links all supported

### Markdown Content Guidelines

Write skill content for **LLM clarity**:

✅ **DO:**
- Use clear, step-by-step instructions
- Include concrete examples
- Organize with descriptive headings
- Use lists for alternatives or options
- Add code snippets and templates
- Cross-reference related skills or tools

❌ **DON'T:**
- Use overly complex formatting
- Assume prior knowledge without context
- Write extremely long paragraphs
- Embed binary files or images
- Use unclear jargon without explanation

---

## Skill Placement

### Decision: Global vs Per-Agent Skills

**Use global skills** for:
- Standards and conventions shared across the team
- Reusable patterns and best practices
- General knowledge applicable to multiple agents
- Foundation knowledge everyone needs

**Use per-agent skills** for:
- Agent-specific responsibilities or roles
- Domain expertise unique to that agent
- Custom methodologies for specialized tasks
- Temporary overrides of global practices

### Example Placement

```
~/.botnexus/skills/
├── git-conventions/SKILL.md        ← Global: all agents follow this
├── testing-standards/SKILL.md      ← Global: testing patterns
└── security-checklist/SKILL.md     ← Global: security best practices

~/.botnexus/agents/
├── code-reviewer/skills/
│   └── code-review-criteria/SKILL.md     ← Agent-specific: CR methodology
├── data-analyst/skills/
│   └── data-pipeline-patterns/SKILL.md   ← Agent-specific: analysis patterns
└── devops-bot/skills/
    ├── deployment-procedures/SKILL.md    ← Agent-specific: deployment steps
    └── incident-response/SKILL.md        ← Agent-specific: incident handling
```

---

## Skill Resolution Order

When loading skills for an agent, BotNexus follows this order:

1. **Load global skills** from `~/.botnexus/skills/`
2. **Load per-agent skills** from `~/.botnexus/agents/{agent-name}/skills/`
3. **Merge**: Per-agent skills with the same name **override** global skills
4. **Filter**: Apply `DisabledSkills` patterns to remove excluded skills
5. **Sort**: Alphabetically by skill name for consistent ordering
6. **Inject**: Into agent system prompt in `## SKILLS.md` section

### Example Resolution

**Config:**
```json
{
  "BotNexus": {
    "Agents": {
      "Named": {
        "my-agent": {
          "DisabledSkills": ["debug-*", "experimental"]
        }
      }
    }
  }
}
```

**Directories:**
```
~/.botnexus/skills/
├── security/SKILL.md           ← Loaded
├── testing/SKILL.md            ← Loaded
├── debug-tools/SKILL.md        ← Disabled (matches "debug-*")
└── experimental/SKILL.md       ← Disabled (exact match)

~/.botnexus/agents/my-agent/skills/
├── security/SKILL.md           ← Overrides global security
└── custom-analysis/SKILL.md    ← Loaded
```

**Final loaded skills:**
1. custom-analysis (per-agent)
2. security (per-agent version, overriding global)
3. testing (global)

---

## Disabling Skills

Use the `DisabledSkills` configuration to prevent specific skills from being loaded.

### Configuration

Add `DisabledSkills` to agent configuration:

```json
{
  "BotNexus": {
    "Agents": {
      "Named": {
        "my-agent": {
          "DisabledSkills": ["debug-*", "experimental-*", "test-skill"]
        }
      }
    }
  }
}
```

Or via environment variable:

```bash
export BotNexus__Agents__Named__my-agent__DisabledSkills__0=debug-*
export BotNexus__Agents__Named__my-agent__DisabledSkills__1=experimental-*
```

### Pattern Matching

Patterns support wildcards:

| Pattern | Matches | Example |
|---------|---------|---------|
| `exact-name` | Exact skill name | `code-review` matches skill `code-review` |
| `prefix-*` | Any skill starting with prefix | `debug-*` matches `debug-tools`, `debug-logging` |
| `*-suffix` | Any skill ending with suffix | `*-experimental` matches `feature-experimental`, `debug-experimental` |
| `test-?` | Single character wildcard | `test-?` matches `test-a`, `test-1` but not `test-ab` |

### Common Patterns

```json
{
  "DisabledSkills": [
    "debug-*",           // Disable all debugging skills
    "experimental-*",    // Disable experimental features
    "*-internal",        // Disable internal/restricted skills
    "test-*",           // Disable test-only skills
    "legacy-*"          // Disable deprecated skills
  ]
}
```

### Permanent Disable

To disable a skill at startup without editing config each time:

```bash
# Set environment variable before running Gateway
export BotNexus__Agents__Named__analyzer__DisabledSkills__0=legacy-patterns
dotnet run --project src/BotNexus.Gateway
```

---

## Using the Skills API

### List Global Skills

**Endpoint:** `GET /api/skills`

**Request:**
```bash
curl -H "X-Api-Key: your-api-key" http://localhost:18790/api/skills
```

**Response (200 OK):**
```json
[
  {
    "name": "git-workflow",
    "description": "Git workflow and commit conventions for BotNexus",
    "version": "1.0.0",
    "scope": "Global",
    "alwaysLoad": false,
    "sourcePath": "/home/user/.botnexus/skills/git-workflow/SKILL.md"
  },
  {
    "name": "testing-standards",
    "description": "Testing patterns and best practices",
    "version": "1.0.0",
    "scope": "Global",
    "alwaysLoad": false,
    "sourcePath": "/home/user/.botnexus/skills/testing-standards/SKILL.md"
  }
]
```

### List Agent Skills

**Endpoint:** `GET /api/agents/{name}/skills`

**Request:**
```bash
curl -H "X-Api-Key: your-api-key" http://localhost:18790/api/agents/code-reviewer/skills
```

**Response (200 OK):**
```json
[
  {
    "name": "code-review-criteria",
    "description": "Code review standards for this project",
    "version": "1.0.0",
    "scope": "Agent",
    "alwaysLoad": false,
    "sourcePath": "/home/user/.botnexus/agents/code-reviewer/skills/code-review-criteria/SKILL.md",
    "contentPreview": "# Code Review Criteria\n\nReviewers should check:\n1. Functionality\n2. Code style\n3. Tests\n..."
  },
  {
    "name": "git-workflow",
    "description": "Git workflow and commit conventions for BotNexus",
    "version": "1.0.0",
    "scope": "Global",
    "alwaysLoad": false,
    "sourcePath": "/home/user/.botnexus/skills/git-workflow/SKILL.md"
  }
]
```

---

## Best Practices

### 1. Keep Skills Focused

One skill = one domain/responsibility. Avoid bloated multi-purpose skills.

✅ Good:
- `git-workflow` — Git conventions only
- `testing-strategy` — Testing patterns only
- `security-checklist` — Security practices only

❌ Bad:
- `general-knowledge` — Mix of unrelated topics
- `everything-you-need` — Overwhelming and unfocused

### 2. Write for Clarity

Agents are LLMs that benefit from explicit, structured content.

✅ Good:
```markdown
## Code Style

When writing Python:
1. Use PEP 8 formatting
2. Limit lines to 88 characters (via black)
3. Use f-strings for formatting
4. Use type hints on public functions
```

❌ Bad:
```markdown
## Code Style

Follow standard conventions.
```

### 3. Include Examples

Concrete examples help agents understand and apply patterns.

✅ Good:
```markdown
## Commit Message Format

Use conventional commits:

feat: add skill filtering
fix: resolve memory leak
docs: update README

Always run tests before committing.
```

❌ Bad:
```markdown
## Commit Message Format

Follow conventions.
```

### 4. Version Your Skills

Use semantic versioning when you update skills significantly.

```markdown
---
version: "1.0.0"  // Major.Minor.Patch
---
```

### 5. Use Descriptive Names

Skill folder names should reflect their purpose.

✅ Good: `code-review-criteria`, `git-workflow`, `security-checklist`  
❌ Bad: `skill1`, `my-knowledge`, `temp`

### 6. Document Dependencies

If a skill assumes knowledge from another skill, mention it.

```markdown
---
description: "Advanced code review techniques (requires: code-review-criteria)"
---
```

### 7. Keep Performance in Mind

Large skills consume context window space. Balance comprehensiveness with conciseness.

- Global skills: 1-3 KB typical
- Per-agent skills: Up to 5 KB if specialized
- Truncated in prompts if they exceed `MaxContextFileChars` (default: 8000 chars)

---

## Example: Code Review Skill

Here's a complete, production-ready example of a code review skill:

### Step 1: Create Directory

```bash
mkdir -p ~/.botnexus/agents/code-reviewer/skills/code-review-standards
```

### Step 2: Create SKILL.md

```markdown
---
name: "code-review-standards"
description: "Code review checklist and standards for BotNexus project"
version: "1.0.0"
---

# Code Review Standards

This skill provides the review criteria used for all BotNexus pull requests.

## Review Checklist

### 1. Functionality
- [ ] Code implements the described feature or fix
- [ ] Logic is correct and handles edge cases
- [ ] No obvious bugs or runtime errors
- [ ] Changes don't break existing functionality

### 2. Code Quality
- [ ] Code follows project conventions (see git-workflow skill)
- [ ] Variable and function names are clear and descriptive
- [ ] Complex logic is commented
- [ ] No unnecessary duplication or dead code

### 3. Testing
- [ ] New features include unit tests
- [ ] Tests are comprehensive and focused
- [ ] Test names describe what they test
- [ ] All tests pass locally

### 4. Performance
- [ ] No obvious performance regressions
- [ ] Loops and algorithms are efficient
- [ ] No memory leaks or resource leaks
- [ ] Database queries are optimized

### 5. Security
- [ ] No hardcoded secrets or credentials
- [ ] Input validation present where needed
- [ ] No SQL injection or similar vulnerabilities
- [ ] Secure defaults used

### 6. Documentation
- [ ] PR description explains the change
- [ ] Code comments are clear and helpful
- [ ] API docs updated if applicable
- [ ] README updated if user-facing

## Review Approach

1. **Understand**: Read PR description and understand the intent
2. **Scan**: Quickly scan file structure and changes
3. **Deep Dive**: Review each file line-by-line using the checklist
4. **Test**: Suggest running tests locally if unsure
5. **Feedback**: Provide constructive, actionable feedback
6. **Approve**: Only approve when confident in quality

## Common Issues to Flag

- Missing error handling
- Inconsistent naming or style
- Insufficient test coverage
- Performance concerns (N+1 queries, loops over collections)
- Security vulnerabilities
- Breaking API changes without migration path

## Approval Criteria

Approve when:
- All checklist items addressed (or N/A)
- No blocking issues remain
- Code quality meets standards
- Tests are passing
- Documentation is complete
```

### Step 3: Verify

```bash
# Check that the file exists and is readable
ls -lh ~/.botnexus/agents/code-reviewer/skills/code-review-standards/SKILL.md

# Query the API to confirm it's loaded
curl -H "X-Api-Key: your-api-key" http://localhost:18790/api/agents/code-reviewer/skills | jq '.[] | select(.name=="code-review-standards")'
```

### Step 4: Use

Once created, the skill is automatically loaded into the `code-reviewer` agent's system prompt. When reviewing code, the agent will reference this skill's checklist and approach.

---

## Summary

Skills enable agents to operate with domain-specific knowledge without code changes. They're simple to create (just markdown + YAML), flexible to manage (global or per-agent), and powerful when well-written (explicit, example-rich, and focused).

**Next steps:**
1. Identify key knowledge areas your team wants to codify
2. Create skills for those domains
3. Place them in global or per-agent directories
4. Test with agents to ensure clarity and usefulness
5. Iterate and improve based on usage patterns
