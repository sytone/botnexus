---
title: "Release v0.44.0"
description: "Release notes for BotNexus v0.44.0"
date: "2026-08-12"
---

# Release v0.44.0

> **Released:** 2026-08-12
>
> **Full diff:** [v0.43.0...v0.44.0](https://github.com/sytone/botnexus/compare/v0.43.0...v0.44.0)

## [0.44.0] - 2026-08-12

### ✨ Features

- **#2766:** Add config shadow-mode diff harness (#2778)
- **#2646:** Add sqlite config store (#2784)
- **#2646:** Wire shadow mode to the sqlite store (#2786)
- **#2646:** Add the store-backed configuration read path (#2799)
- **#1888:** Add a live-session facet to the activity dashboard (#2822)
- **#2889:** Record per-phase timings in the runner artifact set (#2891)
- **#2818:** Add ralph conversation kind with turn-end loop (#2823)
- **#2857:** Surface participant roles on activity agent chips (#2913)
- **#2865:** Add docs-lint gate for literal drift and contradiction (#2972)
- **#2323:** Render ask_user prompts on telegram with inline keyboards (#2971)
- **#2974:** Add canvas guidance to the system prompt (#2980)
- **#2834:** Add worldId to config.json resolved once as injected value (#2996)
- **#2840:** Add HTTP endpoint to post a message into a conversation (#2998)
- **#2833:** Stamp and verify world identity on every sqlite store (#3000)
- **#2794:** Expand active agent loops into per-loop detail (#3008)
- **#3015:** Separate quota-exhaustion from transient retry lanes (#3021)
- **#2727:** Add operator-configurable secret redaction patterns (#3027)

### 🐛 Bug Fixes

- **#2628:** Bound signalr hub fixture startup (#2741)
- **#2725:** Classify spawn-only runs as handoff (#2742)
- **#2740:** Build the fts match expression explicitly (#2743)
- **channels:** Authorise RespondToAskUser on control scope, not signalr binding (#2744)
- **#2724:** Bound inbound telegram media by size and time (#2750)
- **#2731:** Wire channel extensions behind the fault barrier (#2763)
- **#2757:** Remove false-positive Nested quoting preflight heuristic (#2768)
- **#2746:** Bound the pending exec-approval registry with a ttl and cap (#2754)
- **cron:** Apply shared SSRF policy to webhook targets (#2756)
- **#2759:** Coerce stringified edits at the edit tool boundary (#2771)
- **#2723:** Kill the stdio server process when the mcp handshake fails (#2775)
- **#2722:** Strip terminal escapes from CLI-rendered stored strings (#2776)
- **#2772:** Report observed gateway stop and liveness outcomes (#2779)
- **#2774:** Reclaim ungrouped prompt-created firewall rules (#2783)
- **#2747:** Require explicit credentials for overridden gateway urls (#2803)
- **#2798:** Default init to a loopback bind with wildcard opt-in (#2814)
- **#2792:** Derive ask_user payload from the submit guard (#2800)
- **#2764:** Read compaction checks through the bound config path (#2787)
- **#2789:** Disclose spawn budget clamp on the tool result (#2802)
- **#2748:** Unify cron timezone resolution behind one resolver (#2791)
- **#2780:** Index markdown notes into the searchable memory store (#2804)
- **#2819:** Bind the cron store home by type and make remote validation trustworthy (#2841)
- **#2819:** Resolve the cron home by type, not an assembly-name string (#2843)
- **#2851:** Allow-list the runner skip classifier to fail closed (#2852)
- **#2158:** Default validation to remote and make local an explicit opt-in (#2867)
- **#2847:** Install the sub-agent deny-list before the child handle exists (#2875)
- **#2796:** Thread effective execution settings into runtime context (#2817)
- **#2808:** Normalise escaped markup before sanitizer scanning (#2821)
- **#2815:** Address outbound replies in the target channel's space (#2826)
- **#2795:** Bind agent config panel to the real endpoint shapes (#2829)
- **#2759:** Report full value length and truncation in tool argument errors (#2831)
- **#2900:** Derive the runner image tag from content, refuse overwrites (#2901)
- **#2739:** Serialise the e2e solution prebuild (#2749)
- **#2883:** Truncate content on surrogate-safe boundaries (#2922)
- **#2923:** Preserve astral characters when sanitizing external text (#2932)
- **#2903:** Drain active runs before archiving a session (#2935)
- **#2816:** Refuse config writes that flatten unnamed sections (#2828)
- **#2892:** Merge child env overrides through one platform-aware seam (#2930)
- **#2894:** Reject non-object OAuth response envelopes (#2931)
- **#2845:** Redact credentials from CLI gateway URL and transport error diagnostics (#2934)
- **#2844:** Exclude bodyless 400 from context-overflow detection (#2937)
- **#2870:** Decouple daily-memory injection from systemPromptFiles
- **#2882:** Preserve operator environment across service install (#2942)
- **#2902:** Bound streamed tool-call argument accumulation (#2943)
- **#2793:** Honour an explicit -LockWaitSeconds in gate mode (#2944)
- **#2936:** Return pre-compaction history to the portal (#2945)
- **#2856:** Match provider transient errors with a pattern table (#2946)
- **#2747:** Route prompt and validate through GatewayClientFactory (#2947)
- **#2788:** Give the activity conversation column an explicit width (#2949)
- **#2881:** Redact secrets from provider error bodies (#2958)
- **#2954:** Delimit transcript role records with a shared encoder (#2967)
- **#2785:** Reject stale test assemblies and stale base refs (#2962)
- **#2781:** Emit the fused relevance score instead of the rank ordinal (#2969)
- **#2976:** Bind canvas pane to the routed conversation (#2982)
- **#2979:** Schedule sub-agent deadline on the injected TimeProvider (#2983)
- **#2956:** Prune session-scoped memory rows on session delete (#2989)
- **#2992:** Implement SearchScoredAsync on RecordingMemoryStore (#2993)
- **#2984:** Separate cron run agenda from earlier-run minutes (#2991)
- **#2751:** Demote extension load banners from warning to information (#3002)
- **#2988:** Signal watcher readiness after arming, not before (#2990)
- **#2813:** Sanitize web tool output at the untrusted-content boundary (#3004)
- **#2997:** Skip check rows with no usable display key (#3007)
- **#2940:** Collapse leading ./ in context-file path normalization (#3009)
- **#2755:** Project GET /api/agents to a lean list DTO (#3011)
- **#2762:** Preflight inline node -e scripts before execution (#3017)
- **#3012:** Validate mcp url scheme before injecting bearer token (#3019)
- **#3018:** Serialise ProviderDiagnostics mutators in Gateway.Tests (#3023)
- **#2924:** Unify the three surrogate-safe truncation implementations (#3026)

### 📖 Documentation

- Daily documentation grooming 2026-08-03 (#2753)
- **#2858:** Correct the getting-started webui port to 5005 (#2866)
- Daily documentation grooming 2026-08-07 (#2850)
- **#2876:** Permit local dotnet build, ban only local test execution (#2879)
- **#2885:** Correct agent config model, spawn archetypes, and admonition rendering (#2886)
- **#2859:** Resolve config.json shape and location contradictions (#2951)
- **#2952:** Document ralph conversation kind and fix mkdocs tabs (#2953)
- **#3024:** Correct phantom prompt-pipeline docs and doctor check list (#3025)

### ⚡ Performance

- **#2910:** Scope test-fixture prebuilds to the deployment closure (#2912)
- **#2914:** Hoist the release build of src into the runner build phase (#2915)

### 🔨 Refactor

- **#2765:** Extract configuration into its own project (#2777)

### 🧪 Testing

- **#2525:** Pin redelivery idempotency across a lock-lost completion (#2806)
- **#2801:** Skip the differential control when it loses its own race (#2805)
- **#2830:** Derive locations test paths from the running platform (#2868)
- **#2869:** Widen the idle-timeout window, not the assertion (#2920)
- **#2933:** Drop scheduling-race count assert in watchdog cancel test (#3022)

### ⚙️ Miscellaneous

- **#2916:** Add area:trailguide to issue template dropdowns (#2917)

### Wip

- **#2906:** Persist tool arguments on tool result rows (#2939)
- **#2839:** Store-only webhook resolves conversation session (#2948)
- **#2748:** Scheduler-path timezone tests + logged UTC degradation (#2950)
- **#2921:** Guard empty assistant completion (#2957)
- **#2522:** Stamp provider prompt tokens on blocking PromptAsync paths (#2960)
- **#2774:** Derive firewall lease programs from build output (#2963)
- **#2846:** Conversation-scope gate for owner-private prompt files (#2964)
- **#2807:** Credential provenance resolver (#2965)
- **#2979:** Flake fixes + concurrency fence (#2987)
- **#2809:** Binding-aware dangerous-exec + computed env-harvesting (#2959)
- **#2961:** Auth path for unattended sync push (#2970)
- **#2977:** Busy_timeout handler disposal safety (#2981)
- **#2491:** Fixture-success skip fence + extension-boot health test (#2986)
- **#2985:** Execution-class marker + zero-tool-call cron outcome (#3001)
- **#2437:** Clause-by-clause close rule + advisory guard (#3006)
- **#2614:** Route the four remaining blocking paths through the tool-audit sink (#2999)
- **#2485:** In-band user-visible image drop notice (#3003)
- **#2767:** Feature flag inventory (#3016)
- **#3013:** Consolidate tilde expansion into HomePathExpander (#3020)

[0.44.0]: https://github.com/sytone/botnexus/compare/v0.43.0...v0.44.0

