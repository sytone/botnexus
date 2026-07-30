---
title: "Release v0.38.0"
description: "Release notes for BotNexus v0.38.0"
date: "2026-07-29"
---

# Release v0.38.0

> **Released:** 2026-07-29
>
> **Full diff:** [v0.37.0...v0.38.0](https://github.com/sytone/botnexus/compare/v0.37.0...v0.38.0)

## [0.38.0] - 2026-07-29

### ✨ Features

- **#2372:** Verify downloaded payloads before execution (#2377)
- **#2327:** Group the mobile conversation picker like the sidebar (#2360)
- **channels:** Add channel-neutral conversation event contracts and publication seam (#2364)
- **#2340:** Add first-class conversation visibility (#2343)
- **#2374:** Add opus 5 and parse model versions for gating (#2381)
- **agents:** Suggest nearest schema property on unknown tool arg (#2472)
- **#2037:** Add portal landing page with start-conversation controls (#2367)
- **#2385:** Badge conversation origin on the Activity dashboard (#2496)
- **#2106:** Add embedding abstraction and hybrid memory retrieval (#2356)
- **#2441:** Reclaim portal space with top-bar identity and density (#2445)
- **#2449:** Add submitToAgent canvas bridge verb (#2454)

### 🐛 Bug Fixes

- **#2392:** Restrict config and credential files to owner-only (#2399)
- **#1430:** Strip delimited runtime-context on user-visible channels (#2355)
- **gateway:** Add askFallback posture so approval-required tools no longer fail open (#2397)
- **gateway:** Feed recent-log store from the Serilog pipeline (#2403)
- **container:** Ship first-party extensions in the published image (#2398)
- **#2400:** Make pre-commit self-test independent of ambient validation env (#2401)
- **gateway:** Re-anchor grep result paths to the requested prefix (#2402)
- **scripts:** Resolve repo root from PSScriptRoot in ci-pr-sync-main (#2451)
- **#2406:** Resolve symlinks before sandbox containment check (#2448)
- **#2413:** Emit cache validators on revalidated portal assets (#2422)
- **#2415:** Accept boxed integer tool arguments before rejecting (#2446)
- **#2365:** Declare entryAssembly and extensionTypes on three manifests (#2366)
- **#2447:** Retry channel adapter start with bounded backoff (#2450)
- **#2373:** Preflight and classify cron model overrides (#2378)
- **gateway:** Prune extension services the host container cannot activate (#2453)
- **extensions:** Preserve real process exit codes in exec tool (#2459)
- **#2395:** Skip session cleanup while an agent run is in flight (#2456)
- **scripts:** Reap stranded validation locks by owner liveness (#2457)
- **#2438:** Queue hub follow-up until the run settles (#2458)
- **#2387:** Central request-cancellation seam returns 499 not 500 (#2474)
- **recover:** Use interactive copilot session and prime with topology (#2470)
- **cron:** Reap orphaned running cron runs (#2473)
- **#2357:** Replace config.json via file.replace with bounded retry (#2464)
- **#2386:** Bound retry storms in service bus and telegram loops (#2467)
- **#2131:** Guard conversation saves with optimistic concurrency (#2471)
- **#2460:** Log a structured reason when compaction aborts (#2465)
- **#2389:** Support command jobs in the cron tool and optional rest id (#2461)
- **providers:** Warn instead of silently dropping images (#2490)
- **#2484:** Carry draft attachments through steer, redirect, follow-up (#2494)
- **#2411:** Paginate GET /api/sessions and bound ListSummariesAsync (#2468)
- **#2499:** Page session loading in the portal registry (#2503)
- **#2478:** Abort before draining follow-ups on a cancelled run (#2508)
- **tests:** Implement DraftAttachment overloads on canvas test stub (#2514)
- **portal:** Treat agent-minted user conversations as writable (#2527)
- **#2481:** Unify extension ALC on host-shipped assemblies (#2512)
- **#2462:** Gate cron command firing through exec tool policy (#2505)
- **#2134:** Mutate config sections under the writer lock (#2504)
- **compaction:** Measure provider vs estimator tokens on abort (#2531)
- **#2477:** Make missed-run detection idempotent across restarts (#2497)
- **#2479:** Honor cancellation immediately before process start (#2501)
- **#2495:** Report vision payload drop on non-in-process handles (#2506)
- **#2487:** Re-initialize expired mcp http session (#2516)
- **#2530:** Stop agent ids becoming external channel addresses (#2538)
- **#2529:** Stop proactive sends inheriting an unrelated conversation (#2534)
- **portal:** Add home entry to the left nav (#2537)
- **update:** Pre-flight dirty-repo check and classified pull failures (#2498)
- **#2418:** Guide agents off blocked loopback fetches (#2517)
- **#2520:** Redactor fails closed on malformed runtime markers (#2540)
- **#2532:** Page sessions by the filtered set, not the global list (#2543)
- **#2539:** Document scope default truthfully and pin it (#2542)
- **servicebus:** Renew SB lock, split ack failure from handler failure (#2546)

### 📖 Documentation

- **channels:** Add Teams<->BotNexus Service Bus Logic App examples (#1914)
- **#221:** Add code standards guide for xml comments and testing (#2353)
- **#219:** Add signalr hub, agents, and sessions api reference (#2354)
- **cron:** Reconcile cron tool actions with the live input schema (#2466)
- Daily documentation grooming 2026-07-25/29 (cron config, hub methods, releases) (#2380)
- **#2544:** Add issue templates and issue conventions (#2545)

### 🔨 Refactor

- **#2489:** Move compaction skip reasons to a smart enum (#2507)
- **#2442:** Centralise the actor pseudonym behind one primitive (#2515)

### 🧪 Testing

- **#2066:** Add physical-file config mutation e2e matrix (#2363)
- **#2404:** Audit path-returning tools for symlink display paths (#2469)
- **#2404:** Pin requested-prefix display paths through symlinks (#2509)

[0.38.0]: https://github.com/sytone/botnexus/compare/v0.37.0...v0.38.0

