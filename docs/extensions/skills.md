# Skills Extension

The Skills extension provides the runtime infrastructure for loading, managing, and injecting skill knowledge into agent prompts. It powers the `skills` tool that agents use to discover and activate domain-specific knowledge packages.

## What It Does

- **Discovers skills** from the global skills directory (`~/.botnexus/skills/`) and per-agent workspace skills
- **Injects skill context** into agent system prompts via the prompt pipeline hook system
- **Provides the `skills` tool** with `list` and `load` actions for on-demand skill activation
- **Manages skill lifecycle** — loading, caching, and unloading skill content

## Enabling

The Skills extension is built-in and enabled by default. No explicit configuration is required.

Skills are discovered from:

1. **Global directory**: `~/.botnexus/skills/<skill-name>/SKILL.md`
2. **Agent workspace**: `~/.botnexus/agents/<agent-id>/workspace/skills/<skill-name>/SKILL.md`

## Tools Provided

### `skills`

The primary tool agents use to interact with the skill system.

#### `list` — Discover available skills

Returns all skills available to the current agent, with descriptions.

```json
{
  "action": "list"
}
```

#### `load` — Activate a skill

Loads a skill's content into the current conversation context.

```json
{
  "action": "load",
  "skillName": "my-skill"
}
```

### `skill_manage`

Administrative tool for creating and maintaining skills at runtime.

| Action | Description |
|--------|-------------|
| `create` | Create a new skill with SKILL.md content |
| `edit` | Full rewrite of a skill's SKILL.md |
| `patch` | Targeted find-replace within a skill file |
| `delete` | Remove a skill |
| `write_file` | Write a supporting file (references/, templates/, scripts/, assets/) |
| `remove_file` | Delete a supporting file |

An optional `scope` argument selects where a newly created skill is written: `workspace`
(default), `agent` (this agent only), or `shared` (the global all-agent directory). For
edit/patch/delete/write_file/remove_file the existing skill is matched across all scopes.

#### Managing shared (all-agent) skills

By default `skill_manage` can only write to agent and workspace scopes. Writing to the global
`~/.botnexus/skills/` directory -- visible to every agent -- requires the opt-in gate
`AllowSharedSkillManagement`. Because a shared skill changes behaviour for all agents, treat
this as a wide blast radius: enable it only for trusted operator agents. Deleting a shared
skill (or removing a supporting file from one) additionally requires `AllowSkillDeletion`.
Symlink, path-traversal, size, and security scans apply to shared skills exactly as they do
to agent and workspace skills.

## Prompt Integration

Skills integrate with the prompt pipeline through the `SkillPromptHookHandler`:

1. **Auto-loaded skills** — Skills marked in agent config are injected into every prompt
2. **On-demand skills** — Skills loaded via the `skills` tool are added to the current session context
3. **Skill context section** — Appears as a `<!-- SKILLS_CONTEXT -->` block in the system prompt

## Configuration

### Agent-Level Skill Configuration

Agents can auto-load specific skills via their configuration:

```json
{
  "agents": {
    "my-agent": {
      "skills": ["github", "teams", "calendar"]
    }
  }
}
```

Auto-loaded skills are always available without the agent needing to call `skills load`.

### skill_manage gates

These flags live in the agent extension config under `botnexus-skills`:

| Setting | Default | Effect |
|---------|---------|--------|
| `AllowSkillCreation` | `true` | Enables `skill_manage` (create/edit/patch/write_file). |
| `AllowSkillDeletion` | `true` | Allows `delete` and `remove_file`. |
| `AllowSharedSkillManagement` | `false` | Allows writing to the global all-agent skills dir via `scope: shared`. Wide blast radius -- opt-in. |

## Skill Directory Structure

Each skill follows the [Agent Skills specification](https://agentskills.io/specification):

```text
skills/
└── my-skill/
    ├── SKILL.md           # Required — skill definition with YAML frontmatter
    ├── references/        # Domain knowledge files
    ├── templates/         # Reusable templates
    ├── scripts/           # Executable scripts (tool wrappers)
    └── assets/            # Static assets
```

## See Also

- [Skills Guide](/skills) — comprehensive guide to writing and using skills
- [Extension Development](../extension-development.md) — building custom extensions
- [Prompt Pipeline](../development/prompt-pipeline.md) — how skills integrate with the prompt system
