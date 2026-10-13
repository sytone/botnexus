# Bounded owned-PR reconciliation

For maintainers reviewing or installing the repair for issue #4638. These are repository-owned source files, **not an installed skill update**. Nothing here changes a scheduler, gateway, Git configuration, or installed skill automatically. Use PowerShell 7 on Windows or Linux.

## Files and responsibilities

- `Reconciliation.Core.ps1`: injected policy core; no direct network, filesystem, environment authentication, or gateway access.
- `Get-BotNexusOwnedPullRequestHealth.ps1`: collector entrypoint, one complete successful JSON packet or a terminating sanitized error.
- `Invoke-BotNexusOwnedPullRequestHealth.ps1`: collector plus routing using the **same** context/deadline; successful JSON only after routing and readback.
- `templates/HostAdapter.ps1`: portable host template for HTTP transport, operation-scoped credentials, exclusive storage ownership, and atomic JSON persistence. Explicit paths replace machine-specific defaults.
- `tests/Test-BoundedReconciliation.ps1`: offline deterministic fixtures, including entrypoint/adapter integration and real isolated filesystem persistence.
- `tests/architecture/BotNexus.Architecture.Tests/BoundedPrReconciliationTests.cs` (repository-relative): runs the fixture suite as a subprocess in Architecture.Tests, which is part of CORE. Missing PowerShell or malformed/failed output fails the test; nothing is skipped.

## Compatibility

The collector preserves the successful fields `generatedAt`, `repository`, `scanned`, `attention`, and `healthyPullRequests`, including existing PR row/check fields. Ownership uses only the exact, case-sensitive authors `agent-farnsworth[bot]` and `app/agent-farnsworth`. External PRs remain part of the inventory consistency check but are never routed. `base.sha` is the current tip of that PR's base branch, not GitHub's captured PR base commit. The existing main-base behavior therefore remains intact and other base branches are supported.

The router preserves `status`, `code`, `scanned`, `ownedActionable`, `routed`, and `archived`; `archived` is always an empty array in this latency path. Marker format stays `head|observed-base|sorted-problem-codes`. Existing string-only markers and `{marker,routedAt}` state are read. New entries may also contain `pending`. State paths and credentials are now explicit: unattended invocations must be adapted during reviewed installation, not copied blindly. `SelfTest` is replaced by the repository fixture suite.

A healthy packet has no draft/failing-check/merge-conflict/base-behind or uncertainty problem codes. Completed checks with missing or unrecognized conclusions, and unrecognized check statuses, produce `unknown-check-result`. Null mergeability or an unknown/empty merge state produces `mergeability-unknown`. Known pending checks remain reported without an actionable code and are **not proof of readiness to merge**. This is not a review-comment collector or a merge gate. Successful routing does not authorize merging.

## Deadline, consistency and cache contract

The entrypoint starts its monotonic clock before loading the adapter. Default budget is 100 seconds, configurable from 1 through 110 seconds, below the 120-second scheduler boundary. Adapter startup/storage, collection, retries, gateway reads/mutations/readback, and final cache write consume that one budget. Requests receive `min(request limit, remaining budget)` in **milliseconds**. The host transport uses one cancellation token for connect, response headers, and response-body reading. Every stage checks remaining time before continuing and after requests/persistence. No deadline is reset when routing starts.

Default request limit is 10 seconds, two attempts and one-second retry delay. Only idempotent GETs retry transient timeouts/reset/closed connections or HTTP 429/502/503/504, within remaining time. Authentication/not-found errors do not retry. Gateway POSTs never retry, even on 503 or timeout. Errors expose only fixed stage/code labels, never credential values, request URLs, response bodies or raw exception strings.

Requests remain sequential deliberately: parallel fan-out would not make partial inventory safe. Under load the run can exhaust its budget; it then fails closed rather than publishing partial rows as healthy. This trades availability for consistency. No assertion is made that an arbitrary-size inventory can always succeed within 100 seconds. The scheduler's outer process limit remains the last defense against OS/runtime stalls (for example, synchronous local filesystem operations); use local, reliable storage, not network-mounted state paths.

Pull and check inventories use 100-row pages, an explicit terminal short/empty page, a 100-page safety limit and duplicate-identity detection. A short page advertising `next`, changing check totals, or incomplete check counts fails. A second complete pull census compares IDs, authors, state, captured head/base, draft and update stamps. PR detail/head/base/mergeability and current base tips are also independently read back. All check pages are independently revalidated to detect count-preserving page drift. GitHub does not offer an atomic snapshot across these endpoints: changes after the final observation belong to the next scan, not a claimed transactionally frozen snapshot.

Each check-page cache key includes repository, exact head SHA, base ref, observed base SHA **and the exact paginated endpoint**. Every scan sends a conditional request with that page's ETag; a head-only hit is never reused without network revalidation. A 304 requires an existing validated payload. A same-head check update returning a new ETag is evaluated immediately. Candidate cache pages publish only after the entire inventory passes readback; failed scans cannot publish a new cache. A successful scan replaces the cache with only its current pages to bound stale entries. ETags reduce payload transfer, not the number of required observations.

## Routing and uncertain side effects

The adapter holds non-blocking exclusive locks on the explicitly supplied state/cache paths for the invocation. Atomic writes replace JSON on the same filesystem. Concurrent invocations fail before creating or waking a conversation. A failed collector or gateway-list read leaves last-good markers untouched. Closed-PR marker pruning happens only after a complete scan; unresolved `pending` entries are never pruned.

Before creating a conversation or sending a wake, the router durably writes `pending` without changing the previous marker. It verifies a created conversation via GET before sending the wake. A wake requires the gateway's acceptance receipt (`wake`, matching `conversationId`, nonempty `sessionId`) and independent GET readback of the same active conversation/agent/session. Only then does it write the new marker/time.

The gateway create/message contracts (`CreateConversationRequest` in `ConversationDtos.cs` and `PostConversationMessageRequest` in `ConversationMessagesController.cs`, under `src/gateway/BotNexus.Gateway.Api/Controllers`) do not expose client-selected conversation IDs or idempotency keys. A lost response, failed readback, or process termination therefore cannot be safely retried automatically. The next scan fails closed with `uncertain: operator-readback-required` for an actionable pending PR instead of creating another conversation or sending another wake. **Do not delete `pending` as a routine recovery step.** An operator must inspect gateway conversations/session evidence and resolve the specific uncertain operation: retain the verified conversation, record a successful marker only when wake admission/readback can be established, or explicitly authorize clearing uncertainty after proving no side effect landed. The core does not guess.

Closed-issue conversation cleanup remains the independent responsibility of the existing `Remove-BotNexusClosedIssueConversations.ps1` selector/lifecycle cleanup lane. Do not add that closed-issue sweep back into this reconciler's shared latency budget. Reviewing the deployment must confirm the independent cleanup lane remains covered; these source files do not change cron configuration.

## Validate without live services

From the worktree root:

```powershell
pwsh -NoProfile -File scripts/pr-execution/tests/Test-BoundedReconciliation.ps1
```

The fixtures inject transport, monotonic and wall clocks, delay, a fake credential, cache and state. No real GitHub or gateway request is made. Host-adapter filesystem tests use isolated paths and release locks/delete their files. Architecture.Tests invokes this same suite with its owned temporary directory and isolated PowerShell startup cache; remote CORE is the authoritative integration check. Do not run local `dotnet test` on a live gateway host.

## Post-review installation (explicitly not performed here)

1. Review the source and exact-source CORE results. Keep the installed collector/router unchanged until approval.
2. Choose explicit local state/cache paths and back up the current marker JSON. Confirm existing independent closed-issue lifecycle cleanup is still owned outside this reconciler.
3. Copy the reviewed core, both entrypoints and adapter template together to the selected deployment directory, preserving relative layout (or pass `-AdapterPath` explicitly). Do not use retired `scripts/maintenance` as a source or destination convention.
4. Adjust the authorized host invocation to obtain a fresh operation-scoped bot installation token **before calling the entrypoint in-process**, and pass it as `-Token $token`. Never put a token literal on a CLI command line or in scheduler configuration. Acquisition itself must have its own bounded host limit within the scheduler's outer budget. Do not call the unsupported `Use-FarnsworthBot.ps1 -Command` parameter, or use its Git/environment-mutation path merely to collect health.
5. Supply `-StatePath`, optional `-CachePath`, and the chosen adapter path. If the gateway requires authentication, pass its separately acquired credential in-process as optional `-GatewayToken $gatewayToken`. The production adapter sends it only to gateway requests as `Authorization: Bearer`, matching `ApiKeyGatewayAuthHandler`; `-Token` is used only for GitHub. Without `-GatewayToken`, gateway requests have no authorization header. Neither credential is written to state/cache or diagnostics; never put either literal in scheduler configuration or a CLI command line. Confirm the entrypoint's maximum 110-second budget leaves sufficient room for host token acquisition/startup under the 120-second scheduler limit. Clear both host caller credential variables in `finally`; the entrypoints clear their own token/context references and never publish token environment variables.
6. First verify with a disposable fixture adapter and isolated storage, then explicitly approve production installation/scheduler invocation changes. Check successful JSON, sanitized failures, preserved last-good state, and pending uncertainty handling before relying on unattended routing.

Production installation, cron changes, and live gateway operations require separate authorization. Repository validation and PR publication remain part of the parent repair lifecycle; these scripts do not perform them.
