# Content-Addressed Validation Receipts

> **Note:** receipts are still live, but no git hook consumes them. #2841 removed the
> pre-commit hook; `scripts/repo/Validate-PreCommit.ps1` is now the only reader, and it
> is invoked explicitly rather than on commit. References to "the pre-commit hook" below
> mean that explicit invocation.

**Purpose:** Explain how BotNexus lets repeat validation safely skip redundant
build/test work when the *exact content being committed* has already passed the current
required validation policy (issue #2143).

---

## The stage → validate → commit workflow

Explicit validation is a safety catch, not a mandatory duplicate test runner. To benefit
from receipt reuse, follow this order:

1. **Stage the intended snapshot.** `git add` exactly what you plan to commit.
2. **Run canonical validation** against that staged snapshot:

   ```powershell
   scripts/repo/Validate-PreCommit.ps1
   ```

   On success this emits a *validation receipt* describing the exact staged content.
3. **Commit.** A later `Validate-PreCommit.ps1` run recomputes the staged identity and,
   if it matches a valid receipt, prints a cache-hit message and returns without rerunning
   build/tests.

If you change any staged file, the validation policy, or the toolchain between steps 2
and 3, the receipt no longer matches and the hook falls back to full validation.

---

## What a receipt certifies

A receipt is a small JSON document (see `scripts/repo/ValidationReceipt.psm1`) containing
at least:

- schema version and repository identity
- the **prospective Git tree hash** (`git write-tree` over the staged snapshot)
- base commit/reference used for impact analysis
- **validation policy hash** (the validation scripts + mandatory safety-net patterns)
- **toolchain hash** (`global.json` / SDK pin, tool manifest, package pins)
- configuration and target framework, .NET SDK identity
- validation scope (`impacted`, `full`, `strict`, `playwright`) and the projects run
- build result, test result, creation time, and optional expiry

The receipt is written **atomically** (temp file + move) only after every required
command succeeds. A failed or interrupted run removes any prior receipt first, so it can
never leave stale-but-matching evidence behind.

---

## The exact-content rule (fail closed)

A receipt is reusable **only** when its prospective tree hash equals the current staged
tree hash **and** every identity input still matches. The hook never reuses evidence
based on branch name, HEAD alone, timestamps, output directories, or a dirty working-tree
hash. Any of the following causes fallback validation:

- missing, malformed, or partial receipt
- schema-version mismatch
- expired receipt
- non-success build or test result, or a scope that is not required
- a different staged tree, base commit, policy hash, toolchain hash, configuration, or
  target framework

Because `dotnet test` reads the working tree rather than the index, a receipt is only
emitted when the working tree already matches the staged snapshot for tracked files (or
when the producer explicitly validated a materialized snapshot). This prevents certifying
staged content using binaries built from different unstaged content.

---

## Portable storage (containers / CI)

By default the receipt lives under the Git **common directory**
(`<git-common-dir>/botnexus-validation/receipt.json`), so it is shared correctly across
linked worktrees and is never a tracked workspace file.

For container or CI artifact exchange, set an explicit path so a mounted host path or
copied container artifact can be consumed safely:

```powershell
$env:BOTNEXUS_VALIDATION_RECEIPT = '/mnt/artifacts/receipt.json'
```

The host hook accepts a receipt produced in a container through that path only when every
identity field matches.

---

## Related Documentation

- **[running-tests.md](running-tests.md)** - impacted-test selection and firewall rules
- **[azure-build-test-runner.md](azure-build-test-runner.md)** - remote Azure validation

## Recover an interrupted Azure receipt wait

For contributors using remote validation, `Invoke-AzureBuildTest.ps1` now prints a
**Recovery context** path before waiting. `-NoWait` also returns it as `ContextPath`.
The context is stored under the worktree's Git directory, not in the temporary
download directory. Keep that file and the candidate unchanged.

If the client stops waiting, do not start a second validation run. With PowerShell 7,
Azure CLI, and access to the original job and artifact storage, run this from the
repository root. Replace the two variables with the original context and worktree:

```powershell
scripts/repo/Resume-AzureBuildTest.ps1 -ContextPath $contextPath -WorktreePath $worktree
```

This queries the **existing execution** and downloads its original artifact prefix.
It does not upload source, start another execution, or delete remote artifacts.
A running or failed execution is rejected. Try recovery again only after you have
confirmed that the original execution succeeded; recovery does not wait or poll.

Recovery requires the provider's successful execution status and matching run/mode
environment, canonical `result.json`, verified source digest, and complete numeric test
results. It recomputes HEAD, base commit, prospective tree and source fingerprint.
Changed candidates, missing or malformed evidence, incomplete tests and fixture failures
cannot produce a receipt. The normal strict gate can reuse the resulting receipt.

Identical recovery is safe to repeat and concurrent identical writers converge.
Before execution starts, the launcher persists the current receipt's byte hash (or
explicit absence) with the start fingerprint. Finalization takes a bounded exclusive
worktree lock and replaces the singleton receipt only if that predecessor still matches.
The previous receipt is archived by byte hash in the Git validation directory. Normal
subsequent runs and recovery can therefore replace the expected old receipt without
manual deletion. A distinct intervening writer wins: a late conflicting run fails closed
and cannot overwrite its receipt. Do not delete or edit receipts to bypass that conflict.
Strict mode checks `playwright.log` in the fresh canonical download before merging local
artifacts, so a stale local copy cannot certify a run.
Older runs without the persisted start context cannot use this recovery path. Recovery
also requires that the original artifacts still exist remotely; normal successful runs
may already have cleaned them up. Use `-KeepRemoteArtifacts` when starting a run whose
remote evidence needs to remain available after success.

### Run the isolated recovery regressions

CORE runs the .NET suite; it does **not** automatically run these PowerShell fixtures.
From the exact candidate checkout, run these explicit commands separately in the isolated
Linux validation environment and retain their output alongside CORE evidence:

```powershell
pwsh -NoProfile -File scripts/repo/Test-AzureBuildTestRecovery.ps1
pwsh -NoProfile -File scripts/repo/Test-AzureBuildTestFinalization.ps1
Import-Module Pester -RequiredVersion 5.7.1
Invoke-Pester -Path ./scripts/repo/Test-SourceSnapshot.ps1 -Output Detailed
```

These fixtures use synthetic Git repositories and provider/runner documents, with Azure
commands fenced offline. They do not start Azure executions, gateways or product test
hosts. The source-snapshot fixture includes two case-sensitive filesystem assertions that
require Linux; a Windows result cannot substitute for them. The finalization fixture
also starts two bounded synthetic PowerShell children to test distinct concurrent writers.
