# Agent Guidelines for BotNexus

## Document Ownership

Some docs have a YAML front-matter header indicating ownership:

```yaml
---
owner: human          # human | ai | shared
author: Jon Bullen
ai-policy: minimal    # minimal | collaborative | open
---
```

**Respect these headers:**
- `minimal` — Fix typos and broken links only. No restructuring, no content removal. Substantive changes require explicit human approval.
- `collaborative` — May propose additions but must not remove or rewrite existing content without approval.
- `open` (default if no header) — May freely update, but don't delete useful content.

**If a doc has `owner: human` and `ai-policy: minimal`, do not rewrite or remove it during cleanup tasks.** This convention exists because a previous cleanup accidentally removed a human-authored document.

## Planning Docs

Design specs and bug specs live in `docs/planning/`. Each item is a folder containing `design-spec.md` and optionally `research.md`.

**Archival convention:**
- Active items: `docs/planning/<item-name>/`
- Archived items: `docs/planning/archived/<item-name>/`
- When a topic is done: update `status: done` in the spec's YAML frontmatter, then move the whole folder to `archived/` as-is -- no renaming
- The canonical archive folder is `archived/` (not `archive/`)
- **Master index:** `docs/planning/INDEX.md` lists all specs (active + archived) with status and summary
- **Lifecycle skill:** See `.github/skills/planning-management/SKILL.md` for the full spec template, lifecycle, and workflows

## Test Enforcement

**All tests must pass before any task is considered complete.** No exceptions.

### Rules

1. **Run the full test suite** before committing any code change:

   ```shell
   dotnet test BotNexus.slnx --nologo --tl:off
   ```

2. **Zero failures required.** If any test fails, diagnose and fix the issue before proceeding. Do not commit code with failing tests.

3. **Do not skip or disable tests** to make the suite pass. If a test is failing, the production code or the test itself must be fixed — not removed.

4. **Do not use `--no-verify`** for code changes. The pre-commit hook runs the test suite and must pass.

5. **If you introduce new behavior**, add corresponding tests. If you change existing behavior, update affected tests to match.

## Build

```shell
dotnet build BotNexus.slnx --nologo --tl:off
```

Build the full solution before running tests to avoid stale assembly issues (e.g., CLI integration tests depend on `BotNexus.Cli.dll` being built).

## Code Practices

### Never Guess Time

**Never assume or calculate the current time.** Always run `Get-Date` to get the local user time. Do not convert UTC timestamps to local time manually — you will get it wrong.

### No `[Obsolete]` Attributes

**Never mark code as `[Obsolete]`.** This codebase has no external consumers — delete dead code instead of deprecating it. If a method, class, or interface is no longer needed, remove it and update all call sites in the same commit.

### No Dead Code

Remove unused methods, classes, and parameters rather than commenting them out or leaving them for "future use." If something isn't called, it shouldn't exist.

## Commits

Use [Conventional Commits](https://www.conventionalcommits.org/) for all commit messages.

### Rules

1. **Format:** `<type>(<scope>): <short summary>`
2. **Types:** `feat`, `fix`, `refactor`, `docs`, `test`, `chore`, `ci`, `perf`, `build`
3. **Scope:** Use the area of the codebase affected (e.g., `cli`, `gateway`, `scripts`, `domain`, `extensions`).
4. **Commit in small batches.** Each commit should be a single logical change — one new feature, one refactor, one bug fix. Do not bundle unrelated changes.
5. **Multi-line body** is encouraged for non-trivial changes. Explain *what* and *why*, not *how*.

### Examples

```
feat(cli): add serve command with gateway and probe subcommands
fix(gateway): prevent duplicate session writes on concurrent requests
refactor(scripts): simplify start-gateway.ps1 to delegate to CLI
docs(planning): archive completed provider-routing spec
test(domain): add missing edge case for session expiry
```

## Configuration

The BotNexus development configuration file is located at:

```
C:\Users\jobullen\.botnexus\config.json
```

Use the BotNexus CLI to manage configuration:

```shell
dotnet run --project src\gateway\BotNexus.Cli -- <command>
```
