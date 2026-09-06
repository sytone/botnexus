# PowerShell child startup isolation in docs lint tests

`DocsLintScriptTests` launches a fresh PowerShell child for each lint invocation.
Each launch owns a temporary cache directory until the child exits. Overrides are
applied only to `ProcessStartInfo.Environment` through the existing
`ProcessEnvironment.Merge` seam; the test host's environment is not changed.

| Platform | Isolated state | Remaining limitation |
| --- | --- | --- |
| Unix | `XDG_CACHE_HOME` points to the owned directory, selecting its `powershell` subdirectory for startup profiles; `PSModuleAnalysisCachePath` selects an owned module-cache file | Isolation prevents sharing these paths; it does not establish the cause of a historical crash |
| Windows | `PSModuleAnalysisCachePath` selects an owned module-cache file | Startup-profile isolation is **not** claimed: PowerShell uses `Environment.SpecialFolder.LocalApplicationData`, not the Unix XDG selector |

## Why `-NoProfile` is insufficient

In the inspected [PowerShell ConsoleHost source](https://github.com/PowerShell/PowerShell/blob/b9ff1da7/src/Microsoft.PowerShell.ConsoleHost/host/msh/ConsoleHost.cs),
startup calls `ProfileOptimization.SetProfileRoot(Platform.CacheDirectory)` and
`StartProfile("StartupProfileData-NonInteractive")` independently of loading user
PowerShell profile scripts. The [platform selector](https://github.com/PowerShell/PowerShell/blob/b9ff1da7/src/System.Management.Automation/CoreCLR/CorePsPlatform.cs)
resolves Unix cache state beneath `XDG_CACHE_HOME/powershell`.
The [module analysis cache](https://github.com/PowerShell/PowerShell/blob/b9ff1da7/src/System.Management.Automation/engine/Modules/AnalysisCache.cs)
honors `PSModuleAnalysisCachePath` separately. Isolating only that file does not
isolate startup JIT profile data.

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

The named mutation **omit Unix startup-profile override** retains module-cache
isolation but removes `overrides["XDG_CACHE_HOME"] = Root`. It must fail the
inherited-input, independent-path and actual child-cache-root assertions. This
separates the regression oracle from stochastic corruption or module-cache behavior.

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
code. Keep issue #3968 open for a captured failing startup with full identity and
cache evidence, and for separately scoped Windows startup-profile investigation.
No runner deployment or live gateway rebuild is part of this test-only change.

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
