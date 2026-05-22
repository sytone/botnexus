# Contributing to BotNexus

Thank you for contributing. This guide covers everything you need to get a change merged.

---

## Quick start

```powershell
git clone https://github.com/sytone/botnexus.git
cd botnexus
.\scripts\install-pre-commit-hook.ps1   # install the pre-commit hook (once)
.\scripts\dev-loop.ps1                  # build + test + run gateway
```

Full developer setup: [docs/getting-started-dev.md](docs/getting-started-dev.md).

---

## Branch and PR workflow

1. **Sync `main` first** — always branch from a current `main`:
   ```powershell
   git checkout main
   git pull --ff-only
   ```
   If `pull --ff-only` fails, investigate before proceeding. Do not force or rebase `main`.

2. **Create a feature branch** — never commit directly to `main`:
   ```powershell
   git checkout -b feat/short-description
   # or: fix/short-description, chore/short-description, docs/short-description
   ```

3. **Make your changes** — follow the TDD cycle below.

4. **Build and test the full solution** — see [Build requirements](#build-requirements) below.

5. **Commit** — follow [Conventional Commits](#commit-message-format).

6. **Open a PR** against `main`.

---

## Build requirements

**Always build from the repository root.** Never build individual projects in isolation.

```powershell
# Fresh clone or new worktree (no obj/ directory):
dotnet restore

# Every change — full solution build:
dotnet build --no-restore

# All tests:
dotnet test --no-build
```

**Why full-solution builds matter:**

- CI runs `dotnet build --no-restore` from the repo root against `BotNexus.slnx`. Your local build must match.
- Project-level builds miss cross-project type errors, sibling test failures, and Linux-specific API differences.
- If the full build or any test fails: **fix it before committing**. Do not open a PR with a failing build.

### Common pitfalls

| Pitfall | Fix |
|---------|-----|
| `CS1503: cannot convert string to ReadOnlySpan<char>` | Use `.TrimEnd('/')` (char overload), not `.TrimEnd("/")` (string overload) — the string overload is .NET 10 / Linux only |
| Interface changed but mocks still compile | Run full-solution tests — sibling test project failures surface there |
| Syntax error in a test file not caught locally | Build from solution root — project-level builds don't catch cross-project syntax errors |
| `dotnet test` reports 0 tests run | You ran `dotnet test` from a project directory — run from the repo root |

---

## Commit message format

BotNexus uses [Conventional Commits](https://www.conventionalcommits.org/). Every commit message **and** PR title must follow this format:

```
type(scope): short description

optional body

Closes #NNN
```

### Types

| Type | When to use |
|------|-------------|
| `feat` | New feature or capability |
| `fix` | Bug fix |
| `chore` | Maintenance, dependency bumps |
| `docs` | Documentation only |
| `refactor` | Code restructuring without behaviour change |
| `test` | New or updated tests only |
| `perf` | Performance improvement |
| `style` | Formatting (no logic change) |
| `ci` | CI/CD pipeline changes |
| `build` | Build system changes |

### Scope

The component name: `gateway`, `portal`, `cron`, `sessions`, `channels`, `cli`, `agents`, `memory`, `providers`, `extensions`, `tests`. **Not** the issue number.

### Rules

- Description: **lowercase, imperative, no trailing period, no issue number**
- Issue number goes in the **footer only**: `Closes #NNN` or `Fixes #NNN`
- Breaking changes: append `!` after type/scope — e.g. `feat(api)!: ...`

### Examples

```
feat(portal): add agent dashboard homepage

Closes #390
```

```
fix(cron): deduplicate conversation creation on concurrent job runs

Closes #403
```

```
chore(deps): bump Microsoft.Extensions to 10.0.1
```

PR titles follow the same rule — `type(scope): description` only, **no issue number in the title**.

---

## Test-driven development

Write failing tests **before** implementing. Every PR must include:

- **Happy path** tests
- **Sad/error path** tests (null inputs, not-found, unauthorized, etc.)

Run the full solution tests to confirm all pass before opening a PR.

---

## Code style

- Follow existing patterns in the file you are editing
- Use `char` overloads for string methods where available (`.TrimEnd('/')` not `.TrimEnd("/")`)
- XML doc comments on all public interfaces and methods in `Abstractions` projects
- Prefer immutable types; avoid mutable statics

---

## Working with worktrees (multi-issue development)

If you need to work on multiple issues in parallel, use git worktrees:

```powershell
git worktree add ..\botnexus-wt\feat-my-feature -b feat/my-feature
Set-Location ..\botnexus-wt\feat-my-feature
dotnet restore
dotnet build --no-restore
dotnet test --no-build
```

Each worktree has its own independent `obj/` and `bin/` directories. Build failures in a worktree are real failures — investigate them, do not dismiss as environment noise.

---

## Reporting bugs and requesting features

Open a [GitHub Issue](https://github.com/sytone/botnexus/issues/new) with:

- **Summary** — one sentence describing the problem or feature
- **Steps to reproduce** (for bugs) or **use case** (for features)
- **Expected vs actual behaviour** (for bugs)
- **Acceptance criteria** — when is this done?

Issues without acceptance criteria may be put on hold pending clarification.

---

## License

By contributing, you agree that your contributions are licensed under the same license as this repository.
