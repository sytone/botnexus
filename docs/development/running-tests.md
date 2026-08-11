# Running Impacted Tests

**Purpose:** How `scripts/repo/test-impacted.ps1` selects and runs tests, and how the
Windows testhost firewall pre-authorization works.

---

## Overview

`scripts/repo/test-impacted.ps1` runs only the test projects affected by your
changes (via `dotnet-affected`), plus the `*.Architecture.Tests` and
`*.Scenarios.Tests` safety-net projects. Run it before every push:

```powershell
scripts/repo/test-impacted.ps1
```

See the script's comment-based help (`Get-Help scripts/repo/test-impacted.ps1 -Detailed`)
for parameters (`-From`, `-Configuration`, `-All`, `-NoBuild`, `-DryRun`).

---

## Freshness guards (#2785)

The gate consumes two **cached** inputs. Both used to be trusted without ever being
checked, so the gate could report a confident verdict about work it had not done.

### Stale test assemblies

`dotnet test --no-build` does **not** fail when the assembly on disk predates the source.
It silently runs whatever `.dll` is already there. Observed on 2026-08-03: a gate run
reported 564 tests / 3 failed from an assembly compiled 15 minutes before the commit under
validation, while a forced-clean run of the same commit reported 591 / 0 - 27 tests did not
exist in the executed assembly. The false-red cost two hours of triage; the same mechanism
produces a **false green** just as easily, which is the severe direction for a pre-push gate.

When `-NoBuild` is passed, `test-impacted.ps1` now compares every selected project's
compiled assembly against the HEAD commit timestamp and **refuses to run** if any is older,
naming the offending project and its timestamp. A project with no assembly at all is
`missing`, never `fresh` - absence fails closed.

### Stale base ref

`-From` defaulted to `origin/main` and nothing fetched it, so the impacted set was derived
from whatever that remote-tracking ref last pointed at in that checkout (observed: 7 commits
stale). `--from <tip>` is also a **two-dot** diff, so every commit that landed on the base
after the branch forked entered the change set - a true 10-file diff was reported as 26
files across projects the branch never touched. The dangerous direction is the opposite one:
a stale base can **omit** projects a change genuinely impacts, and those tests never run.

The base ref is now fetched before use and the impacted set is computed from
`git merge-base <base> HEAD`, which is by construction unaffected by later base commits. A
fetch failure (offline, no auth) is **not** fatal - the gate still runs - but it is reported
rather than silent.

### Configuration unification

`Invoke-LocalValidation.ps1` takes a single `-Configuration` (default `Debug`) and passes it
to the build step *and* every test step. Previously the build passed no `-c` while
`test-impacted.ps1` defaulted independently, so `Debug` and `Release` artifact sets could
diverge with only one of them refreshed.

The guards live in [`ValidationFreshness.psm1`](../../scripts/repo/ValidationFreshness.psm1),
covered by `ValidationFreshness.Tests.ps1` and mutation-checked by
`ValidationFreshness.Mutation.ps1` (each guard must redden its test **by name** when reverted).

---

## Windows testhost firewall pre-authorization

### The problem

The .NET test host (`testhost.exe`) opens a loopback socket to communicate with the
`dotnet test` runner. On Windows, the first time a given `testhost.exe` **path** runs,
Windows Defender Firewall shows an interactive *"Allow app through the firewall"* popup.

Because every git worktree lives at a different absolute path, e.g.

```
Q:\repos\botnexus-wt\<branch>\tests\gateway\BotNexus.Cli.Tests\bin\Debug\net10.0\testhost.exe
```

a **new popup appears for every worktree**. During unattended or agent-driven runs
this blocks the test run entirely. Worse, the popup is not self-limiting: answering it
creates a permanent ungrouped rule, so the rule set grows monotonically with every
worktree ever created. See [Reclaiming orphaned rules](#reclaiming-orphaned-rules-issue-2774).

### The fix

`test-impacted.ps1` calls
[`Ensure-TesthostFirewallRules.ps1`](../../scripts/repo/Ensure-TesthostFirewallRules.ps1)
just before running tests. That helper:

1. Derives the leased program set from each project's **build output** — every
   executable actually present in `<projectDir>/bin/<Configuration>/<tfw>/`, not just
   `testhost.exe`. This matters because fixtures such as `CliTestFixture` and
   `CrossProcessConfigWriteTests` spawn a child process, and the binary they launch
   (`BotNexus.Cli.exe`) sits in that same directory. Leasing only `testhost.exe` left
   the CLI unleased, so it prompted anyway (issue #2774). If the output directory does
   not exist yet — a fresh worktree before any build — the composed `testhost.exe` path
   is used as a floor so a lease still happens.
2. Checks which of those paths do **not** already have a firewall rule.
3. If any are missing, batches them into a **single self-elevated child process** and
   creates inbound + outbound allow-rules (grouped under `BotNexus-Testhost`).

The derivation lives in
[`FirewallLeaseProgram.ps1`](../../scripts/repo/FirewallLeaseProgram.ps1) and is
narrow by construction: only binaries directly inside the project's *own* output
directory are ever leased. Nested `runtimes/` probes, sibling projects, and anything
outside the repository are discarded.

### Behavior notes

- **At most one UAC prompt per run.** All missing rules are created in one elevated
  pass. When rules already exist (the common case after the first run in a worktree),
  there is **no** prompt at all.
- **Best-effort and non-fatal.** The helper is Windows-only and swallows all failures
  with a warning. A declined UAC prompt, group-policy block, or non-Windows host never
  fails the test run — at worst the original testhost popup reappears once.
- **Idempotent.** Re-running is a clean no-op once rules are present.

### Reclaiming orphaned rules (issue #2774)

When someone answers an interactive firewall prompt, Windows creates rules named
`TCP Query User{<GUID>}<program path>` with **no group at all**. A prune that iterates
`Get-NetFirewallRule -Group 'BotNexus-Testhost'` is therefore a structural no-op against
exactly the rule class the prompts generate — which is why 68 such rules accumulated on
one machine, most pointing at long-deleted worktrees, four of them `Block`.

[`Invoke-FirewallRuleReclaim.ps1`](../../scripts/repo/Invoke-FirewallRuleReclaim.ps1)
is the one-shot reclaim. It reports by default and changes nothing without `-Apply`:

```powershell
# Report only — safe to run anywhere, changes nothing.
pwsh -NoProfile -File scripts/repo/Invoke-FirewallRuleReclaim.ps1

# Remove the orphans. Run from an ELEVATED shell.
pwsh -NoProfile -File scripts/repo/Invoke-FirewallRuleReclaim.ps1 -Apply
```

**Narrowness contract.** A rule is removed only when *both* hold: its program path is
under this repository or its worktree container, **and** that program no longer exists
on disk. A `BotNexus-Testhost` lease rule is additionally spared while its owning
process is still alive. Nothing outside those roots is ever touched — an over-broad
firewall prune on a developer machine is worse than the bug it fixes. The guarantee is
pinned by [`FirewallRulePrune.Tests.ps1`](../../scripts/repo/FirewallRulePrune.Tests.ps1),
whose negative assertions fail if the selection ever widens.

### Managing the rules manually

List the rules created by the helper:

```powershell
Get-NetFirewallRule -Group 'BotNexus-Testhost'
```

List the ungrouped rules that interactive prompts created:

```powershell
Get-NetFirewallRule -DisplayName 'testhost' | Where-Object { -not $_.Group }
```

Remove the grouped lease rules (e.g. after deleting old worktrees):

```powershell
Get-NetFirewallRule -Group 'BotNexus-Testhost' | Remove-NetFirewallRule
```

> Prefer `Invoke-FirewallRuleReclaim.ps1` over a hand-rolled `Remove-NetFirewallRule`
> pipeline: it will not remove a rule whose binary still exists or whose lease is held
> by a running test process.

You can also pre-create rules for a specific set of projects without running tests:

```powershell
scripts/repo/Ensure-TesthostFirewallRules.ps1 -ProjectPath (Get-ChildItem -Recurse -Filter *.Tests.csproj).FullName
```

---

## Related Documentation

- **[validation-receipts.md](validation-receipts.md)** - content-addressed receipt reuse in pre-commit
- **[git-worktree-config-hardening.md](git-worktree-config-hardening.md)** - worktree config hygiene
- **[../getting-started-dev.md](../getting-started-dev.md)** — building and debugging BotNexus
