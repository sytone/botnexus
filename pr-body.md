## Summary

Stop the shared background-process output buffer from materializing and recounting its retained tail for every small append. Retained UTF-8 bytes are now maintained incrementally while preserving bounded tail retention and Unicode-scalar boundaries.

Closes #3943

## Root cause

`BackgroundOutputBuffer.AppendChunk` converted the complete `StringBuilder` to a string and called `Encoding.UTF8.GetByteCount` over that complete value on every append. Once the buffer reached its 100 KiB cap, each small process-output chunk therefore allocated and scanned roughly the entire retained tail again.

## Changes

- Count only the appended chunk and adjust the single cross-chunk UTF-16 surrogate-pair boundary.
- Scan only the head removed to restore the byte cap, including the existing bounded newline preference.
- Make empty final-drain appends a no-op.
- Add an allocation regression comparing identical small appends against 4 KiB and 100 KiB retained tails, plus split-scalar trimming coverage.

## Anti-reinvention

The fix stays in the existing shared `BackgroundOutputBuffer`, `OutputRetentionPolicy`, decoder, and process-registry seams. Current source, merged PR #3922, directly related process issues, and all open PRs were inspected; no open focused PR owns this accounting defect. Adjacent decoder, drain-failure, PID-reuse, interactive-input, and kill-lifecycle issues remain separate. No second buffer or retention policy was introduced.

## Tests

- Exact-source RED run `20260916031135-6528905c` failed only `AppendChunk_AllocationAfterWarmup_IsIndependentOfRetainedTailSize`: the same 4,096 appended characters allocated 4,223,048 bytes with a 4 KiB tail and 104,894,152 bytes with a 100 KiB tail.
- `AppendChunk_AllocationAfterWarmup_IsIndependentOfRetainedTailSize` now passes and reports 8,264 bytes versus 16,072 bytes for the same comparison.
- `AppendChunk_PreservesUtf8AccountingAcrossSplitScalarAndTrimming` verifies a surrogate pair split across calls remains one four-byte scalar while head trimming and discarded-byte accounting stay exact.
- Existing process-buffer, decoder, large-output, retention, and architecture tests remain enabled. No assertion, skip, timeout threshold, or baseline was weakened.

## Validation

- `dotnet build tests/agent/BotNexus.Agent.Core.Tests/BotNexus.Agent.Core.Tests.csproj --no-restore --disable-build-servers -m:1 -p:UseSharedCompilation=false`: succeeded with 0 warnings and 0 errors.
- `dotnet build tests/extensions/BotNexus.Extensions.ProcessTool.Tests/BotNexus.Extensions.ProcessTool.Tests.csproj --no-restore --disable-build-servers -m:1 -p:UseSharedCompilation=false`: succeeded with 0 warnings and 0 errors.
- `dotnet build tests/architecture/BotNexus.Architecture.Tests/BotNexus.Architecture.Tests.csproj --disable-build-servers -m:1 -p:UseSharedCompilation=false`: succeeded with 0 warnings and 0 errors.
- Exact-source remote CORE run `20260916033655-970dd6d1`, verified source digest `8fec9ae65d1e9e6d8b40d6c29a8824292af421b1a498b0eccafe7aac7fe485ed`: 19,125 total; 19,087 executed and passed; 0 failed; 38 skipped; 0 fixture failures; complete result contract.

## Risk & rollback

- Blast radius is limited to byte accounting and head trimming in the shared background-process output buffer. Process ownership, stream draining, decoder state, retention cap, disclosure banner, and lifecycle semantics are unchanged.
- Revert the commit to restore full-buffer recounting.
- Tests are additive. No assertion, skip, threshold, or baseline was weakened to obtain green.

## Merge notes

PR #3922 is already merged; its former instruction to repair that open worktree is stale. This focused PR targets current `main`. There is no schema, storage, configuration, dependency, UI, migration, or deployment-order change. Passing validation and CI do not authorize merge.
