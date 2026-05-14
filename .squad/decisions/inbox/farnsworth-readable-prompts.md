# Farnsworth Decision Inbox — Readable Prompt Templates

## Context

Issue #29 needs prompt templates that are human-readable for multi-line content (headings, paragraphs, bullet/numbered lists) while keeping existing `.prompt.json` templates working.

## Decision

Adopt dual file-format support in the shared prompt resolver:

- `.prompt.md` with YAML front matter (`name`, `defaults`, `parameters`) and markdown body as prompt text.
- `.prompt.json` retained for compatibility and machine-generated/simple templates.

When both formats exist for the same template name in a directory, `.prompt.md` takes precedence.

## Why

This keeps existing JSON workflows stable while making real-world prompt authoring and review substantially easier for humans.
