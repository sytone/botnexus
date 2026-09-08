# Skills Extension

The Skills extension provides the runtime infrastructure for loading, managing, and injecting skill knowledge into agent prompts. It powers the `skills` tool that agents use to discover and activate domain-specific knowledge packages.

## What It Does

- **Discovers skills** from the global skills directory (`~/.botnexus/skills/`) and per-agent workspace skills
- **Injects skill context** into agent system prompts via the prompt pipeline hook system
- **Provides the `skills` tool** with `list`, `load`, and `view_file` actions for on-demand skill activation
- **Exposes explicit alias tools** (`skills_list`, `skill_view`) that map to the multi-action `skills` tool for better model ergonomics
- **Tracks skill usage telemetry** (view/use/patch counts) in SQLite, readable via the skills API (see [Extension Telemetry](telemetry.md) for the sanctioned seam extensions use)
- **Manages skill lifecycle** — loading, caching, and unloading skill content

## Enabling

The Skills extension is built-in and enabled by default. No explicit configuration is required.

Skills are discovered from, in increasing order of precedence:

1. **Installed plugins**: `~/.botnexus/plugins/<plugin-name>/skills/<skill-name>/SKILL.md`
2. **Global directory**: `~/.botnexus/skills/<skill-name>/SKILL.md`
3. **Agent directory**: `~/.botnexus/agents/<agent-id>/skills/<skill-name>/SKILL.md`
4. **Agent workspace**: `~/.botnexus/agents/<agent-id>/workspace/skills/<skill-name>/SKILL.md`

When the same skill name appears at more than one level, the higher-numbered one wins.

### Knowing which root a skill came from

Because there is more than one root, a skill directory cannot be guessed from the skill name.
The `skills` tool `load` output therefore states both the resolved directory and the tier it
came from:

```
## Skill: botnexus-maintenance
**Path:** ~/.botnexus/agents/farnsworth/workspace/skills/botnexus-maintenance
**Resolved from:** Workspace skill root
```

Always build script and support-file paths from that reported directory. Hard-coding the shared
`~/.botnexus/skills/<skill-name>/` root works only for skills that happen to live there, and
fails for an agent-local skill with a "not recognized as the name of a script file" error that
names the wrong problem.

### Plugin skills

A plugin bundles skills alongside its other components and ships them as one unit. Its skills
join discovery at the **global/shared tier**, immediately *below* the global directory, so:

- a plugin skill is available to every agent, like a global skill;
- a global, agent, or workspace skill of the same name **overrides** it.

That ordering is deliberate. A plugin can add capability but can never silently displace a
skill the operator wrote themselves — installing a plugin should never change the meaning of
an existing name.

Only plugins recorded in `installed-plugins.json` contribute skills. A directory dropped into
the plugin root by hand was never installed, has no removal manifest and no known provenance,
so it is ignored rather than surfaced into agent context.

Plugin skills go through exactly the same validation, security scan and trust verification as
every other skill. Under `TrustMode: Enforce` a plugin skill whose `trust.json` catalog does
not match its content on disk is skipped and the refusal logged; under `Warn` it is loaded and
the violation logged. See [plugin architecture](../architecture/plugins.md).

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

#### `view_file` — Load a single linked support file

Loads one linked support file (under `references/`, `templates/`, `scripts/`, or `assets/`) from a skill **without** injecting the whole skill into context. Use this for progressive disclosure when only a specific reference is needed.

```json
{
  "action": "view_file",
  "skillName": "my-skill",
  "filePath": "references/api-notes.md"
}
```

### Explicit alias tools

For better model ergonomics, two thin alias tools inject a fixed `action` and delegate to the same `skills` implementation (sharing its per-session loaded-skill state). Callers never pass an `action` argument to an alias.

| Tool | Equivalent to | Purpose |
|------|---------------|---------|
| `skills_list` | `skills` action `list` | List available skills and their descriptions. |
| `skill_view` | `skills` action `view_file` | View a single linked support file from a skill without loading the whole skill. |

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

Key names bind **case-insensitively**, so `allowSharedSkillManagement` and `AllowSharedSkillManagement`
are equivalent — write whichever matches the rest of your config file's style. Before #3495 the
extension bound case-sensitively, so a camelCase key silently bound to nothing and the property kept
its default; if you are upgrading from a build that predates that fix, a shared-scope gate you
thought was open may only now start working.

## Security scanning and scoped acknowledgements

Every skill directory is scanned at discovery time by `SkillSecurityScanner`. A skill with **any
critical finding is skipped** — it never reaches any agent. That is the right default, but many
legitimate skills exist precisely to shell out (`child_process`) or to read `process.env` in order
to authenticate an HTTP call, and both of those are critical rules (`dangerous-exec`,
`env-harvesting`).

### The skip warning names the findings

The discovery warning identifies each outstanding finding by **relative path, line and ruleId**, so
the log line alone is actionable:

```text
[WRN] Skill at '<skills-root>/teamnexus' skipped: security scan found
      unacknowledged critical finding(s): scripts/node/connect-board.mjs:12 (dangerous-exec);
      scripts/node/msconnect.mjs:41 (env-harvesting).
      Record a scoped acknowledgement (skill + ruleId + file) to load it anyway.
```

### Acknowledging a reviewed finding

An operator who has read the flagged code and accepts it records an acknowledgement in the agent's
`botnexus-skills` extension config. Each entry clears **exactly one** finding:

```json
{
  "botnexus-skills": {
    "securityAcknowledgements": [
      {
        "skill": "teamnexus",
        "ruleId": "dangerous-exec",
        "file": "scripts/node/connect-board.mjs",
        "reason": "Skill exists to drive the board CLI; reviewed 2026-08-18.",
        "sha256": "9f2b..."
      }
    ]
  }
}
```

| Field | Required | Meaning |
|-------|----------|---------|
| `skill` | yes | Skill directory name. Case-insensitive. |
| `ruleId` | yes | The scanner rule that was reviewed, e.g. `dangerous-exec`. |
| `file` | yes | Path of the reviewed file **relative to the skill directory**. Either slash style. |
| `sha256` | no | Hex SHA-256 of the reviewed file content. When present, the acknowledgement stops applying the moment the file changes. |
| `reason` | no | Operator justification. Carried for audit; never matched on. |

**This is not a "disable scanning" switch, by design:**

- It is scoped to one `skill + ruleId + file` triple. An acknowledgement for `dangerous-exec` in
  one file says nothing about `dangerous-exec` in another file, or about any other rule.
- It does **not widen**. If the acknowledged file is later edited so that a *new* critical rule
  fires, that new finding is unacknowledged and the skill is skipped again — with the new finding
  named in the warning.
- Adding `sha256` pins the approval to the exact content that was reviewed, so any edit at all to
  that file revokes the acknowledgement until a human looks again.

Warn- and info-severity findings never blocked discovery and are unaffected.

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

## Usage Telemetry

The Skills extension records per-skill usage counters (view, use, and patch counts, plus `last_used_at`, `created_by`, and a `pinned` flag) in a SQLite store as skills are loaded, viewed, and edited at runtime. This telemetry is exposed read-only via the skills API:

| Endpoint | Returns |
|----------|---------|
| `GET /api/skills/telemetry` | Usage records for all skills. |
| `GET /api/skills/telemetry/{skillName}` | Usage record for a single skill. |

The telemetry surface is passive — it never changes skill discovery, loading, or content; it only surfaces how skills are being used so operators can spot stale or high-churn skills.

## See Also

- [Skills Guide](/skills) — comprehensive guide to writing and using skills
- [Extension Development](../extension-development.md) — building custom extensions
- [Prompt Pipeline](../development/prompt-pipeline.md) — how skills integrate with the prompt system
