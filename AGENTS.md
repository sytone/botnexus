# Agent Guidelines for BotNexus

## Platform / Runtime

BotNexus runs on **Windows and Linux**. All code, tests, scripts, and documentation must be portable across both platforms. Do not assume a single OS — CI runs on both, and developer machines vary.

## Validation

The authoritative repository gate is remote by default (#2158):

```powershell
scripts/repo/Validate-PreCommit.ps1
```

With nothing configured this runs **remote** Azure Container Apps validation. Set
`BOTNEXUS_VALIDATION_MODE` to `local` or `remote` to override; resolution checks process, user,
then machine environment so user/machine settings work in spawned processes.

**Local validation is opt-in and must not be run on a host with a live gateway.** It spawns real
gateway processes; when their parent dies the children survive, and because every gateway opens
the shared cron store they claim scheduled jobs belonging to the live gateway and fail them. On
2026-08-06 three such orphans - two of them 30+ hours old - starved the live gateway until the
portal would not load. Choose it deliberately when remote infrastructure is genuinely unavailable,
and say so:

```powershell
$env:BOTNEXUS_VALIDATION_MODE = 'local'
scripts/repo/Validate-PreCommit.ps1
```

`-LocalFallback` remains a backward-compatible alias for local mode. The lower-level scripts remain focused diagnostic tools, not substitutes for the strict gate.

**Stage → validate → commit.** Stage the exact snapshot you intend to commit, then run the strict gate, then commit. A successful run emits a content-addressed validation receipt keyed to the staged Git tree hash plus the validation-policy and toolchain identities. The pre-commit hook reuses that receipt to skip redundant build/test only when the exact staged content still matches; any missing, malformed, failed, stale, expired, or mismatched receipt fails closed and reruns validation. See `docs/development/validation-receipts.md`.

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

   By default this invokes remote Azure Container Apps validation; a qualifying exact-content receipt then bypasses redundant remote validation. Set `BOTNEXUS_VALIDATION_MODE=local` to explicitly select globally serialized local strict validation instead. Both modes build the full solution and run impacted tests plus mandatory architecture/scenario and Playwright safety nets.

   The lower-level `test-impacted.ps1 -DryRun` remains useful to preview impacted projects during diagnosis, but it is not an additional pre-push requirement after a qualifying remote receipt.

3. **Zero failures required.** If any test fails, diagnose and fix the issue before proceeding. Do not commit code with failing tests.

4. **Do not skip or disable tests** to make the suite pass. If a test is failing, the production code or the test itself must be fixed — not removed.

5. **Do not use `--no-verify`** for code changes. There is deliberately no pre-commit hook - commit-time local validation is banned, and `scripts/repo/install-hooks.ps1` activates only the `pre-push` `core.bare` guard (#1602). `--no-verify` therefore skips that guard, not a test gate. Run `scripts/repo/Validate-PreCommit.ps1` for the final candidate instead.

6. **Local `dotnet build` is permitted and expected; local test execution is not.** Compile the projects you changed before spending a remote gate - `dotnet build` starts no test host and no gateway process, so it cannot leak, and it catches in about a second the compile errors that otherwise cost a full remote run. Never run `dotnet test`, `test-impacted.ps1`, or `Validate-PreCommit.ps1 -LocalFallback` on a host with a live gateway: a test host boots real gateway processes that survive their parent, claim scheduled jobs from the shared cron store, and starve the live gateway - that is the orphan-process leak #2158 exists to close. All test execution is remote and authoritative: `scripts/repo/Invoke-AzureBuildTest.ps1 -Mode core -WorktreePath <worktree>`. If the remote infrastructure is genuinely unavailable, `Validate-PreCommit.ps1 -ValidationMode local` (or the `-LocalFallback` alias) is the sole supported local gate; state that fallback explicitly whenever you use it.

7. **Documentation-only changes do not run the test gate.** If a change touches nothing but `*.md`, `docs/**`, or `mkdocs.yml`, the required validation is the documentation build, not the ~12-minute remote test suite:

   ```powershell
   npm ci          # first time only
   npm run docs:build
   ```

   This is the same command `deploy-docs.yml` runs, it completes in about 20 seconds, and it is a real gate rather than a renderer - a broken relative link fails it with `[vitepress] N dead link(s) found.` and exit 1. CI agrees: `ci-build-test.yml` lists `docs/**` and `**/*.md` under `paths-ignore`, so a docs-only PR does not trigger the test workflow at all. Running the remote gate on such a change proves nothing about the diff and costs a container run. If a change touches docs **and** code, it is a code change - run the remote gate.

8. **If you introduce new behaviour**, add corresponding tests first (see rule 1).

9. **If you delete a class or service**, you MUST rewrite its tests for the replacement — not delete them.
   - Old class deleted → old test file deleted AND new test file created for the replacement
   - Tests are never net-deleted; they are migrated
   - A refactor that reduces test coverage is a regression

10. **Component tests (bUnit) are mandatory** for all Blazor components. Every `.razor` component must have a corresponding test covering:
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

1. `scripts/repo/Validate-PreCommit.ps1` passes in the selected mode, or remote mode finds a qualifying exact-content strict Azure receipt.
2. Do not rerun build/tests when a qualifying remote receipt applies.
3. Record the chosen validation mode and strict gate evidence.
4. No `--no-verify` used on commits containing code changes.

### Worktree Policy

- **Every task requires a dedicated worktree.** Create one at the start of work:
  ```bash
  git worktree add ../botnexus-wt-N -b <type>/N-<short-slug>
  cd ../botnexus-wt-N
  ```

- **Local `main` must always be clean.** After a worktree is merged and the PR closes,
  remove the worktree through the hardened helper - never with a raw `git worktree remove`:
  ```powershell
  pwsh -NoProfile -File scripts/repo/Remove-Worktree.ps1 -WorktreePath ../botnexus-wt-N -DeleteBranch
  cd ../botnexus; git checkout main; git pull origin main
  ```
  Use the hardened helper `scripts/repo/Remove-Worktree.ps1`: it retries boundedly, returns a structured `locked` outcome when Windows file locks hold the directory, and never deletes the branch unless removal succeeded (issue #2104). Never chain `git worktree remove ...` straight into `git branch -d/-D ...` - on a failed removal that orphans the directory and strands the commits.

- **If you find local changes on `main`:** Move them to a worktree immediately before continuing work:
  1. `git worktree add ../botnexus-recover -b <type>/<recovery-slug>`
  2. Cherry-pick or push the changes to the worktree
  3. `git reset --hard origin/main` on the main repo
  4. Continue work in the worktree, then merge via PR

### Branch & PR Conventions

- `../botnexus-wt-N` — dedicated worktree per issue/PR (N = GitHub issue number)
- Branch naming: `<type>/<issue-number>-<short-slug>` (e.g. `fix/64-history-first-load`, `feat/128-gateway-plugins`)
- Ordinary PRs target `main` and never branch from another feature branch.
- **Stacked PR exception:** use GitHub native stacked PRs only when a plan has at least two dependent, independently reviewable layers. The bottom layer targets `main`; each upper layer targets the branch immediately below it. Unrelated or independently mergeable issues remain separate `main`-based PRs.
- Every stack layer keeps its own issue or explicit partial-work reason, worktree, validation evidence, title/body checks, and merge authorization. Merge bottom-up. A lower-layer change requires affected upper layers to be rebased and revalidated.
- Use GitHub CLI 2.90.0+ with the official `github/gh-stack` extension for stack topology and cascading rebases. Do not hand-maintain PR bases when `gh stack` can do so.
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

Use the selected strict repository gate for normal candidate validation:

```shell
scripts/repo/Validate-PreCommit.ps1
```

A hand-run `dotnet build` is encouraged before validating - it is the cheapest way to catch a compile error, and it never starts a test host. It is not itself a validation gate. Remote is the default (#2158); select local explicitly only when the remote infrastructure is unavailable:

```shell
$env:BOTNEXUS_VALIDATION_MODE = 'local'
scripts/repo/Validate-PreCommit.ps1
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

### Strongly Typed IDs and Value Objects

**A domain concept that has rules gets a type, not a `string`.** BotNexus uses [Vogen](https://github.com/SteveDunn/Vogen) source-generated value objects in `BotNexus.Domain.Primitives` for identifiers and constrained values. The canonical set is `AgentId`, `ConversationId`, `SessionId`, `UserId`, `RunId`, `JobId`, `ToolId`, `ToolName`, `WorkingDir`, `ConversationTitle`, and their siblings in that namespace.

**Why.** Two `string` parameters of the same shape are silently interchangeable. `Foo(string agentId, string conversationId)` compiles perfectly when the arguments are swapped, and the failure surfaces much later as missing data rather than as a build error. A value object makes that swap a compile error, and moves validation from "every call site remembers to check" to "impossible to construct an invalid instance".

**Rules:**

- **Declare with the Vogen attribute, never hand-roll.** `[ValueObject<string>(conversions: Conversions.SystemTextJson)]` on a `readonly partial struct`. Vogen generates `From`, equality, `ToString` and the JSON converter. A hand-written `readonly record struct` with its own `Equals` and a bespoke `JsonConverter` is the pattern being retired - it drifts, and the analyser cannot see it.
- **Provide `Validate` and `NormalizeInput`.** `Validate` returns `Validation.Invalid("<Type> cannot be ...")` with a message naming the type; `NormalizeInput` trims (and canonicalises case where the domain is case-insensitive, as `ToolName` and `ChannelKey` do). Normalising is what makes equality meaningful, because Vogen derives equality from the stored primitive.
- **Never expose implicit conversions to or from the primitive.** An implicit operator reintroduces exactly the silent-substitution hole the type exists to close. Callers use `.From(value)` and `.Value` explicitly. This is enforced by `DomainArchitectureTests`.
- **Validate what is structurally true, not what is momentarily true.** `WorkingDir` guarantees path *shape* (non-empty, no invalid characters, within the length ceiling) and deliberately does not promise the directory exists or is absolute - a value object cannot hold an invariant the filesystem can change underneath it. Containment and traversal safety stay with `PathUtils.ResolvePath` and `IPathValidator`.
- **Reject blanks; do not substitute a default.** If a caller omits a title, that is the caller's decision to make explicitly (see `ConversationFactory`). Defaulting inside the value object lets an empty value travel and reappear as a placeholder far from its origin.
- **Own the limit.** A max length belongs on the value object as a `public const`, and validators derive from it (`ConversationInputValidator.MaxTitleLength = ConversationTitle.MaxLength`). Restating the number in a second place is how the REST error message and the domain invariant become merely coincidentally equal.
- **Use them in non-boundary code; convert at the boundary.** Controllers, DTOs, SQLite column reads and channel wire formats legitimately carry primitives - that is what a boundary is. Convert once on the way in, and pass the typed value everywhere below. The wire representation is unchanged either way: a Vogen `string` value object serialises as a bare JSON string.
- **New value objects are pinned by `DomainArchitectureTests`** (`tests/architecture/BotNexus.Architecture.Tests/`). Add the Vogen-attribute and no-implicit-conversion assertions there when introducing one.
- **The convention is ENFORCED, not merely documented (#3099).** `PrimitiveIdParameterFenceArchitectureTests` (`tests/architecture/BotNexus.Architecture.Tests/PrimitiveIdParameterFenceArchitectureTests.cs`) fails the validation gate when a new `string agentId`, `string conversationId` or `string sessionId` **parameter** is declared in non-boundary code, and its message names the value object to use instead. That class's doc comment carries the authoritative definition of "boundary" — controllers and their request/response DTOs, SQLite column reads, channel wire formats, CLI argument binding — so read it there rather than inferring it from the exemption list. The pre-existing population is frozen in `PrimitiveIdParameterBaseline.baseline`, which is **shrink-only**: adding a violation fails, and so does leaving a count high after fixing a site. When you convert a site, lower or delete its baseline line in the same change. The residual sweep of the frozen population is tracked by #3147.

```csharp
// GOOD - the swap below is a compile error
public Task ArchiveAsync(AgentId agentId, ConversationId conversationId, CancellationToken ct);

// BAD - compiles when the arguments are transposed, fails at runtime as "conversation not found"
public Task ArchiveAsync(string agentId, string conversationId, CancellationToken ct);
```

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
