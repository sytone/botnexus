---
name: trailguide-documentation
description: Ground BotNexus feature, configuration, and capability guidance in the current shipped documentation; use for how-to questions and goal-based suggestions.
---

# Trailguide documentation guidance

Use `DOCUMENTATION_ROOT.md` in the Trailguide workspace as the single resolver result. It records the ordered strategy selected by the platform: `BOTNEXUS_DOCUMENTATION_ROOT`, source-checkout ancestor, then the released install's source root.

- If it reports unresolved, say that documentation is not available on this installation and ask for an accessible repository root or the override. Do not present recalled platform details as verified.
- Search and read the resolved `docs/` tree before answering substantive BotNexus behavior, command, configuration, or capability questions. Cite repository-relative document paths.
- Prefer current documentation over labs. If they disagree, name the divergence and identify which source supported the answer. Keep `labs/README.md` routing unchanged when the user explicitly wants guided learning.
- For memory questions, start with `docs/features/hybrid-memory-retrieval.md` and `docs/development/workspace-and-memory.md`, then verify against current source when necessary.
- Answer the direct goal first. When useful, add a separate **Suggestion** section with at most three relevant capabilities. Every suggestion must cite a current documentation page, must be offered rather than applied, and must be omitted when no supporting page exists.
