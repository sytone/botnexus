---
name: "planning-management"
description: "Design spec lifecycle management — creating, reviewing, transitioning, and archiving specs in docs/planning/"
domain: "planning"
confidence: "high"
source: "nova"
---

# Planning Management

Manages the lifecycle of design specs in `docs/planning/`. Every feature, improvement, and bug fix starts as a spec, moves through a defined lifecycle, and gets archived when complete.

## Folder Structure

```
docs/planning/
  INDEX.md                              # Master index of all specs (active + archived)
  <type>-<name>/                        # Active spec folders
    design-spec.md                      # The spec (required)
    research.md                         # Research notes (optional)
  archived/                             # Completed/closed specs
    <type>-<name>/                      # Moved here as-is, no renaming
      design-spec.md
      research.md
```

## Naming Convention

Folder names are prefixed by type:

| Prefix | Type |
|--------|------|
| `bug-` | Bug fix |
| `feature-` | New feature |
| `improvement-` | Enhancement to existing functionality |

Examples: `bug-session-switching-ui`, `feature-media-pipeline`, `improvement-skills-path-resolution`

## Design Spec Template

Every `design-spec.md` starts with YAML frontmatter:

```yaml
---
id: <type>-<kebab-case-name>           # Must match folder name
title: "Human-Readable Title"
type: bug | feature | improvement
priority: critical | high | medium | low
status: draft | ready | in-progress | delivered | done
created: YYYY-MM-DD
updated: YYYY-MM-DD
author: <who wrote it>
depends_on: []                          # IDs of specs this depends on
tags: [relevant, tags]
ddd_types: [DomainType1, DomainType2]   # Affected domain types (optional)
---
```

### Required Sections

1. **Summary** — What and why, 2-3 sentences max
2. **Problem** — What's broken or missing
3. **Proposal / Design** — How to fix it
4. **Implementation** — Phases, tasks, or steps

### Optional Sections

- **Research** — Background findings (or use separate `research.md`)
- **Acceptance Criteria** — How to verify it's done
- **Risks / Considerations** — Edge cases, security, performance
- **Dependencies** — What must exist first

## Lifecycle

```
draft ──> ready ──> in-progress ──> delivered ──> done ──> archived
  │         │          │               │           │         │
  │         │          │               │           │         └─ Folder moved to archived/
  │         │          │               │           └─ Human reviews and approves
  │         │          │               └─ Squad/dev completes implementation
  │         │          └─ Squad/dev picks up and works on it
  │         └─ Human approves spec as ready for implementation
  └─ Author drafts spec, not yet ready for review
```

### Status Definitions

| Status | Meaning | Who transitions |
|--------|---------|-----------------|
| `draft` | Being written, not ready for review or work | Author |
| `ready` | Spec reviewed, approved for implementation | Human (Jon) |
| `in-progress` | Actively being implemented | Human or Squad (when automated) |
| `delivered` | Implementation complete, PR ready for review | Squad/dev |
| `done` | Reviewed and accepted | Human (Jon) |

### Archival

When a spec reaches `done`:
1. Update `status: done` in frontmatter
2. Move the **entire folder** to `archived/` as-is — no renaming
3. Update `INDEX.md` — move the entry to the Archived section

## INDEX.md Maintenance

The index at `docs/planning/INDEX.md` is the single source of truth for all specs. It has two sections:

### Active Section
Lists all specs NOT in `archived/`, sorted by type then priority.

### Archived / Done Section
Lists all specs in `archived/`, sorted by type.

### Index Entry Format

Each entry is a table row:

```markdown
| ID | Type | Priority | Status | Created | Summary |
```

### When to Update INDEX.md

- **Spec created** — add row to Active section
- **Status changed** — update the status column
- **Spec archived** — move row from Active to Archived section
- **Periodic refresh** — re-scan all folders and reconcile

## Workflows

### Creating a New Spec

1. Create folder: `docs/planning/<type>-<name>/`
2. Create `design-spec.md` with frontmatter template (status: `draft`)
3. Optionally create `research.md` for background research
4. Add entry to `INDEX.md` Active section

### Transitioning Status

1. Update `status` field in frontmatter
2. Update `updated` date
3. Update `INDEX.md` status column

### Archiving a Spec

1. Set status to `done` in frontmatter
2. Move folder: `docs/planning/<name>/` -> `docs/planning/archived/<name>/`
3. Move INDEX.md entry from Active to Archived section

### Refreshing the Index

Scan all folders in `docs/planning/` and `docs/planning/archived/`, read frontmatter, and regenerate INDEX.md. Use this when the index drifts out of sync.

## Rules

- **Never delete specs** — archive them
- **Never rename folders** when archiving — move as-is
- **One canonical archive folder**: `archived/` (not `archive/`)
- **Frontmatter is the source of truth** for status — INDEX.md mirrors it
- **Squad ignores `draft` specs** — only `in-progress` or later can be worked on
- **Only humans transition to `done`** — Squad delivers, humans approve
