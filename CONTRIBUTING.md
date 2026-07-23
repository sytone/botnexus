# Contributing to BotNexus

Thanks for wanting to poke the machine responsibly. BotNexus is an experimental
C#/.NET platform for orchestrating LLM agents, tools, channels, and memory. This
guide covers everything you need to build from source, make a change, and open a
pull request that CI (and your fellow humans) will accept.

For a higher-level tour of the project, start with the [README](README.md). For
deeper topics, see the [Documentation](#documentation-map) section below.

---

## Prerequisites

| Requirement | Version | Notes |
|---|---|---|
| **.NET SDK** | **10.0.204 or later** | Pinned in [`global.json`](global.json) with `rollForward: latestMinor`. Earlier SDKs (including .NET 9) are **not** supported. |
| **PowerShell** | **7+** (`pwsh`) | Required for the repo helper and validation scripts under `scripts/`. |
| **Git** | Recent | Worktrees are used heavily (see [Development Workflow](#development-workflow)). |

Verify your SDK:

```bash
dotnet --version
# Must output 10.0.204 or later
```

Download the .NET 10 SDK: <https://dotnet.microsoft.com/download/dotnet/10.0>

> BotNexus runs on **Windows and Linux**. All code, tests, scripts, and docs must
> be portable across both — CI runs on both and developer machines vary.

---

## Getting Started (Dev Setup)

Clone the repo and restore tools and packages:

```bash
git clone https://github.com/sytone/botnexus.git
cd botnexus

dotnet tool restore   # installs pinned tools from dotnet-tools.json (dotnet-affected, etc.)
dotnet restore        # restores NuGet packages (Central Package Management)
dotnet build          # builds the solution (BotNexus.slnx)
```

Run the CLI directly from source:

```bash
dotnet run --project src/gateway/BotNexus.Cli -- init
dotnet run --project src/gateway/BotNexus.Cli -- provider setup
dotnet run --project src/gateway/BotNexus.Cli -- gateway start
```

Or run the gateway in the foreground:

```bash
dotnet run --project src/gateway/BotNexus.Cli -- serve
```

The WebUI and REST API serve at `http://localhost:5005`. For a fuller walkthrough
see the [Developer Setup guide](docs/getting-started-dev.md).

Optional: install the local pre-commit validation hook (see
[Validation](#validation)):

```powershell
pwsh scripts/install-pre-commit-hook.ps1
```

---

## Development Workflow

### Worktrees and Branches

**All file modifications and commits happen in a dedicated worktree — never
directly on `main`.** Local `main` stays clean and aligned to `origin/main`.

```bash
# Create a worktree + branch off up-to-date main
git worktree add ../botnexus-wt/<issue-number>-<slug> -b <type>/<issue-number>-<slug>
cd ../botnexus-wt/<issue-number>-<slug>
```

Branch naming: `<type>/<issue-number>-<short-slug>` — for example
`fix/64-history-first-load` or `feat/128-gateway-plugins`. PRs always target
`main`; never branch off another feature branch.

Planning lives in **GitHub Issues** on `sytone/botnexus` (not in `docs/planning/`).
Use `gh issue list` / `gh issue view <number>` to find work.

### Conventional Commits

All commit messages use [Conventional Commits](https://www.conventionalcommits.org/):

```
<type>(<scope>): <short summary>
```

- **Types:** `feat`, `fix`, `refactor`, `docs`, `test`, `chore`, `ci`, `perf`, `build`
- **Scope:** the area affected — e.g. `cli`, `gateway`, `scripts`, `domain`, `extensions`, `tools`
- Keep the summary lowercase, imperative mood, no trailing period
- Commit in **small logical batches** — one feature, one fix, one refactor per commit
- Multi-line bodies are encouraged for non-trivial changes: explain *what* and *why*, not *how*

**Examples:**

```
feat(cli): add serve command with gateway and probe subcommands
fix(gateway): prevent duplicate session writes on concurrent requests
refactor(scripts): simplify start-gateway.ps1 to delegate to CLI
docs(agents): add conventional commit rules for PRs
chore(deps): bump Microsoft.Extensions.* to 10.0.1
```

### Pull Requests

- **PR titles must also follow Conventional Commits** — GitHub uses the PR title as
  the squash-merge commit message, so a non-conforming title produces a
  non-conforming history entry.
- Reference the issue number in the PR **body** (e.g. `Closes #128`), not the title.
- When work is complete, close the issue with a comment referencing the PR or commit.

---

## Code Style

BotNexus enforces a few hard conventions. The full detail lives in
[`AGENTS.md`](AGENTS.md) and the per-area `AGENTS.md` files under `src/`.

- **Cross-platform paths.** Production code injects `IFileSystem`
  (`System.IO.Abstractions`) and uses `fileSystem.Path.Combine(...)` rather than
  hardcoded separators or `/tmp/`. Use `Path.Combine()` in tests and static
  helpers. Never hardcode `C:\...` or `/tmp/...`.
- **Value objects use [Vogen](https://stevedunn.github.io/Vogen/overview.html).**
  New identifiers are `[ValueObject<T>]` partial structs in
  `BotNexus.Domain.Primitives` with a `Validate` partial method. Do **not** add
  implicit operators — callers use `.Value` and `.From(...)` explicitly. See
  [`src/domain/AGENTS.md`](src/domain/AGENTS.md).
- **XML docs on public API.** All public methods and properties need a
  `<summary>` explaining *why* the member exists and the context a caller needs —
  not a restatement of the signature.
- **Warnings are errors.** Fix all compiler warnings during development;
  `TreatWarningsAsErrors=true` is enforced centrally. Do not silence warnings with
  `#pragma warning disable`, `#nullable disable`, or null-forgiving `!`.
- **No dead code, no `[Obsolete]`.** This codebase has no external consumers —
  delete unused code and update call sites in the same commit instead of
  deprecating.

MSBuild conventions are centralized: `TargetFramework`, `Nullable`, and
`ImplicitUsings` come from [`Directory.Build.props`](Directory.Build.props), and
all package versions live in
[`Directory.Packages.props`](Directory.Packages.props) (Central Package
Management — no `Version` on individual `PackageReference`s).

---

## Project Structure

```text
botnexus/
├── src/
│   ├── agent/          agent core + provider adapters (Copilot, OpenAI, Anthropic, ...)
│   ├── domain/         BotNexus.Domain — domain model and Vogen value objects
│   ├── extensions/     channels (SignalR, Telegram, ServiceBus, TUI), tools, MCP, skills
│   ├── gateway/        gateway, CLI, cron, sessions, prompts, memory, tools, webhooks
│   └── persistence/    BotNexus.Persistence.Sqlite
├── tests/              unit, integration, architecture, scenario, component, e2e tests
├── docs/               published documentation site (MkDocs Material)
├── scripts/            build, test, repo, and local development scripts
├── examples/           experiments and sample integrations
└── tools/              supporting utilities
```

The solution file is [`BotNexus.slnx`](BotNexus.slnx). Many directories carry
their own `AGENTS.md` with area-specific conventions — read the one closest to the
code you are changing.

---

## Running Tests

**All tests must pass before a change is complete.** Prefer Test-Driven
Development: write the failing test first, then implement until it passes.

Run only the tests impacted by your change (plus the Architecture and Scenario
safety-net suites):

```powershell
scripts/repo/test-impacted.ps1
```

This uses `dotnet-affected` to select affected test projects and always adds the
`*.Architecture.Tests` and `*.Scenarios.Tests` projects as a safety net. See
[Running Impacted Tests](docs/development/running-tests.md) for parameters
(`-From`, `-Configuration`, `-All`, `-NoBuild`, `-DryRun`) and the Windows
testhost firewall pre-authorization details.

Architecture fitness functions live in
`tests/architecture/BotNexus.Architecture.Tests/` and structurally enforce the
Vogen and scenario-suite conventions. Channel-agnostic acceptance tests live under
`tests/scenarios/` — read `tests/scenarios/AGENTS.md` before adding one. Every
Blazor `.razor` component requires a corresponding bUnit component test.

---

## Validation

The authoritative pre-push gate is:

```powershell
scripts/repo/Validate-PreCommit.ps1
```

Mode is selected via `BOTNEXUS_VALIDATION_MODE` (`local` or `remote`); the
operational default is `local`, which performs one full solution build, impacted
tests including the architecture and scenario safety nets, and Playwright under a
global host lock. Select remote Azure validation explicitly when required:

```powershell
$env:BOTNEXUS_VALIDATION_MODE = 'remote'
scripts/repo/Validate-PreCommit.ps1
```

Install the git pre-commit hook so validation runs automatically:

```powershell
pwsh scripts/install-pre-commit-hook.ps1
```

Do not use `--no-verify` on commits containing code changes, and do not treat a
hand-run `dotnet build`/`dotnet test` as the validation gate — it is
diagnostic-only.

---

## Debugging and Deeper Topics

The `docs/development/` and `docs/training/` trees carry the in-depth material:

| Topic | Page |
|---|---|
| Agent execution model | [docs/development/agent-execution.md](docs/development/agent-execution.md) |
| Message flow | [docs/development/message-flow.md](docs/development/message-flow.md) |
| Prompt pipeline | [docs/development/prompt-pipeline.md](docs/development/prompt-pipeline.md) |
| LLM request lifecycle | [docs/development/llm-request-lifecycle.md](docs/development/llm-request-lifecycle.md) |
| Gateway crash diagnostics | [docs/development/gateway-crash-diagnostics.md](docs/development/gateway-crash-diagnostics.md) |
| WebUI connection issues | [docs/development/webui-connection.md](docs/development/webui-connection.md) |
| Session stores | [docs/development/session-stores.md](docs/development/session-stores.md) |
| Workspace and memory | [docs/development/workspace-and-memory.md](docs/development/workspace-and-memory.md) |
| DDD patterns | [docs/development/ddd-patterns.md](docs/development/ddd-patterns.md) |
| Agent core (training) | [docs/training/02-agent-core.md](docs/training/02-agent-core.md) |
| Coding agent (training) | [docs/training/03-coding-agent.md](docs/training/03-coding-agent.md) |
| Tool development (training) | [docs/training/09-tool-development.md](docs/training/09-tool-development.md) |
| Tool security (training) | [docs/training/tool-security.md](docs/training/tool-security.md) |

---

## Documentation Map

- **Docs site:** <https://sytone.github.io/botnexus/>
- [Getting Started](docs/getting-started.md) — pick the right setup path
- [Developer Setup](docs/getting-started-dev.md) — build, run, test from source
- [Configuration Guide](docs/configuration.md)
- [CLI Reference](docs/cli-reference.md)
- [Architecture Overview](docs/architecture/overview.md)
- [Extension Development](docs/extension-development.md)

---

## Reporting Security Issues

Please review [`SECURITY.md`](SECURITY.md) for how to report vulnerabilities
responsibly. Do not open a public issue for a security problem.

---

Welcome aboard. Build something interesting, keep `main` clean, and let the build
warnings stay errors.
