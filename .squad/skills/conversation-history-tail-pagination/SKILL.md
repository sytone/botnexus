---
name: "conversation-history-tail-pagination"
description: "Prevent refresh regressions by paging conversation history from newest entries"
domain: "gateway-runtime"
confidence: "high"
source: "runtime incident: Quill refresh dropped latest turns when history exceeded page size"
---

## Context
When chat UIs request only one history page on load (for example limit=200, offset=0), oldest-first pagination can hide recent turns in long conversations. Users perceive this as missing history after refresh.

## Patterns
### Page from tail by default
- Build the full ordered timeline (oldest → newest).
- Interpret `offset` as distance from newest entry.
- Compute page window from the tail so `offset=0` returns latest page.

### Keep chronological order in page
Even when selecting from the tail, return entries oldest → newest inside the page so rendering remains natural.

### Guard out-of-range offsets
If `offset >= totalCount`, return an empty page with the original `totalCount`.

## Examples
```csharp
var take = Math.Min(limit, totalCount - offset);
var startIndex = Math.Max(0, totalCount - offset - take);
var page = allEntries.Skip(startIndex).Take(take).ToList();
```

## Anti-Patterns
- Oldest-first `Skip(offset).Take(limit)` for refresh endpoints.
- Returning newest-first arrays that reverse chronology in UI rendering.
- Omitting `totalCount`, which prevents deterministic paging behavior.
