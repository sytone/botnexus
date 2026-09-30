# PowerShell child startup isolation in docs lint tests

`DocsLintScriptTests` launches a fresh PowerShell child for each lint invocation.
Each launch owns a temporary cache directory until the child exits. Overrides are
applied only to `ProcessStartInfo.Environment` through the existing
`ProcessEnvironment.Merge` seam; the test host's environment is not changed.

| Platform | Child startup boundary | Remaining limitation |
| --- | --- | --- |
| Unix | `XDG_CACHE_HOME` points to the owned directory, `PSModuleAnalysisCachePath` selects an owned module-cache file, and `DOTNET_MultiCoreJitMinNumCpus` disables the optional startup profile | The redundant disable prevents profile reads and writes; it does not establish the cause of a historical crash |
| Windows | `PSModuleAnalysisCachePath` selects an owned module-cache file and `DOTNET_MultiCoreJitMinNumCpus` disables the optional startup profile | PowerShell still resolves its cache directory through `Environment.SpecialFolder.LocalApplicationData`; the docs-lint child does not consume that startup profile |

## Why `-NoProfile` is insufficient

In the inspected [PowerShell ConsoleHost source](https://github.com/PowerShell/PowerShell/blob/b9ff1da7/src/Microsoft.PowerShell.ConsoleHost/host/msh/ConsoleHost.cs),
startup calls `ProfileOptimization.SetProfileRoot(Platform.CacheDirectory)` and
`StartProfile("StartupProfileData-NonInteractive")` independently of loading user
PowerShell profile scripts. The [platform selector](https://github.com/PowerShell/PowerShell/blob/b9ff1da7/src/System.Management.Automation/CoreCLR/CorePsPlatform.cs)
resolves Unix cache state beneath `XDG_CACHE_HOME/powershell`.
The [module analysis cache](https://github.com/PowerShell/PowerShell/blob/b9ff1da7/src/System.Management.Automation/engine/Modules/AnalysisCache.cs)
honors `PSModuleAnalysisCachePath` separately. Isolating only that file does not
isolate startup JIT profile data.

PowerShell has no supported per-process cache-root selector on Windows: it derives
the root through `Environment.SpecialFolder.LocalApplicationData`, and a child-only
`LOCALAPPDATA` override does not redirect that known folder. The docs-lint launcher
therefore sets the .NET runtime's `DOTNET_MultiCoreJitMinNumCpus` threshold to
`2147483647`. The runtime declines multicore-JIT profile use when the available CPU
count is below that threshold, so these short-lived lint children neither read nor
write `StartupProfileData-NonInteractive`. This disables an optional startup
optimization; it does not change script semantics, lint exit codes, or ordinary JIT
compilation.

Issue [#3968](https://github.com/Sytone/botnexus/issues/3968) records six child
startup aborts with exit 134 and a truncated assembly identity. Public reports
[PowerShell #26528](https://github.com/PowerShell/PowerShell/issues/26528) and
[dotnet/runtime #121977](https://github.com/dotnet/runtime/issues/121977) are
supporting investigation leads, **not proof of the failed container's root cause**.
Its cache bytes were not captured. This change is partial hardening, not a claim
that corruption was deterministically reproduced or Windows startup isolation solved.

## Regression contract

`PowerShellStartupIsolationTests` covers inherited-input replacement, unchanged
parent variables, independent owned paths, and cleanup restricted to owned state.
Two live children report the actual `Platform.CacheDirectory`, module-cache path,
PowerShell version, runtime description and executable into test output. Both
children reach the stdin readiness boundary before release; success never depends
on sleeping or elapsed-time assertions.

The protocol has a 60-second safety cancellation deadline. Cleanup has a separate
10-second deadline and attempts both owned children before awaiting completion.
Deadline expiry is failure, never a retry or an accepted startup result.

The named mutation **omit startup-profile disable** retains module-cache and Unix
cache-root isolation but removes the `DOTNET_MultiCoreJitMinNumCpus` override. It
must fail three assertions: inherited inputs are not replaced, two configured
launches do not carry the disable contract, and concurrent child probes report the
wrong numeric threshold. The existing **omit Unix startup-profile override** mutation
continues to fail its three Unix cache-root assertions. These deterministic oracles
are independent of stochastic corruption or module-cache behavior.

Every original docs lint assertion remains, including exact exit codes 0, 1 and 2.
Exit 134 remains failure. Launch diagnostics attach executable selection, arguments,
owned cache root and exit code without contaminating JSON stdout. No shared cache
is deleted, no permission or runner setting changes, and no suite serialization is
introduced.

## Validation and remaining work

Compile the architecture test project locally, but execute child-process and .NET
tests only through the remote repository gate on a live-gateway workstation:

```powershell
scripts/repo/Invoke-AzureBuildTest.ps1 -Mode core -WorktreePath <worktree>
```

Read `result.json` test counters and named TRX results, not merely the wrapper exit
code. The original failed container's cache bytes were never captured, so historical
root cause remains a bounded unknown; the launch contract no longer shares or uses
startup-profile state on either platform. No runner deployment or live gateway
rebuild is part of this test-only change.

## Actual lint-launch pipe and deadline safety (#3982)

The fifteen lint regressions use `RunLintAtAsync` through the existing synchronous
adapter. The helper starts asynchronous stdout and stderr drains together, then
awaits both drains and process exit under a linked 60-second safety deadline.
Tests may supply a shorter deadline to exercise failure; elapsed time is not a
success oracle. Caller cancellation remains cancellation; the helper's deadline
produces an explicit `TimeoutException`, never a lint exit code.

On cancellation or failure, the helper attempts owned process-tree termination
once, then confirms process exit and pipe completion under a separate 10-second
cleanup deadline. Cache deletion happens only after that confirmation. A cleanup
failure reports the executable, arguments and retained cache path; it does not
silently delete state belonging to a possibly live child. Timeout diagnostics
include up to 4096 characters from each drained stream. Ordinary results retain
complete, separate stdout and stderr, including pure JSON stdout.

Two regressions invoke this actual helper with an isolated script: one writes
2 MiB to stderr before closing stdout, and one emits a readiness marker then waits
indefinitely. The latter must fail explicitly, prove owned termination and cache
cleanup, and preserve an unrelated cache sentinel. An independent outer guard
contains the broken sequential/no-deadline mutation and confirms termination even
when the helper under test is deliberately defective. The mutation must fail both
named assertions without requiring the remote replica to be killed.

This addresses a reproducible test-helper pipe/wait defect, not the historical
exit-134 assembly-load cause. No assertion or exit-code contract is weakened.
