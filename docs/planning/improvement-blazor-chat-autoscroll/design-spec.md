---
id: improvement-blazor-chat-autoscroll
title: "Blazor Chat Canvas Auto-Scroll to Bottom"
type: improvement
priority: medium
status: draft
created: 2026-04-17
tags: [blazor, webui, ux, chat]
---

# Improvement: Blazor Chat Canvas Auto-Scroll to Bottom

**Status:** draft
**Priority:** medium
**Created:** 2026-04-17

## Problem

The Blazor WebUI chat canvas does not auto-scroll to the bottom when new messages arrive. Users must manually scroll down to see the latest messages, which breaks the basic chat UX expectation.

## Desired Behavior

- **Auto-scroll to bottom** when new messages are added and the user is already at or near the bottom of the chat
- **Do not force scroll** if the user has intentionally scrolled up to read history
- **Always scroll to bottom** when the user sends a message
- **Scroll to bottom** on initial session load / session switch

## Implementation Notes

Blazor WASM cannot directly manipulate the DOM — this requires a small JS interop call:

1. Add a JS function that scrolls a container element to the bottom (`element.scrollTop = element.scrollHeight`)
2. Call it via `IJSRuntime` after message list renders
3. Optionally track scroll position to detect if the user has scrolled up (e.g., check if `scrollTop + clientHeight >= scrollHeight - threshold`)

This is a small, self-contained change — likely a few lines of JS interop and a call in the message rendering lifecycle.

## Scope

- Small — estimated a few hours of work
- No backend changes required
- No new dependencies
