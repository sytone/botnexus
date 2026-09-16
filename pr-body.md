## Summary

Guide full-text search now reports indexing as complete only after every intended page body is cached. Failed pages remain explicitly retryable on a later search action, successful bodies are reused, concurrent searches share one indexing pass, and disposal cancels outstanding body requests.

Closes #3972

## Root cause

`Guide.LoadAllBodiesAsync` set `_fullTextLoaded` before issuing any body request, swallowed individual HTTP failures, and never restored an incomplete state. `OnSearchInput` therefore suppressed every later indexing attempt even when a page body was missing.

## Changes

- Track one active indexing task so overlapping search input shares the same pass.
- Mark full-text indexing complete only when every indexed page has a cached body.
- Preserve successful body cache entries while leaving failed pages eligible for a later search retry.
- Distinguish an active indexing message from an incomplete retry message.
- Cancel outstanding indexing requests when the Guide component is disposed.

## Anti-reinvention

The repair stays within the existing `Guide` body cache, `AllPages` enumeration, search-input path, and rendered bUnit seam. Current `origin/main`, merged PR #3557, directly related issues #3933 and #3934, open PR #4222, and the broad portal stack #4141 were inspected. PR #4222 changes selection-generation ownership in the same component but not indexing completion; #3934 covers relative-link routing. No second search index or persistence layer was introduced.

## Tests

- Exact-source RED run `20260916071651-d23a4dfa`: 19,127 total; 19,089 executed; 19,085 passed; 4 failed; 38 skipped; 0 fixture failures. The four new Guide regressions failed on premature completion, suppressed retry, duplicate-pass behavior, and missing disposal cancellation.
- `Full_text_indexing_remains_in_progress_until_every_body_completes` holds one body response and requires the rendered indexing state until completion.
- `Failed_body_is_retried_without_refetching_successful_pages` injects one failure, retries successfully, and pins per-page request counts.
- `Concurrent_searches_share_one_indexing_pass` proves overlapping input does not duplicate body requests.
- `Disposing_during_indexing_cancels_the_active_pass` observes cancellation at the controlled HTTP boundary.
- No assertion, skip, timeout threshold, or baseline was weakened to obtain green. One intermediate run exposed that bUnit fragment disposal did not directly invoke the component instance's synchronous disposal in this harness; the test was corrected to invoke the component contract explicitly while retaining the HTTP cancellation assertion.

## Validation

- `dotnet build tests/extensions/BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests/BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests.csproj --no-restore --disable-build-servers -m:1 -p:UseSharedCompilation=false`: succeeded with 0 warnings and 0 errors; it compiles the affected portal project and tests.
- Exact-source remote CORE run `20260916075837-dd56d284`, verified source digest `d5c307b99801043e5f2c43ff32932c5063b58948c95ee044301cb89c71248223`: 19,127 total; 19,089 executed and passed; 0 failed; 38 skipped; 0 fixture failures; complete result contract.

## UI evidence

No visible UI change — this corrects when the existing indexing status is shown and when an existing search result becomes available after retry; it adds no markup, layout, styling, or new visual capability. Rendered bUnit evidence covers the existing in-progress, incomplete/retry, and completed search states, plus searchable results after recovery. Browser-emulated E2E remains quarantined under #3601 and is not represented as passing evidence.

## Risk & rollback

- Blast radius is limited to Guide full-text indexing state, body-request retry, concurrent input ownership, and component disposal. Title/navigation behavior, page selection, Markdown sanitization, and relative-link behavior are unchanged.
- Revert the commit to restore one-shot indexing behavior.
- Tests are additive and exercise rendered behavior and request counts. No assertion, skip, threshold, or baseline was weakened.

## Merge notes

PR #4222 independently changes Guide selection ownership and may require ordinary line-level reconciliation if it lands first. Issue #3934 remains separate relative-link work. There is no schema, storage, endpoint, package, migration, configuration, CSS, or deployment-order change. Passing validation and CI do not authorize merge.
