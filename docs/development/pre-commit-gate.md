# Pre-Commit Gate Scope, Timeouts and Lock Behaviour

> **HISTORICAL - the pre-commit hook described here no longer exists.** #2841 removed it,
> and `scripts/repo/install-hooks.ps1` now activates only the `pre-push` `core.bare` guard
> (#1602). Commit-time local validation is banned because a local test host boots gateway
> processes that outlive their parent (#2158). Test execution is remote; see
> [Azure build/test runner](azure-build-test-runner.md). This page is retained to explain
> the timeout and lock reasoning that shaped the current design.

**Purpose:** What the pre-commit hook ran, how long it was allowed to take, and
what it did when another validation was already running.

Related issue: [#2331](https://github.com/Sytone/botnexus/issues/2331).

---

## Two gates, deliberately different

BotNexus has two local gates. Conflating them is what made the old hook unusable.

| | Pre-commit hook (advisory) | Pre-push / authoritative gate |
|---|---|---|
| Command | `Validate-PreCommit.ps1 -Hook` | `Validate-PreCommit.ps1` |
| Build | impacted projects only | one full solution build |
| Tests | impacted projects only | impacted + architecture/scenario safety nets |
| Playwright | no | yes (strict mode) |
| Emits a receipt | no | yes |
| Lock contention | waits, then **skips** | waits (unbounded), never skips |
| Timeouts | bounded per step | unbounded |

The hook is a fast smoke check on every commit. It does **not** certify a candidate for
push and never writes a validation receipt. Full validation belongs in the pre-push gate
and in CI, which is where it is authoritative.

## Why the hook was rewritten

Before #2331 the hook ran a full-solution build plus the whole strict gate on every
commit, and it *threw* when another worktree's validation held the global lock. In
practice that meant the hook regularly overran its budget or failed on contention, so
`--no-verify` became the normal path. A gate that is habitually bypassed protects nothing
while still costing time on every commit.

## Budgets

Defaults are set in `scripts/repo/Invoke-LocalValidation.ps1` and apply to hook mode only:

| Step | Budget | Rationale |
|---|---|---|
| Global lock wait | 120s | Long enough for a short concurrent run to finish; past that, skipping is better than blocking a commit. |
| Impacted build | 300s | An impacted-scope build on a warm developer machine. |
| Impacted tests | 600s | Impacted test projects, including a cold testhost start. |

Override per run:

```powershell
scripts/repo/Invoke-LocalValidation.ps1 -Mode hook -TestTimeoutSeconds 900
```

The authoritative gate (`-Mode strict`) is intentionally **unbounded**: truncating the
gate that decides whether code may be pushed would be worse than waiting for it.

## Diagnosable timeouts

Every step runs through `Invoke-BotNexusValidationStep`, which names the step in all
outcomes:

```
[validation] step 'impacted tests (hook scope)' EXCEEDED its 600s timeout and was terminated.
Validation FAILED: step 'impacted tests (hook scope)' exceeded its 600s budget.
```

On timeout the whole child process tree is killed - `dotnet` leaves MSBuild node and
`testhost` children behind that would otherwise keep build outputs locked and cause the
*next* run to fail for an unrelated reason. The exit code is `124`, matching POSIX
`timeout(1)`, so callers can distinguish an overrun from a genuine test failure.

## Lock contention

All BotNexus validation is serialized host-wide, because separate worktrees still compete
for the same CPU, Defender scanning, NuGet cache and tool processes. Acquisition now
*waits* a bounded time via `Get-BotNexusValidationLock` and reports the outcome as data:

- **Hook mode:** on non-acquisition, prints a clear message and exits `0`. Commit proceeds;
  the pre-push gate and CI still run everything.
- **Authoritative mode:** waits effectively indefinitely. It is never skipped.

## The transient `.editorconfig could not be found` error

Two separate contributors were found:

1. **The repository has no `.editorconfig` at all.** Any tool or analyzer configuration
   that expects one resolves it by walking up from the *current working directory*, so
   outside the repo root it can find an unrelated file - or none - non-deterministically.
2. **The old runner inherited its working directory.** `dotnet build` and `dotnet test`
   were invoked with whatever directory the hook process happened to start in. Under
   parallel worktree builds that could be a *different* worktree.

Cause 2 is fixed: `Invoke-BotNexusValidationStep` requires an explicit
`-WorkingDirectory`, throws if it does not exist, and every step is launched with the
repository root of the worktree being validated. There is no directory inheritance left
in the validation path. Cause 1 is noted rather than fixed here - adding a repository
`.editorconfig` changes analyzer behaviour across the whole solution and belongs in its
own change.

---

## Related Documentation

- **[running-tests.md](running-tests.md)** - how impacted-test selection works
- **[validation-receipts.md](validation-receipts.md)** - content-addressed receipt reuse
- **[git-worktree-config-hardening.md](git-worktree-config-hardening.md)** - worktree config hygiene
