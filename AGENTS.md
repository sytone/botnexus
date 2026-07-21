# Agent Guidelines for BotNexus

## Platform / Runtime

BotNexus runs on **Windows and Linux**. All code, tests, scripts, and documentation must be portable across both platforms. Do not assume a single OS — CI runs on both, and developer machines vary.

## Validation

Azure Container Apps is the default authoritative validation path for every candidate:

```powershell
scripts/repo/Validate-PreCommit.ps1
```

The command reuses a qualifying receipt only when it matches the exact candidate tree and base commit. Otherwise it runs strict remote validation: full solution build, impacted tests (including architecture and scenario safety nets), and Playwright. Do not repeat those build/tests locally after a qualifying receipt.

When Azure is unavailable, opt into the serialized local equivalent explicitly:

```powershell
scripts/repo/Validate-PreCommit.ps1 -LocalFallback
```

The lower-level `build.ps1`, `test.ps1`, and `test-impacted.ps1` scripts remain available for focused diagnosis, but they are not the standard pre-commit or pre-push gate.


## Document Ownership

Some docs have a YAML front-matter header indicating ownership:

```yaml
---
owner: human          # human | ai | shared
author: BotNexus Team
ai-policy: minimal    # minimal | collaborative | open
---
```

**Respect these headers:**
- `minimal` — Fix typos and broken links only. No restructuring, no content removal. Substantive changes require explicit human approval.
- `collaborative` — May propose additions but must not remove or rewrite existing content without approval.
- `open` (default if no header) — May freely update, but don't delete useful content.

**If a doc has `owner: human` and `ai-policy: minimal`, do not rewrite or remove it during cleanup tasks.** This convention exists because a previous cleanup accidentally removed a human-authored document.

## Planning

All planning items (features, bugs, improvements, refactors) are tracked as **GitHub Issues** on `sytone/botnexus`.

**Working with issues:**
- `gh issue list` — browse open issues
- `gh issue view <number>` — read a specific issue
- `gh issue create` — file a new issue
- `gh issue edit <number>` — update an existing issue

**Issue title prefixes** (use one to categorise):
`[Portal]`, `[Gateway]`, `[Agents]`, `[CLI]`, `[Docs]`, `[Skills]`, `[Memory]`, `[Channels]`, `[Platform]`, `[Config]`

**Rules:**
- Do **not** create new `docs/planning/` folders or spec files — specs live in issues now
- When work is complete, close the issue with a comment referencing the PR or commit

## Test Enforcement

**All tests must pass before any task is considered complete.** No exceptions.

### Rules

1. **Write tests before implementation (TDD).** When adding new behaviour:
   - Write the test first — it must fail before the implementation exists
   - Implement until the test passes
   - Never write implementation code to make a pre-existing test pass by deleting the test

2. **Run authoritative validation before every push.** This is not optional:

   ```shell
   scripts/repo/Validate-PreCommit.ps1
   ```

   By default this invokes Azure Container Apps strict validation. A qualifying exact-content receipt bypasses redundant validation. Strict mode builds the full solution, runs impacted tests plus mandatory `Architecture.Tests` and `Scenarios.Tests` safety nets, and runs Playwright. Use `-LocalFallback` only when Azure is unavailable; the fallback is serialized per worktree.

   The lower-level `test-impacted.ps1 -DryRun` remains useful to preview impacted projects during diagnosis, but it is not an additional pre-push requirement after a qualifying remote receipt.

3. **Zero failures required.** If any test fails, diagnose and fix the issue before proceeding. Do not commit code with failing tests.

4. **Do not skip or disable tests** to make the suite pass. If a test is failing, the production code or the test itself must be fixed — not removed.

5. **Do not use `--no-verify`** for code changes. The pre-commit hook verifies an exact-content strict Azure receipt or starts strict Azure validation itself. Strict mode includes the full build, impacted tests, mandatory architecture/scenario safety nets, and strict Playwright coverage; do not run `test-impacted.ps1` again after it passes.

6. **Do not run local `dotnet build` or `dotnet test` as the normal validation gate.** Use Azure validation to avoid worktree output collisions and development-host saturation. For focused diagnosis only, local commands may be used deliberately. If Azure is unavailable, `Validate-PreCommit.ps1 -LocalFallback` is the sole supported local gate; it serializes validation for the worktree and may use `--no-build` internally after its single build.

7. **If you introduce new behaviour**, add corresponding tests first (see rule 1).

8. **If you delete a class or service**, you MUST rewrite its tests for the replacement — not delete them.
   - Old class deleted → old test file deleted AND new test file created for the replacement
   - Tests are never net-deleted; they are migrated
   - A refactor that reduces test coverage is a regression

9. **Component tests (bUnit) are mandatory** for all Blazor components. Every `.razor` component must have a corresponding test covering:
   - Rendering in default/empty state
   - Rendering with data
   - User interactions (clicks, input)
   - Edge cases (loading, error, empty lists)

### Test Warnings

**Fix all compiler warnings in tests, including nullable and async warnings.** Do not use `#nullable disable`, `#pragma warning disable`, or null-forgiving operators (`!`) to silence warnings — fix the underlying code:
- Nullable warnings: Add proper null checks or use required initializers
- Async warnings: Await all `Task` results or mark unused values with `_ = await`
- Do not use `Task.Run(...).Wait()` or `task.Result` — these hide warnings and can deadlock

All test warnings will be treated as test failures once warnings-as-errors is enabled.

## Git Workflow

**All file modifications and commits must happen in a dedicated worktree, never directly on `main`.** This is mandatory for all agents and developers. Local `main` must remain clean and aligned to `origin/main`.

### Pre-Push Checklist

Before every `git push` on a PR branch:

1. `scripts/repo/Validate-PreCommit.ps1` passes, or a qualifying receipt proves the exact candidate already passed strict Azure validation.
2. Do not rerun local build/tests when that receipt qualifies.
3. If Azure is unavailable, use the explicit serialized `-LocalFallback` path and report that evidence.
4. No `--no-verify` used on commits containing code changes.

### Worktree Policy

- **Every task requires a dedicated worktree.** Create one at the start of work:
  ```bash
  git worktree add ../botnexus-wt-N -b <type>/N-<short-slug>
  cd ../botnexus-wt-N
  ```

- **Local `main` must always be clean.** After a worktree is merged and the PR closes:
  ```bash
  git worktree remove ../botnexus-wt-N
  cd ../botnexus && git checkout main && git pull origin main
  ```

- **If you find local changes on `main`:** Move them to a worktree immediately before continuing work:
  1. `git worktree add ../botnexus-recover -b <type>/<recovery-slug>`
  2. Cherry-pick or push the changes to the worktree
  3. `git reset --hard origin/main` on the main repo
  4. Continue work in the worktree, then merge via PR

### Branch & PR Conventions

- `../botnexus-wt-N` — dedicated worktree per issue/PR (N = GitHub issue number)
- Branch naming: `<type>/<issue-number>-<short-slug>` (e.g. `fix/64-history-first-load`, `feat/128-gateway-plugins`)
- PRs always target `main`; never branch off a feature branch
- `~/projects/botnexus` — always on `main`, clean and synced to `origin/main`

### PR Titles

**PR titles must follow Conventional Commits format**, exactly as commit messages do:

```
<type>(<scope>): <short summary>
```

This is critical because GitHub uses the PR title as the squash-merge commit message. A non-conforming PR title produces a non-conforming history entry.

**Rules:**
- Use the same types and scopes as commits: `feat`, `fix`, `refactor`, `docs`, `test`, `chore`, `ci`, `perf`, `build`
- Keep the summary lowercase, imperative mood, no trailing period
- Reference the issue number in the PR body, not the title (e.g. `Closes #128`)

**Examples:**
```
feat(agents): add memory consolidation on session close
fix(gateway): handle null provider response on timeout
docs(agents): add conventional commit rules for PRs
chore(deps): bump Microsoft.Extensions.* to 10.0.1
```

## Build and test validation

Use the remote container gate for normal candidate validation:

```shell
scripts/repo/Validate-PreCommit.ps1
```

A local `dotnet build` is diagnostic-only. Do not run local builds concurrently in the same worktree. When Azure is unavailable, use the explicit serialized fallback instead of hand-running build and test commands:

```shell
scripts/repo/Validate-PreCommit.ps1 -LocalFallback
```

### Build Warnings

**All compiler warnings must be treated as build failures and fixed before a task is complete.** Do not ship code with compiler warnings. This is enforced centrally via `TreatWarningsAsErrors=true` in `Directory.Build.props` once implementation lands. Fix warnings during development rather than ignoring them later.

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

The project uses **`System.IO.Abstractions`** (`TestableIO.System.IO.Abstractions`) for filesystem operations. Production code should inject `IFileSystem` and use its path APIs (`fileSystem.Path.Combine()`, `fileSystem.Path.GetTempPath()`, etc.) rather than calling `System.IO.Path` directly. This enables testability via `MockFileSystem` and ensures consistent cross-platform behaviour.

**Rules:**
- Use `IFileSystem.Path.Combine()` in production code (or `Path.Combine()` in tests and static helpers)
- Use `Path.GetTempPath()` for temporary directories — never hardcode `/tmp/` or `C:\Temp\`
- Use `Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)` for the user home directory, with a fallback to `Environment.GetEnvironmentVariable("HOME")` on Linux/macOS
- Use `Path.DirectorySeparatorChar` or `Path.AltDirectorySeparatorChar` when separator-aware logic is needed
- In test assertions, normalise paths before comparing (e.g., `Path.GetFullPath()`) rather than asserting exact separator characters

```csharp
// GOOD — production code with IFileSystem
var configDir = _fileSystem.Path.Combine(_fileSystem.Path.GetTempPath(), "botnexus", "config");

// GOOD — test setup
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

### Value Objects Use Vogen

All domain identifiers and scalar value wrappers are generated by
[Vogen](https://stevedunn.github.io/Vogen/overview.html). New hand-rolled value-object
structs are not added — the migration of the existing primitives is in progress and the
architecture fitness functions in `tests/architecture/BotNexus.Architecture.Tests/`
structurally prevent regressions.

**Rules:**

- Define new identifiers as `[ValueObject<T>]` partial structs in `BotNexus.Domain.Primitives`.
- Provide a `Validate` partial method that returns `Validation.Invalid(...)` for bad input.
- **Do not** add implicit operators to/from the backing primitive. Callers use `.Value` and
  `.From(...)` explicitly so type boundaries stay visible. Silent string ↔ AgentId conversions
  hid real bugs in the hand-rolled era.
- Sum types (e.g. discriminated unions) stay hand-written record structs — Vogen targets
  single-value wrappers — but the inner case types are still Vogen.

See `src/domain/AGENTS.md` for the full convention and a worked example.

### Scenario Test Suite

Channel-agnostic acceptance tests for the citizen → conversation → session model live under
`tests/scenarios/`. The harness (`BotNexus.Scenarios.Harness`) and the spec project
(`BotNexus.Scenarios.Tests`) are governed by `tests/scenarios/AGENTS.md` and four
architecture fitness functions in
`tests/architecture/BotNexus.Architecture.Tests/ScenarioSuiteArchitectureTests.cs`:

- Scenario tests must not reference any `BotNexus.Extensions.Channels.*` assembly.
- The harness must not reference any channel extension either.
- `VirtualChannelAdapter` must implement `IChannelAdapter`.
- Scenario tests must drive the platform through the harness DSL, never through
  `IServiceProvider` directly.

If a future PR adds scenarios or extends the harness, read `tests/scenarios/AGENTS.md`
first — the conventions there are the answer to "how do I add a new scenario without
recreating the slop?"

### Memory Tool Naming

- The agent-facing tool for persisting notes is **`memory_save`**. Do not call it "memory store."
- `memory_save` appends daily notes to `memory/YYYY-MM-DD.md`.
- `MEMORY.md` is **read-only** during normal turns; it is written only by future consolidation/dreaming processes.
- Terms like "memory store", "index", and "SQLite" refer to internal implementation details — do not surface them in agent-facing docs or tool descriptions.

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
C:\Users\<ALIAS>\.botnexus\config.json
```

Use the BotNexus CLI to manage configuration:

```shell
dotnet run --project src\gateway\BotNexus.Cli -- <command>
```
