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
this blocks the test run entirely.

### The fix

`test-impacted.ps1` calls
[`Ensure-TesthostFirewallRules.ps1`](../../scripts/repo/Ensure-TesthostFirewallRules.ps1)
just before running tests. That helper:

1. Derives the deterministic `testhost.exe` path for each selected test project
   (`<projectDir>/bin/<Configuration>/net10.0/testhost.exe`). The firewall rule does
   not require the binary to exist yet, so this works even on a fresh worktree.
2. Checks which of those paths do **not** already have a firewall rule.
3. If any are missing, batches them into a **single self-elevated child process** and
   creates inbound + outbound allow-rules (grouped under `BotNexus-Testhost`).

### Behavior notes

- **At most one UAC prompt per run.** All missing rules are created in one elevated
  pass. When rules already exist (the common case after the first run in a worktree),
  there is **no** prompt at all.
- **Best-effort and non-fatal.** The helper is Windows-only and swallows all failures
  with a warning. A declined UAC prompt, group-policy block, or non-Windows host never
  fails the test run — at worst the original testhost popup reappears once.
- **Idempotent.** Re-running is a clean no-op once rules are present.

### Managing the rules manually

List the rules created by the helper:

```powershell
Get-NetFirewallRule -Group 'BotNexus-Testhost'
```

Remove them all (e.g. after deleting old worktrees):

```powershell
Get-NetFirewallRule -Group 'BotNexus-Testhost' | Remove-NetFirewallRule
```

You can also pre-create rules for a specific set of projects without running tests:

```powershell
scripts/repo/Ensure-TesthostFirewallRules.ps1 -ProjectPath (Get-ChildItem -Recurse -Filter *.Tests.csproj).FullName
```

---

## Related Documentation

- **[validation-receipts.md](validation-receipts.md)** - content-addressed receipt reuse in pre-commit
- **[git-worktree-config-hardening.md](git-worktree-config-hardening.md)** - worktree config hygiene
- **[../getting-started-dev.md](../getting-started-dev.md)** — building and debugging BotNexus
