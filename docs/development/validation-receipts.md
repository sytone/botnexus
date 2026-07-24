# Content-Addressed Validation Receipts

**Purpose:** Explain how BotNexus lets the pre-commit hook safely skip redundant
build/test work when the *exact content being committed* has already passed the current
required validation policy (issue #2143).

---

## The stage → validate → commit workflow

The pre-commit hook is a safety catch, not a mandatory duplicate test runner. To benefit
from receipt reuse, follow this order:

1. **Stage the intended snapshot.** `git add` exactly what you plan to commit.
2. **Run canonical validation** against that staged snapshot:

   ```powershell
   scripts/repo/Validate-PreCommit.ps1
   ```

   On success this emits a *validation receipt* describing the exact staged content.
3. **Commit.** The pre-commit hook recomputes the staged identity and, if it matches a
   valid receipt, prints a cache-hit message and allows the commit without rerunning
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
