# Comment Moderation

Who may leave a durable comment on an issue or pull request in this repository, and
how that is enforced.

## Why this exists

Every trust decision in this repository is **author-keyed**:

| Control | Trusts |
|---|---|
| `/allow-security-sensitive-change <sha>` | commenters with `admin`/`maintain`/`write` |
| `/allow-pr-convention-exception` | same |
| Agent-side trust filter (`Get-TrustedGitHubContent.ps1`) | `sytone` and `agent-farnsworth[bot]`, exact match |

None of those can be spoofed — an attacker cannot forge a comment author. But a
third-party comment does not need to be honoured as a command to do damage. It
**persists in the thread** and is read as context by every human and every agent
that later loads that issue or PR. The autonomous maintenance loop reads issue
bodies, PR bodies and comments; an unfiltered third-party comment sitting in a
thread is a prompt-injection surface.

Removing the payload at write time is strictly better than relying on every
downstream reader to filter it correctly, forever.

The trigger was concrete: on 2026-08-16 an unrelated account posted a promotional
AI-generated comment on an open issue. It was the only non-bot comment in the
last 100 issue comments on the repository.

## The control has two halves — and one of them expires

**GitHub Actions cannot prevent a comment.** The comment is created first; only
then can a workflow fire. Prevention is only available as a repository setting.
So the control is deliberately two-part:

### Half 1 — Interaction limits (prevention, expires)

> **Manual maintainer action. Not automatable from this repo** — the REST
> interaction-limits endpoint is not accessible to the `agent-farnsworth` GitHub
> App token (`HTTP 403 Resource not accessible by integration`).

**Settings → Moderation options → Interaction limits → _Limit to repository
collaborators_.**

This genuinely blocks a non-collaborator from commenting at all. GitHub caps the
duration at **6 months**, after which it silently lapses and the repository is
open again.

| Field | Value |
|---|---|
| Setting | Limit to repository collaborators |
| Enabled | _record the date when you enable it_ |
| **Expires / renew by** | _enable date + 6 months_ |

Renewing is a calendar item, not a code change. If you find this table blank, the
limit has probably never been set — set it and fill the table in.

### Half 2 — Comment moderation workflow (durable, code-owned)

`.github/workflows/comment-moderation.yml` fires on `issue_comment`,
`pull_request_review_comment` and `pull_request_review`. If the author is not on
the allow-list, the comment is **minimized as spam** within seconds.

This half never expires, is reviewable in the diff, and catches anything the
interaction limit misses (including the window after it lapses, and any
collaborator-but-not-allow-listed account).

## The allow-list

Defined in `.github/scripts/comment-moderation.mjs`:

- `sytone` — the maintainer
- `agent-farnsworth[bot]` — the platform agent

Matching is **exact after case folding**, and nothing more. GitHub logins are
case-insensitive for identity, so `SYTONE` is the same account and is admitted.
Anything beyond folding — trimming a `[bot]` suffix, stripping punctuation, prefix
or substring matching — would reopen the spoofing hole the list exists to close.
These are all rejected, and each has a test:

`sytone[bot]`, `sytone-attacker`, `Sytone-Fake`, `sytonee`, `xsytone`,
`agent-farnsworth`, `agent-farnsworth-evil`, `not-agent-farnsworth[bot]`

## Minimize, not delete

The default action is `minimize` (GraphQL `minimizeComment`, classifier `SPAM`),
not deletion, because **it is reversible and leaves an audit trail**. A false
positive can be un-minimized by a maintainer; a deleted comment is unrecoverable
and a false positive becomes invisible to everyone, including the maintainer.

Deletion is available by changing the `MODERATION_ACTION` constant, but that is a
deliberate decision and a security-boundary change — see below.

## Safety model

- **No untrusted code is ever executed.** The workflow checks out only the
  repository default branch, sparse, to run its own trusted script. There is no
  checkout of a PR head or a fork anywhere in the path.
- **The comment body is never read.** Only the author login and the comment node
  id are taken, both from the trusted webhook payload. The body is not parsed,
  matched, or echoed into a log or job summary — a test asserts the extracted
  target contains no `body` field.
- **Unparseable payloads fail safe.** An event shape the script does not
  recognise results in no action and no job failure. Moderating a shape we cannot
  parse risks hiding the wrong thing, which is worse than leaving a comment for a
  human.
- **Moderation failures fail loud.** If the mutation throws, the job fails. A
  silently-failing guard is worse than no guard, because the repository looks
  protected when it is not.

## Changing this control

Both `.github/scripts/comment-moderation.mjs` and
`.github/workflows/comment-moderation.yml` are listed in `SENSITIVE_EXACT` in the
[security-sensitive file guard](security-sensitive-file-guard.md) and in
`CODEOWNERS`. Adding a login to the allow-list, or switching the default action to
`delete`, therefore requires a head-SHA-bound `/allow-security-sensitive-change`
ack from the maintainer on the PR.

## Tests

```shell
node --test .github/scripts/comment-moderation.test.mjs
```

22 cases covering each allow-listed login, every near-miss spoof above, all three
event shapes, missing/partial payloads, the moderate-vs-skip decision, and the
loud-failure path. The workflow runs this suite as a step, in the same shape as
the PR conventions guard.
