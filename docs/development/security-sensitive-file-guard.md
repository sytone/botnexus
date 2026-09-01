# Security-Sensitive File Guard

A small CI guard blocks pull requests that modify **security-sensitive boundary
files** unless an authorized maintainer explicitly approves the change.

## Why

A handful of files govern the repository's security posture. A PR that quietly
weakens them is a real supply-chain / accidental-secret-commit risk, and there
was previously no gate on it. The most important is `.gitignore`: it controls
whether local secret files (`.env`, key/cert files, `secrets.json`) are ignored
**before** they can be accidentally committed. Un-ignoring one of those, or
weakening a CI workflow or the guard itself, should require a deliberate ack.

## What is guarded

The guard's sensitive list lives in
`.github/scripts/security-sensitive-guard.mjs`:

| Path | Why |
| --- | --- |
| `.gitignore` | Controls which secret/`.env` files are ignored |
| `.gitattributes` | Controls line-ending / filter behaviour |
| `.github/workflows/**` | The CI itself (build, security scans, releases) |
| `.github/CODEOWNERS` | Who must review sensitive paths |
| `.github/scripts/security-sensitive-guard.mjs` | The guard's own logic |
| `.github/scripts/comment-moderation.mjs` | Gates who may leave durable text on an issue or PR |

(`.github/workflows/security-sensitive-guard.yml` and
`.github/workflows/comment-moderation.yml` are listed explicitly in the script's
`SENSITIVE_EXACT` as well, so they stay guarded even if the
`.github/workflows/` prefix rule is ever narrowed.)

### CODEOWNERS declares ownership; it does not currently enforce it

The same paths are routed to the maintainer in `.github/CODEOWNERS`. That file
records **intended** ownership. GitHub only turns it into a merge requirement
when a branch protection rule or repository ruleset asks it to -- and no such
rule is enabled on this repository today. The single ruleset on `main`
(`Default Rules`) is `enforcement: disabled` and carries only `deletion`,
`non_fast_forward` and `copilot_code_review`; it has no `pull_request` rule, so
nothing requires a review or a required status check.

**So the operative gate is the head-SHA-bound
`/allow-security-sensitive-change <sha>` ack described below, and only that.** A
PR touching a guarded file can be merged without a human review as long as the
ack has been posted. Treat the ack as the control, not as a second layer on top
of one.

Enabling the out-of-repo enforcement is tracked in issue
[#3177](https://github.com/Sytone/botnexus/issues/3177). The same gap on the
other side -- a red check that cannot block the merge button -- is written up in
[`stale-base-merges.md`](stale-base-merges.md) under "What is still not covered
in-repo".

## How approval works

When a PR touches any guarded file, the **Security: Sensitive File Guard** check
fails with instructions. An authorized maintainer (repository `admin`,
`maintain`, or `write` permission) unblocks it by commenting:

```
/allow-security-sensitive-change <head-sha>
```

where `<head-sha>` is the PR's current head commit SHA (a 7-40 character hex
prefix is accepted). Posting the comment re-triggers the check, which passes.

The approval is **bound to that head SHA**. Pushing a new commit changes the head
SHA and invalidates the prior approval, so a maintainer cannot approve a PR and
then have an attacker sneak in a later commit. A fresh ack is required after each
push to a guarded file.

## Safety model

The workflow runs on `pull_request_target` (so it has repository context even
for fork PRs), but it is written to be safe against PR-head tampering:

- It checks out **only the trusted base copy** of the guard script
  (`ref: base.sha`, `persist-credentials: false`). It never executes any code
  from the PR head, so an attacker cannot rewrite the guard inside their own PR.
- The set of changed files is read from the GitHub API (`pulls.listFiles`), not
  from a working tree, so no PR-head content is sourced.
- Approval is honored only from users whose **current** repository permission is
  `admin`, `maintain`, or `write`, and only when bound to the current head SHA.

## Tests

The guard's pure logic (sensitive-path matching, approval parsing, SHA binding,
and the block/approve decision including the permission gate) is unit-tested in
`.github/scripts/security-sensitive-guard.test.mjs`:

```
node --test .github/scripts/security-sensitive-guard.test.mjs
```
