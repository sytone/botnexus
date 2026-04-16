---
id: improvement-skills-path-resolution
title: "Skills Extension — Expose Base Path on Load"
type: improvement
priority: medium
status: draft
created: 2026-04-15
updated: 2026-04-15
author: nova
depends_on: []
tags: [skills, extension, tools, agent-experience]
ddd_types: [Extension, SkillDefinition]
---

# Skills Extension — Expose Base Path on Load

## Summary

When an agent loads a skill, the `SkillTool` returns the SKILL.md content but not the skill's filesystem path. Skills routinely reference their own files — scripts, reference docs, templates, asset directories — using relative paths. Without knowing the base path, the agent cannot resolve these references and must guess or search.

## Problem

A loaded skill's content says things like:

```markdown
## Path Resolution
$result = & "<skills-dir>/reference-bank/scripts/Resolve-UserDataRoot.ps1" | ConvertFrom-Json
```

Or:

```markdown
See `references/feature-plan-template.md` for the template.
Run `scripts/Sync-AdoFeatures.ps1` to refresh data.
```

The agent sees these relative paths but has no way to resolve them. The `SkillTool` has the `SourcePath` from `SkillDefinition` and knows all three discovery directories (`globalSkillsDir`, `agentSkillsDir`, `workspaceSkillsDir`), but none of this is returned in the load response.

### Current Load Response

```
## Skill: reference-bank

# Reference Bank
Provides persistent, shared reference data storage for skills...
[full SKILL.md content — no path info]
```

### What the Agent Needs

```
## Skill: reference-bank
**Path:** C:\Users\jobullen\.botnexus\skills\reference-bank\

# Reference Bank
Provides persistent, shared reference data storage for skills...
```

With the base path, the agent can:
- `read("C:\Users\jobullen\.botnexus\skills\reference-bank\scripts\Resolve-UserDataRoot.ps1")`
- `exec("powershell", "-File", "C:\Users\jobullen\.botnexus\skills\reference-bank\scripts\Sync-AdoFeatures.ps1")`
- `ls("C:\Users\jobullen\.botnexus\skills\reference-bank\references\")`

Without it, the agent is blind to the skill's file tree.

## Proposed Changes

### 1. `SkillTool.LoadSkill()` — Include Base Path in Response

```csharp
// Current:
return TextResult($"""
    ## Skill: {skill.Name}

    {skill.Content}
    """);

// Proposed:
return TextResult($"""
    ## Skill: {skill.Name}
    **Path:** {skill.SourcePath}

    {skill.Content}
    """);
```

`SkillDefinition.SourcePath` already contains the skill directory path — it just isn't surfaced.

### 2. `SkillTool.ListSkills()` — Include Paths in List Response

When listing loaded skills, also show their base paths:

```
## Loaded Skills
- **ado-work-management**: Unified ADO work management...
  Path: C:\Users\jobullen\.botnexus\skills\ado-work-management\
- **reference-bank**: Shared reference data...
  Path: C:\Users\jobullen\.botnexus\skills\reference-bank\
```

Available (not-yet-loaded) skills don't need paths in the list — the agent can get the path when it loads.

### 3. Include File Manifest (Optional Enhancement)

For richer context, include a brief file listing alongside the path:

```
## Skill: ado-work-management
**Path:** C:\Users\jobullen\.botnexus\skills\ado-work-management\
**Files:** SKILL.md, scripts/Sync-AdoFeatures.ps1, references/features.md, references/epics.md, workflows/daily.md
```

This lets the agent know what's available without needing to `ls` the directory. Could be generated from `SkillDiscovery` scanning the directory at load time. Keep it to top-level files and one level of subdirectories to avoid bloat.

## Implementation

### Files Changed

- `extensions/skills/BotNexus.Extensions.Skills/SkillTool.cs`
  - `LoadSkill()` — prepend `**Path:** {skill.SourcePath}` to response
  - `ListSkills()` — include path for loaded skills
  - (Optional) Add file manifest generation

### Estimated Effort

Small — the data is already there in `SkillDefinition.SourcePath`. This is a formatting change to the tool response.

## Security Consideration

Exposing filesystem paths to the agent is consistent with existing behavior — the agent already has `read`, `write`, `exec`, `ls`, and `glob` tools with access to the skill directories via its workspace and file access policy. The path doesn't grant new access; it just removes the need to guess.

## Success Criteria

- [ ] `skills({ action: "load", skillName: "..." })` response includes the skill's base path
- [ ] `skills({ action: "list" })` response includes paths for loaded skills
- [ ] Agent can resolve relative file references from loaded skills without searching
- [ ] (Optional) File manifest included in load response
