---
id: bug-blazor-message-timestamps-ordering
title: "Blazor WebUI — Messages Missing Timestamps and Potentially Misordered"
type: bug
priority: medium
status: delivered
created: 2026-04-17
tags: [bug, blazor, webui, ux, chat]
---

# Bug: Blazor WebUI — Messages Missing Timestamps and Potentially Misordered

**Status:** draft
**Priority:** medium
**Created:** 2026-04-17

## Problem

1. **No timestamps** — Assistant and user messages in the chat canvas show no date or time. In a long conversation or when reviewing history, there's no way to know when a message was sent.

2. **Ordering may be incorrect** — Messages may not be consistently ordered with the most recent at the bottom. Needs investigation to confirm whether this is a rendering issue or a data issue.

## Expected Behavior

- Each message should display a timestamp (at minimum time, with date for older messages)
- Messages should be ordered chronologically with the most recent at the bottom
- Session history loaded on switch should maintain correct chronological order

## Notes

- Reported from the new Blazor WebUI
- Timestamps are likely available in the message data from the server — just not rendered
- Ordering could be a client-side sort issue or the server returning messages in an unexpected order
