---
name: "prompt-template-resolver-parity"
description: "Keep prompt template behavior consistent across CLI and cron by reusing one resolver pipeline."
domain: "platform-cli"
confidence: "high"
source: "earned"
---

## Context
Use this when adding or changing prompt-template behavior in CLI, cron, or chat-triggered flows.

## Patterns
1. Reuse CronOptionsPromptTemplateResolver from CLI instead of implementing custom placeholder parsing.
2. Convert PlatformConfig.PromptTemplates into CronOptions.PromptTemplates before rendering/listing.
3. Resolve prompt files from BOTNEXUS_HOME so shared (prompts/) and agent/workspace prompt folders are discovered consistently.
4. Support both `.prompt.md` and `.prompt.json`; when both formats exist for the same template name, prefer `.prompt.md`.
5. Parse `.prompt.md` front matter with bounded keys (`name`, `defaults`, `parameters`) and preserve markdown body line breaks for rendering.
6. Keep prompt run as render-first then gateway /api/chat invocation.

## Examples
- src\\gateway\\BotNexus.Cli\\Commands\\PromptCommands.cs
- src\\gateway\\BotNexus.Cron\\Prompts\\CronOptionsPromptTemplateResolver.cs

## Anti-Patterns
- Duplicating regex placeholder parsing in CLI when resolver logic already exists.
- Listing only config templates while ignoring file-backed prompt libraries.
- Diverging required/default parameter handling between cron and CLI code paths.
