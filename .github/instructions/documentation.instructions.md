# Documentation Instructions

## Purpose

These instructions define documentation standards for BotNexus and how to work with the published documentation site.

## Documentation Structure

- `docs/user-guide/` is for end-user workflows, setup, and operations.
- `docs/development/` is for contributor and implementation guidance.
- `docs/architecture/` is for system design and cross-cutting technical concepts.
- `docs/releases/` is for published release notes and changelogs.

When adding new content, place it in the section that matches the target audience. Do not mix user-focused guidance and developer implementation details on the same page unless that split is intentional and clearly labeled.

## Content Standards

- Keep language clear, direct, and task-oriented.
- Prefer short sections with meaningful headings.
- Use relative links for internal documentation links.
- Keep examples aligned with cross-platform guidance (Windows and Linux).
- Update existing pages instead of creating duplicates when the topic already exists.

## Published Site (VitePress)

The published docs site is built from `docs/` using VitePress.

- Site config: `docs/.vitepress/config.mts`
- Build scripts: `package.json`
  - `npm run docs:dev`
  - `npm run docs:build`
  - `npm run docs:preview`

If you add or move pages intended for the published site:

1. Update navigation and sidebar entries in `docs/.vitepress/config.mts` where needed.
2. Verify links resolve correctly.
3. Run `npm run docs:build` before finalizing documentation changes.

## Scope Clarification

- **User documentation** should explain how to use BotNexus features.
- **Developer documentation** should explain how BotNexus is built, extended, tested, and operated during development.
