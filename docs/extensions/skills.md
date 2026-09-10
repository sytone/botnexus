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

A skill that declares parameters (see [Parameterised skills](#parameterised-skills)) needs values
for them, supplied as a flat object:

```json
{
  "action": "load",
  "skillName": "add-film",
  "parameters": { "title": "Dune" }
}
```

Every declared parameter is **required**. Loading without one is refused rather than substituted
blank -- a skill whose instructions silently lose a value replays wrongly and looks like it worked.
The `list` action names the parameters each skill requires, so they do not have to be discovered by
being refused.

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

### `skill_record`

Turns a run that worked into a reusable skill, through a **propose-and-confirm** cycle. Only
contributed when `AllowSkillCreation` and `AllowSkillRecording` are both enabled.

| Action | Description |
|--------|-------------|
| `steps` | Read back the tool calls this session actually made, from persisted session history |
| `propose` | Stage a draft skill from those steps, with `{{placeholders}}` and declared parameters |
| `review` | Render the pending draft exactly as it would be installed, and issue a confirmation token |
| `confirm` | Install the reviewed draft (requires that token) |
| `discard` | Drop a pending draft |
| `list` | Show pending drafts |

#### Why it works this way

Measurement on a live instance ruled out the simpler designs. Across 1,702 recorded tool calls only
**27% ever repeat verbatim**, so a literal recording replays exactly once. Generalising by diffing
repeated runs needs several runs of "the same task", and deciding which runs are the same task is
the problem the traces cannot answer. And with arguments stripped there is nothing left to abstract
over: the commonest three-step sequence was `bash -> bash -> bash`, 501 times.

So no step here asks a trace to identify parameters. The division of labour is:

- the **trace** supplies the literal values, because it is the only witness to what actually ran;
- the **agent** supplies the semantics, because it is the only party that knows why the steps were
  what they were;
- the **operator** rules on the result, because "does this vary between runs" is a question about
  intent that neither of the other two can answer.

#### Record on the turn *after* the work

The recorder reads **persisted session history**, not the agent's own context. A tool call is not
written there until its turn completes, so an agent that finishes a task and asks for `steps` in the
same turn sees none of them — verified live: a turn that ran `bash` then asked for `steps` got zero,
and asking again on the very next turn returned that same call with its arguments.

That is a constraint of reading history rather than a defect, and reading history is the point: it
is what makes a recording survive compaction, and what stops a proposal being checked against the
agent's account of itself. It also fails safely — `propose` validates against the same empty trace
and refuses, so nothing can be recorded from a run that is not on record. Both messages say so.

#### What keeps a proposal honest

Every declared parameter must record the literal value this run used, and that value is checked
against the recorded tool calls. A value that appears in no call is **rejected** rather than written
into a skill -- the same instinct as the post-turn claim auditor, applied to recordings.

The mirror check runs too: values that appear in both the run and the proposed instructions but were
left hard-coded are listed at review as **values kept fixed**. That is the other half of the question
an operator is being asked -- the agent has said what it thinks varies, and this says what it has
decided is constant.

#### Drafts

A proposal is staged as a draft under `~/.botnexus/agents/<agent>/skill-drafts/<name>/draft.json`.
Drafts are **not** skills: the directory sits outside every root skill discovery scans, and the file
is not named `SKILL.md`, so an unreviewed proposal cannot load for two independent reasons. Argument
*values* never reach a draft file -- only argument key names -- because tool arguments routinely
carry credentials.

`confirm` requires the token `review` issued. The token is a digest of everything that would be
written, so if the draft changes between review and confirm the old token no longer matches and the
write is refused. Be precise about what that proves: the installed skill is byte-identical to the one
displayed. It does **not** prove a person was present -- the agent holds both ends of that exchange.
The human gate is the operator reading the review output, which is why review prints the body in full
and lists the fixed literals rather than counting them.

`confirm` installs through `skill_manage`'s own create path, so a recorded skill passes exactly the
same scope gate, size limit, frontmatter validation and post-write security scan as a hand-written
one.

### Parameterised skills

A skill declares the values that vary between runs in a `parameters:` frontmatter map, and marks
where they go with `{{slot}}`:

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

`skill_record` writes this block from the parameters that were confirmed, so the installed skill
always declares exactly what the operator agreed to; hand-authored skills may declare parameters the
same way. Slot names are matched **case-insensitively**, because the frontmatter parser folds case
and two declarations differing only in case cannot survive being written out. Skills that declare no
parameters load exactly as they always have.

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
| `AllowSkillRecording` | `true` | Enables `skill_record`. Subordinate to `AllowSkillCreation`, since recording ends in a skill being written. |

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

Recorded proposals awaiting confirmation are deliberately **not** here. They live in a sibling
directory that skill discovery never scans:

```text
~/.botnexus/agents/<agent>/
├── skills/                # Discovered and loadable
└── skill-drafts/          # Staged proposals — never discovered
    └── my-skill/
        └── draft.json
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
