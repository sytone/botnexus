# Content-Addressed Validation Receipts

> **Note:** receipts are still live, but no git hook consumes them. #2841 removed the
> pre-commit hook; `scripts/repo/Invoke-PreCommitValidation.ps1` is now the only reader, and it
> is invoked explicitly rather than on commit. References to "the pre-commit hook" below
> mean that explicit invocation. The staged/local receipt below and the remote Azure v2 receipt
> are different formats; see [Remote Azure receipts](#remote-azure-receipts) for the current remote path.

**Purpose:** Explain how BotNexus lets repeat validation safely skip redundant
build/test work when the *exact content being committed* has already passed the current
required validation policy (issue #2143).

---

## The stage → commit → validate workflow

Commit the final candidate before the authoritative validation run. This gives the run a
stable commit ID and avoids invalidating its receipt by committing the same content afterward.

1. **Stage the intended changes** in the issue's isolated worktree.
2. **Commit on the development branch.** Confirm there are no staged, unstaged or untracked
   source changes left in the candidate. A local development commit is allowed before validation;
   it is not a success claim or permission to push.
3. **Run the required validation** against that committed candidate:

   ```powershell
   scripts/repo/Invoke-PreCommitValidation.ps1
   ```

   Code changes require the strict gate. Documentation-only changes use the documentation
   build described in `AGENTS.md`, not a remote test run.
4. **Before push, verify the same candidate.** A later strict-gate invocation can reuse its
   qualifying receipt without another build/test run. Do not create or amend a commit after
   final validation and assume the old receipt still applies.

If validation fails, repair the problem in another commit and validate that candidate. Keep
push, readiness and merge gates unchanged. Cheap compilation and permitted tests-first feedback
may still run during development; they do not replace final validation. Never use local test
execution on a live-gateway host as a shortcut.

A commit ID alone is not the reuse key. The remote receipt also binds actual source content,
base, mode and current runner qualification. Staged, unstaged or untracked source changes,
a changed HEAD or base, and changed validation inputs can invalidate it. The strict gate checks
those conditions; do not skip it based on a remembered successful run.

The local/staged receipt format described below remains supported by its existing helpers.
Its historical design does not change the required contributor order above.

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

## Remote Azure receipts

The default remote path uses a separate receipt at
`<worktree-git-dir>/botnexus-validation/azure-buildtest.json`, not the common-directory local
receipt described above. `BOTNEXUS_VALIDATION_RECEIPT` is not its selector. The implementation
is in `AzureBuildTestRecovery.psm1`; `Invoke-PreCommitValidation.ps1` is its explicit cache reader.

An Azure receipt uses outer `version: 2` and records:

- HEAD, base reference/commit, candidate tree and fingerprint, including the raw source digest.
  This covers the actual worktree candidate, not only staged files.
- Requested mode, run/execution identity, completion time and verified version 1 source proof.
- `runnerRelease`: exactly `releaseId`, `image`, `manifestSha256`, `runnerInputDigest`,
  `payloadVersion`, `evidenceVersion` and `mode`.
- `executionConfiguration: image-entrypoint-v1`.

Creation requires successful provider execution, the matching immutable image and execution
configuration, current external qualification, unchanged candidate, verified source proof and
complete numeric test evidence. Diagnostic runs never issue a receipt. The current gate remains
`scripts/repo/Invoke-PreCommitValidation.ps1`; remote certification defaults to `core`. The lower-level
`strict` mode is distinct and still needs its fresh Playwright artifact.

Cache reuse requires v2 evidence, matching fingerprint/HEAD/base/tree and the requested mode,
valid source proof and a fresh approval comparison selected by the trusted coordinator's
launch-environment `BOTNEXUS_RUNNER_RELEASE_MANIFEST`. This external profile sits outside the
repository under ordinary existing user-profile permissions. The cache trusts the previously
validated execution image in local evidence; it does **not** query the provider again. The
coordinator, local shell, scripts, Git metadata and receipts are trusted. Native peer tool
restrictions do not provide OS/shell isolation or operator-only ACL/nonreplacement guarantees. Neither receipt data nor a persisted manifest path is approval
authority. Legacy receipts cannot self-upgrade.

Changing or revoking approval blocks reuse. The launch binding hashes exact manifest bytes,
so even whitespace-only rotation invalidates old evidence. Missing capabilities, unknown
capabilities and changed execution inputs are real blockers, not permission to edit a receipt
or bypass source/completeness checks. A cache miss can invoke the normal remote path; its launch
must still pass qualification. See the [manifest schema, lifecycle and trust limits](runner-release-contract.md).

## Related Documentation

- **[running-tests.md](running-tests.md)** - impacted-test selection and firewall rules
- **[azure-build-test-runner.md](azure-build-test-runner.md)** - remote Azure validation

## Recover an interrupted Azure receipt wait

For contributors using remote validation, `Invoke-AzureBuildTest.ps1` now prints a
**Recovery context** path before waiting. `-NoWait` also returns it as `ContextPath`.
The v2 context is stored under the worktree's Git directory, not in the temporary
download directory. Keep that file and the candidate unchanged. Recovery also needs the current
operator-selected `BOTNEXUS_RUNNER_RELEASE_MANIFEST`; a context cannot select its own approval.

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

Recovery rereads current external qualification before download and again at final certification.
It requires the original provider's successful execution status, matching immutable execution
image, `image-entrypoint-v1` without command/args overrides, matching run/mode environment,
canonical `result.json`, verified source digest, and complete numeric test results. It recomputes HEAD, base commit, prospective tree and source fingerprint.
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
Older runs without a v2 qualified launch context cannot use this recovery path. Legacy contexts
cannot acquire approval after the fact. Changed manifest bytes, even whitespace-only edits,
invalidate the original binding; do not edit the context to make it match. Recovery
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
