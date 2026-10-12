# Merged pull-request cleanup

These repository-owned scripts provide a reviewed source for the merged-PR cleanup portion of the installed PR execution skill. They do not authorize a merge or automatically replace installed skill files.

After a pull request has merged, invoke `scripts/Complete-BotNexusPullRequest.ps1` with `-PullRequestNumber`, `-Issue`, `-Worktree`, and `-RepoRoot`. The repository root is explicit; task worktrees must be registered beneath the sibling `<repository-name>-wt` directory. Completion verifies the merged PR, exact branch and head, clean worktree, merge commit on the target branch, and full versus partial issue disposition before cleanup. `-WhatIf` previews completion without closing the issue or removing its worktree.

Completion forwards the verified path to `scripts/Remove-BotNexusIssueWorktree.ps1 -WorktreePath`. Direct cleanup also requires `-Issue`, `-PullRequestNumber`, and `-RepoRoot`. Without an explicit path, registration metadata must identify exactly one branch/head match. Unrelated checkouts are never opened for branch/head probes. The chosen target is checked again, and failed Git commands or invalid identity output refuse removal. A failed status query is never evidence of a clean checkout. Paths containing links are refused.

Deletion remains delegated to the repository's existing `scripts/repo/Remove-Worktree.ps1` helper, including its bounded lock handling and branch-deletion ordering. No unrelated registration is repaired or removed by this selector.

Completion uses a sibling `Use-FarnsworthBot.ps1` when installed together. For a repository invocation, it falls back to the installed skill beneath the current user's home. `BOTNEXUS_SKILL_DIRECTORY` may explicitly name that skill's **scripts directory**. This resolves the existing authentication helper, not a different account or credential.

Run the isolated fake-tool fixtures with:

```powershell
pwsh -NoProfile -File skills/botnexus-pr-execution/scripts/Test-MergedWorktreeCleanup.ps1
```

`MergedWorktreeCleanupArchitectureTests` runs those same fixtures in the remote CORE suite. They exercise production scripts without real GitHub writes, credentials, gateway processes, or worktree deletion.
