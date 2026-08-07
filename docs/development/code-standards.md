# Code Standards

**Purpose:** The single reference for how BotNexus code is expected to look — XML
documentation comments, naming, project/dependency boundaries, and testing
conventions.

This page is the developer-facing companion to [`CONTRIBUTING.md`](https://github.com/sytone/botnexus/blob/main/CONTRIBUTING.md).
`CONTRIBUTING.md` tells you *how to get a change merged*; this page tells you
*what the code in that change has to look like*.

The `AGENTS.md` files in the repository are normative and win on any conflict:

- Root [`AGENTS.md`](https://github.com/sytone/botnexus/blob/main/AGENTS.md) — platform, validation, planning, tests, git workflow
- [`src/domain/AGENTS.md`](https://github.com/sytone/botnexus/blob/main/src/domain/AGENTS.md) — domain purity and Vogen value objects
- [`src/gateway/AGENTS.md`](https://github.com/sytone/botnexus/blob/main/src/gateway/AGENTS.md) — gateway project boundaries and tool architecture
- [`src/agent/AGENTS.md`](https://github.com/sytone/botnexus/blob/main/src/agent/AGENTS.md) and [`src/extensions/AGENTS.md`](https://github.com/sytone/botnexus/blob/main/src/extensions/AGENTS.md)

Read the `AGENTS.md` closest to the code you are changing before you start.

---

## XML Documentation Comments

### Where XML docs are required

XML doc comments are **required on all public types and members** in the
extension-facing contract surface:

| Project | Why it is contract surface |
|---|---|
| `BotNexus.Gateway.Abstractions` | The interfaces extensions implement (`IServiceContributor`, `IEndpointContributor`, `IApiContributor`, `IAgentToolContributor`, satellite contracts). |
| `BotNexus.Gateway.Contracts` | Lightweight shared types crossing the gateway/extension boundary. |
| `BotNexus.Domain` | Domain primitives and models every other layer depends on. |

Elsewhere in `src/`, XML docs are **strongly encouraged** on public types and on
any member whose purpose is not obvious from its signature. They are not required
on test projects.

### What a good summary contains

**A summary explains *why* the member exists and what a caller needs to know —
not a restatement of the signature.** "Gets the agent id." on a property called
`AgentId` is noise; delete it or replace it with the constraint that actually
matters.

Write summaries that answer:

- Why would a caller reach for this rather than the alternative?
- What are the lifetime, threading, or ordering constraints?
- What does the implementer have to guarantee?

Use `<remarks>` for the contract details an implementer must honour, `<param>`
for non-obvious parameters, `<returns>` when the return value has semantics
beyond its type, and `<see cref="..."/>` to link related members so the
cross-reference survives renames.

**Good** — from `IServiceContributor` in `BotNexus.Gateway.Abstractions`:

```csharp
/// <summary>
/// Extension hook for registering services into the host DI container during startup,
/// before the application is built. Implement this when an extension needs to perform
/// arbitrary service registration that the loader's contract-based auto-discovery cannot
/// express - for example configuring authorization policies, options, or replacing a
/// framework-provided default (such as <c>IUserIdProvider</c>).
/// </summary>
/// <remarks>
/// Implementations must expose a public parameterless constructor. The loader instantiates
/// the contributor directly (it is not resolved from the container) and invokes
/// <see cref="ConfigureServices"/> while the service collection is still mutable. This runs
/// in addition to - not instead of - the loader's contract-based registration, so contributors
/// should only register what auto-discovery misses.
/// </remarks>
public interface IServiceContributor
{
    /// <summary>
    /// Registers extension-owned services into the host service collection. Called once during
    /// extension load, before <c>WebApplication</c> is built, so policy/options/default-replacement
    /// registrations take effect for the running host.
    /// </summary>
    void ConfigureServices(IServiceCollection services);
}
```

That summary tells an extension author when to implement the interface, what the
loader guarantees, and what it does *not* replace. None of that is recoverable
from the signature.

**Bad:**

```csharp
/// <summary>
/// Configures services.
/// </summary>
void ConfigureServices(IServiceCollection services);
```

### Style rules

- Use `///` doc comments, not `/* */` blocks, and put them directly above the member.
- Prefer plain sentences over markup soup. `<c>` for identifiers and literals,
  `<see cref="..."/>` for members that exist in the solution.
- Do **not** document a member into existence — if you cannot say why it exists,
  that is a design smell, not a documentation gap.
- Keep summaries accurate under refactor. A stale summary is worse than none,
  because reviewers and agents reading the codebase will trust it.
- Do not suppress documentation warnings with `#pragma warning disable CS1591`.
  Either write the doc or make the member non-public.

### Comments in method bodies

Inline comments explain *why*, never *what*. Prefer a comment that records the
non-obvious constraint or the bug it prevents:

```csharp
// The loader instantiates contributors directly, so a container-resolved
// dependency here would be silently null at extension-load time.
```

Comments that narrate the next line (`// increment the counter`) should be
deleted rather than maintained.

---

## Naming and Language Conventions

- **Nullable is enabled solution-wide** (`Nullable=enable` in
  [`Directory.Build.props`](https://github.com/sytone/botnexus/blob/main/Directory.Build.props)).
  Express nullability in the type; do not use the null-forgiving `!` operator or
  `#nullable disable` to get past a warning.
- **Warnings are errors.** `TreatWarningsAsErrors=true` is set centrally. Fix the
  cause, do not silence it with `#pragma warning disable`.
- **Implicit usings are enabled** — do not re-add `using System;` and friends.
- **Target framework, nullability, and implicit usings are centralized** in
  `Directory.Build.props`. Do not override them per project.
- **Package versions are centralized** in `Directory.Packages.props` (Central
  Package Management). A `PackageReference` in a `.csproj` must not carry a
  `Version` attribute.
- **Interfaces are `I`-prefixed**; async methods returning `Task`/`ValueTask`
  end in `Async`; private fields use `_camelCase`.
- **Indentation is 4 spaces, UTF-8, LF line endings**, per
  [`.editorconfig`](https://github.com/sytone/botnexus/blob/main/.editorconfig).

### Cross-platform code is mandatory

BotNexus runs on **Windows and Linux**, and CI runs both. Production code injects
`IFileSystem` (`System.IO.Abstractions`) and uses `fileSystem.Path.Combine(...)`;
tests and static helpers use `Path.Combine(...)`. Never hardcode `C:\...`,
`/tmp/...`, or a path separator character.

### Value objects use Vogen

New identifiers and single-value wrappers are
[Vogen](https://stevedunn.github.io/Vogen/overview.html) `[ValueObject<T>]`
partial structs in `BotNexus.Domain.Primitives`, with a `Validate` partial
method. **Do not add implicit operators** to or from the backing primitive —
callers use `.From(...)` and `.Value` explicitly so type boundaries stay visible.
Hand-rolled value-object structs fail the architecture fitness functions. Full
rules and the canonical example live in
[`src/domain/AGENTS.md`](https://github.com/sytone/botnexus/blob/main/src/domain/AGENTS.md).

### No dead code, no `[Obsolete]`

This codebase has no external consumers. Delete unused code and update every call
site in the same commit rather than deprecating it. Compatibility shims are
migrate-forward-then-delete — see
[compat-shim-lifecycle.md](compat-shim-lifecycle.md).

---

## Dependency Boundaries

Layering is enforced structurally by the architecture tests, not by convention
alone:

- `src/domain/` has **zero** project references. Every layer depends on it; it
  depends on nothing.
- `src/gateway/` may depend on `src/agent/` and `src/domain/`. It must **not**
  reference `src/extensions/` — the gateway discovers and loads extensions
  dynamically.
- `BotNexus.Gateway.Api` has zero extension knowledge (no SignalR, no MCP
  references).
- `BotNexus.Gateway.Abstractions` is the extension contract surface;
  `BotNexus.Gateway.Contracts` carries lightweight shared types.

Adding a `<ProjectReference>` that crosses one of these boundaries will fail
`tests/architecture/BotNexus.Architecture.Tests/`. That failure is the design
review, not an obstacle to route around.

---

## Testing Conventions

Full detail on selection and the Windows testhost firewall pre-authorization is
in [running-tests.md](running-tests.md).

### Test-first

Write the failing test before the implementation. Never make a pre-existing test
pass by deleting it, and never skip or disable a test to get a green run.

### Tests are migrated, never net-deleted

If you delete a class, you rewrite its tests for the replacement in the same
change. A refactor that reduces coverage is a regression.

### Suite layout

| Suite | Location | Role |
|---|---|---|
| Architecture fitness | `tests/architecture/BotNexus.Architecture.Tests/` | Structurally enforces layering, Vogen, and scenario-suite conventions. Always runs. |
| Scenarios | `tests/scenarios/` | Channel-agnostic acceptance tests. Read `tests/scenarios/AGENTS.md` before adding one. Always runs. |
| Unit / integration | `tests/gateway/`, `tests/agent/`, `tests/domain/`, `tests/extensions/`, `tests/persistence/`, `tests/integration/` | Focused behaviour coverage. |
| Component (bUnit) | alongside the Blazor test projects | **Mandatory** for every `.razor` component — default/empty render, render with data, user interaction, and edge cases (loading, error, empty list). |
| Container / e2e | `tests/container/`, `tests/e2e/` | Full-stack verification. |

### Test warnings are failures

Fix nullable and async warnings in tests properly: add null checks or required
initializers, await every `Task`, and discard deliberately-unused results with
`_ = await ...`. Do not use `Task.Run(...).Wait()` or `.Result` — they hide
warnings and can deadlock.

### Running tests

Compile what you changed before spending a remote gate. A build starts no test
host and no gateway process, so it cannot leak, and it catches in about a second
the compile errors that would otherwise cost a full remote run:

```powershell
dotnet build path/to/Changed.Project.csproj
```

To preview which projects the impacted set would cover (a dry run performs no
test execution and is safe locally):

```powershell
scripts/repo/test-impacted.ps1 -DryRun   # show which projects would run
```

Do not run `test-impacted.ps1` without `-DryRun`, and do not run `dotnet test`,
on a host with a live gateway. The authoritative gate is the validation script,
which executes tests remotely:

```powershell
scripts/repo/Validate-PreCommit.ps1
```

Mode is selected by `BOTNEXUS_VALIDATION_MODE` (`local` or `remote`); the
operational default is **`remote`** (#2158), and `-LocalFallback` is a
backward-compatible alias for opting in to local mode. Local validation spawns real
gateway processes that outlive their parent and steal the live gateway's scheduled
jobs, so do not run it on a host with a live gateway. Stage the exact snapshot you
intend to commit, then
validate, then commit — a passing run emits a content-addressed receipt keyed to
the staged tree, and any mismatch fails closed and revalidates. See
[validation-receipts.md](validation-receipts.md).

Do not use `--no-verify` on a commit containing code changes.

---

## Documentation Expectations for a Change

Before opening a PR, check whether your change requires docs in the same PR:

- Adds or changes configuration → `docs/configuration.md` and/or `docs/user-guide/configuration.md`
- Adds a feature or tool → a page under `docs/features/` or `docs/extensions/`
- Changes a developer workflow → the relevant page under `docs/development/`
- Changes public contract surface → XML docs on the affected members, per the rules above

Commit and PR title format is specified in
[pr-and-commit-conventions.md](pr-and-commit-conventions.md).

---

## Related Documentation

- [`CONTRIBUTING.md`](https://github.com/sytone/botnexus/blob/main/CONTRIBUTING.md) — contributor workflow end to end
- [README.md](README.md) — index of development documentation
- [pr-and-commit-conventions.md](pr-and-commit-conventions.md) — required PR body and squash-commit format
- [running-tests.md](running-tests.md) — impacted-test selection and firewall pre-authorization
- [validation-receipts.md](validation-receipts.md) — content-addressed validation receipts
- [debugging.md](debugging.md) — debugging the Gateway, extensions, and WebUI
- [ddd-patterns.md](ddd-patterns.md) — domain modelling patterns in use
- [../getting-started-dev.md](../getting-started-dev.md) — building and running from source
