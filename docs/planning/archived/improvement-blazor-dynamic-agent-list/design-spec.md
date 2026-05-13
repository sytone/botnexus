---
id: improvement-blazor-dynamic-agent-list
title: "Dynamic Agent List — Auto-Detect New Agents Without Page Refresh"
type: improvement
priority: low
status: proposed
created: 2025-07-17
tags: [blazor, agents, filesystem-watcher, ux]
---

# Improvement: Dynamic Agent List in Blazor UI

**Status:** proposed
**Priority:** low
**Created:** 2025-07-17

## Problem

When a new agent folder is added to the user's profile (`~/.botnexus/agents/`), the Blazor UI agent dropdown does not detect it. The user must perform a full page refresh to see newly added agents.

## Requirements

1. The Blazor UI agent dropdown should automatically reflect new (or removed) agent folders without requiring a page refresh.
2. Detection should work for agents added by any mechanism (manual folder creation, CLI provisioning, sub-agent spawning).
3. Minimal performance impact — avoid expensive directory scans on every render.

## Design Options

### Option A: Filesystem Watcher (Preferred)

Use `FileSystemWatcher` on the `~/.botnexus/agents/` directory to detect folder creation/deletion events. On change, push an update via SignalR to connected Blazor clients.

**Pros:** Near-instant detection, event-driven, low overhead.
**Cons:** `FileSystemWatcher` can be unreliable on some platforms/network drives; needs debouncing.

### Option B: Periodic Polling

Poll the agents directory on a timer (e.g., every 10–30 seconds) and compare against the current known list.

**Pros:** Simple, reliable across all platforms.
**Cons:** Slight delay before new agents appear; unnecessary I/O when nothing changes.

### Option C: Hybrid

Use `FileSystemWatcher` as primary with a periodic poll as fallback to catch any missed events.

## Suggested Approach

1. Add a `FileSystemWatcher` in the Gateway for the agents directory.
2. On directory created/deleted events, debounce (500ms) and broadcast an `AgentsChanged` SignalR event.
3. Blazor UI subscribes to `AgentsChanged` and refreshes the agent dropdown.
4. Optional: add a low-frequency poll (60s) as a reliability fallback.

## Scope

- Gateway-side: filesystem watcher + SignalR event
- Blazor UI: subscribe to event, refresh agent list
- No changes to agent configuration or discovery logic itself
