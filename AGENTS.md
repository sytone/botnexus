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

## MSBuild Conventions

Common properties and package versions are centralized — **do not duplicate them in individual csproj files.**

- **`Directory.Build.props` (root):** Sets `TargetFramework` (`net10.0`), `ImplicitUsings`, `Nullable`, and version metadata. All projects inherit these automatically.
- **`tests/Directory.Build.props`:** Chains to the root props and adds test-specific defaults (`IsPackable`, `RunSettingsFilePath`, Shouldly reference). All test projects under `tests/` inherit both.
- **`Directory.Packages.props` (root):** Central Package Management — all `PackageVersion` entries live here. Individual csproj `PackageReference` elements must **not** include a `Version` attribute.

**When adding a new project:**
1. Do not add `TargetFramework`, `ImplicitUsings`, or `Nullable` — inherited from root.
2. Add any new NuGet packages to `Directory.Packages.props` first, then reference without `Version` in the csproj.
3. Only set properties in the csproj that differ from the defaults (e.g., `OutputType`, `RootNamespace`, `Description`).

## Code Practices

### Cross-Platform Path Handling

**All file paths must be constructed using `Path.Combine()` and platform APIs.** BotNexus runs on Windows, Linux, and macOS — hardcoded paths break portability.

**Rules:**
- Use `Path.Combine()` to build all file paths — never concatenate strings with `/` or `\`
- Use `Path.GetTempPath()` for temporary directories — never hardcode `/tmp/` or `C:\Temp\`
- Use `Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)` for the user home directory, with a fallback to `Environment.GetEnvironmentVariable("HOME")` on Linux/macOS
- Use `Path.DirectorySeparatorChar` or `Path.AltDirectorySeparatorChar` when separator-aware logic is needed
- In test assertions, normalise paths before comparing (e.g., `Path.GetFullPath()`) rather than asserting exact separator characters

```csharp
// GOOD — works on all platforms
var configDir = Path.Combine(Path.GetTempPath(), "botnexus-tests", Guid.NewGuid().ToString("N"));
var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
    is { Length: > 0 } home ? home : Environment.GetEnvironmentVariable("HOME") ?? "/tmp";

// BAD — breaks on Linux
var configDir = "C:\\Users\\test\\.botnexus";
var tempDir = "/tmp/botnexus-tests";
var path = workspace + "\\" + filename;
```

### Shell Command Tests

When testing shell execution, ensure commands work on both `bash` and `pwsh`:
- Use `RuntimeInformation.IsOSPlatform()` for platform-specific test branches if unavoidable
- Prefer cross-platform commands (pwsh `Write-Output` works everywhere pwsh is installed)
- Never hardcode `cmd.exe` or `/bin/bash` paths in tests — use the ShellTool abstraction

### Never Guess Time

**Never assume or calculate the current time.** Always run `Get-Date` to get the local user time. Do not convert UTC timestamps to local time manually — you will get it wrong.

### No `[Obsolete]` Attributes

**Never mark code as `[Obsolete]`.** This codebase has no external consumers — delete dead code instead of deprecating it. If a method, class, or interface is no longer needed, remove it and update all call sites in the same commit.

### No Dead Code

Remove unused methods, classes, and parameters rather than commenting them out or leaving them for "future use." If something isn't called, it shouldn't exist.

### XML Documentation on Public API

All public methods and properties must have XML doc comments (`<summary>`). Focus on **why** the member exists and the **context** a caller needs — not a restatement of what the code does.

```csharp
// GOOD — explains why and when to use it
/// <summary>
/// Resolves the agent workspace directory, creating it if this is the
/// agent's first activation. Called during session startup to ensure
/// personality files (SOUL.md, IDENTITY.md) are available before the
/// prompt pipeline runs.
/// </summary>
public string EnsureWorkspace(AgentId agentId) { ... }

// BAD — restates the code
/// <summary>
/// Gets the workspace path for the given agent ID.
/// </summary>
public string EnsureWorkspace(AgentId agentId) { ... }
```

**Rules:**
- Describe **intent and context**, not implementation details visible in the signature.
- Mention non-obvious side effects (e.g., creates directories, writes files, triggers events).
- Document when `null` is a valid return and what it means.
- For interfaces, document the **contract** — what implementers must guarantee.

### Comments on Private Members

Private methods and properties don't require XML doc comments, but **add meaningful comments when the intent isn't obvious from the code alone.** Use your judgement — if a future developer or AI agent would need to understand *why* something is done a particular way, leave a comment.

Good candidates for private-member comments:
- Non-obvious business rules or invariants
- Workarounds for platform quirks or upstream bugs
- Coordination between multiple private methods that form a pipeline
- Magic numbers, thresholds, or retry logic with specific reasoning
- Thread-safety considerations or lock ordering

```csharp
// GOOD — explains a non-obvious design choice
// Debounce config reloads to 500ms because FileSystemWatcher fires
// multiple events for a single save on some editors (VS Code, Rider).
private void OnConfigChanged(object sender, FileSystemEventArgs e) { ... }

// UNNECESSARY — the code is self-explanatory
// Increments the counter
private void IncrementCounter() { ... }
```

## Commits

Use [Conventional Commits](https://www.conventionalcommits.org/) for all commit messages.

ALWAYS commit after a related set of changes, do not wait until the end of the session.

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
