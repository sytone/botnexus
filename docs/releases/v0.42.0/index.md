---
title: "Release v0.42.0"
description: "Release notes for BotNexus v0.42.0"
date: "2026-08-02"
---

# Release v0.42.0

> **Released:** 2026-08-02
>
> **Full diff:** [v0.41.0...v0.42.0](https://github.com/sytone/botnexus/compare/v0.41.0...v0.42.0)

## [0.42.0] - 2026-08-02

### ✨ Features

- **gateway:** Make ask_user a durable resumable checkpoint (#2290)
- **#2636:** Emit trailguide from init via shared defaults (#2704)
- **extensions:** Add plugin manifest and marketplace JSON Schema with validating parser (#2708)
- **#2692:** Hide internal conversations from the activity dashboard (#2721)

### 🐛 Bug Fixes

- **#2712:** Serialise the mobile chat timeline snapshot seam (#2713)
- **#2631:** Revert #2678, drop ChannelKey.Observer signalr literal (#2702)
- **subagent:** Apply model/apiProvider overrides to the child descriptor (#2660)
- **#2677:** Promote one shared subagentstatus terminal predicate (#2693)
- **#2650:** Add an explicit write grant for sub-agent paths (#2719)
- **#2608:** Skip indexing sessions whose store location was reclaimed (#2698)
- **#2707:** Make wasm build-output fence deterministic (#2711)
- **#2671:** Validate cron failure-alert target at every authoring seam (#2697)
- **#2670:** Bound cron scheduler phase 2 concurrency (#2720)
- **#2649:** Validate agent apiProvider against the model registry (#2661)
- **#2632:** Sandbox test harnesses against ambient git redirect (#2694)
- **#2706:** Adopt tri-state inheritance in extension config merger (#2715)
- **#2691:** State loopback/private-range refusal in web_fetch description (#2716)
- **#2522:** Plan compaction cuts in the trigger token units (#2717)
- **#2690:** Name the wrong input shape in edit diagnostics (#2718)
- **#2705:** Preserve explicit nulls across whole-document writes (#2710)
- **#2731:** Contain channel background-service faults at the channel boundary (#2737)
- **#2491:** Start the e2e fixture past the assistant collision (#2738)

### 📖 Documentation

- Daily documentation grooming 2026-08-02 (#2730)

### 🔨 Refactor

- **#2614:** Route both transports through one tool-audit sink (#2714)

### 🧪 Testing

- **#2701:** Fence [ConfigField] coverage across the config graph (#2703)

[0.42.0]: https://github.com/sytone/botnexus/compare/v0.41.0...v0.42.0

