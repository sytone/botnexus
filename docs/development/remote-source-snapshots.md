# Exact-source remote validation snapshots

## Contract (version 1)

Remote validation transports the current workspace, not just HEAD or an additive overlay.
The candidate consists of existing files named by the current Git index plus nonignored
untracked files. Staged and unstaged deletions are absent. Staged additions (including
forced ignored additions), modifications, renames, spaces, Unicode, leading dashes and
brackets are supported. Empty directories are not source inputs.

`SourceSnapshot.psm1` enumerates NUL-delimited Git paths, reads raw bytes, and records an
ordinally sorted manifest of paths, lengths and SHA-256 hashes. The digest hashes a
versioned domain separator and the complete manifest. This is **not** a Git-normalized
blob/tree hash: CRLF and LF are different source inputs even if Git normalizes them.

Capture writes each selected file's bytes once into a private staging directory. Packaging
uses that copy, never reopens live source for archive content, and checks the candidate
fingerprint before capture, after packaging/before upload, and before issuing a receipt.
A changed candidate fails closed. These are consistency checks, not an OS-wide transaction:
callers must keep the workspace stable; simultaneous malicious mutation is not supported.

The outer `source.tar.gz` has exactly four regular entries:

- `repository.bundle`: history and refs for Git-based tooling;
- `workspace.zip`: raw candidate files;
- `source-manifest.json`: version, digest and complete file list;
- `SourceSnapshot.psm1`: the sender's verifier, outside the candidate workspace.

The updated runner validates the outer entry allow-list, clones without checkout,
retains `.git`, clears other working contents and restores ZIP entries explicitly.
No generic workspace archive extractor is used. It verifies the complete file set and
raw bytes **before restore/build** and emits `result.sourceSnapshot` containing
`version: 1`, `digest`, `runId`, and `verified: true`. It does not create a synthetic
pre-validation commit. The source proof describes the build input, not files generated
later by restore/build/tests. The authenticated sender and deployed runner remain trusted;
the digest is content identity, not a signature or protection against a malicious runner.

Absolute paths, traversal, backslashes, control characters, `.git` components, Windows
reserved names, trailing dots/spaces, case collisions, symlinks, reparse traversal and
submodules are rejected. Version 1 transports regular-file bytes and names, not ACLs,
Unix executable permissions, timestamps, hardlink identity or empty directories. A future
mode/metadata contract needs its own version; do not infer those properties from this proof.

## Fingerprints and receipts

Fingerprint calculation starts from a copy of the **current** index, not `read-tree HEAD`.
Unmerged index stages are rejected; an already-resolved but uncommitted merge is accepted.
The caller's index, HEAD and MERGE_HEAD are not changed. An inherited `GIT_INDEX_FILE` is
restored exactly, including removing the environment entry when originally absent.

The fingerprint includes HEAD, base commit, normalized candidate tree, and raw manifest
digest under `botnexus-validation-exact-source-v1`. This invalidates old fingerprints.
`Validate-PreCommit.ps1` need not read the proof field to reject legacy receipts: its
existing fingerprint comparison cannot match the old hashing domain.

The sender requires matching source version/digest/run ID, successful execution and test
exit, the requested mode, a complete nonempty test result, consistent nonnegative integer
counters, no failures/fixture failures, and existing core/full count floors. Deliberate
skips retain the runner's existing policy and must be accounted for. Strict mode still
requires the Playwright artifact. Every mode emits the runner test contract; missing
results never qualify. Receipts retain their reader-compatible outer version and include
the verified source proof. Failure occurs before receipt creation and before success output.

## Operator rollout and evidence boundary

An operator must build and deploy the updated runner image through the normal infrastructure
workflow, then use the matching sender/fingerprint implementation. This change does not
modify the Dockerfile: the new entrypoint deliberately imports the verifier from the payload.
An old image does not understand this payload. Missing proof yields an explicit operator
compatibility/deployment error, not a fallback or automatic bootstrap. This stage neither
changes Azure resources nor deploys/restarts any running service.

A core run launched through the **canonical main-checkout wrapper does not exercise these
unmerged sender/entrypoint changes**. It may validate candidate .NET inputs but is not
end-to-end evidence for this transport implementation. Deployment plus an explicitly
matched sender/runner run remains required before claiming remote transport validation.
Linux execution, deployed image compatibility, actual Azure transfer/results, and impacted
selection behavior without a synthetic commit remain rollout verification items.

## Offline regression suite

Run from the task checkout in PowerShell 7 (Pester 6.0.1):

```powershell
Import-Module Pester -RequiredVersion 6.0.1
Invoke-Pester -Path ./scripts/repo/SourceSnapshot.Tests.ps1 -Output Detailed
```

The suite uses isolated offline Git fixtures with synthetic identities and executes the
production capture, restore, and receipt-guard/writer blocks. It retains the staged-deletion
regression and `keep.md` content assertion. It tests raw-byte identity, safe literal names,
resolved/unresolved merges, caller state restoration, unsafe archive paths/links, missing,
tampered and extra files, legacy fingerprints and invalid proof/test contracts. Fixture
credentials/configuration are isolated and the inherited environment is restored exactly;
task-created fixture directories are removed afterward. No .NET tests, gateway processes,
Azure calls or deployment are part of this suite.

## Readiness regression evidence

The Linux-only case-sensitive fixtures intentionally fail on a non-Linux host rather than
skip acceptance coverage. Run the complete suite in a clean Linux filesystem, not a
case-insensitive host bind mount. A disposable offline container with read-only source
input and container-private fixture copies avoids both host Git credentials and installed
PowerShell module state. No .NET test hosts are needed for these transport fixtures.

The readiness repair retains all existing assertions and adds file-to-directory replacement,
case-only unstaged rename, a genuine two-existing-file case-collision negative, and a
matching archive/manifest link-attribute negative with a regular-file positive control.
Only surviving regular files reserve portable names; deleted index entries cannot collide
with a valid renamed candidate. Link, submodule and unresolved-index guards remain intact.

Failure-result diagnostics still run for failed executions. Source proof and stable-candidate
checks guard every prospective successful receipt, including legacy results missing proof.
The existing verdict fixture now imports its actual artifact dependency in its isolated
runspace. The receipt-selection fixture pins ZIP/NUL-safe reconstruction instead of the
retired tar list-file representation while retaining its nine receipt-selection scenarios.

Measured in clean Linux PowerShell7.6.2/Pester6.0.1: initial49passed/5failed (two snapshot
edge cases plus three inherited verdict dependency failures), repaired54passed/0failed/
0skipped. Each isolated link-guard, collision-order and directory-rejection mutation gives
53passed/1failed at its specific new test. Receipt-selection scenarios9passed/0failed.
These are offline fixture results, not a claim of deployed runner or FULL validation.
