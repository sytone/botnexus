# Portal PWA caching and updates

Both BotNexus portals — the desktop client at `/` and the mobile client at `/mobile` — are Blazor
WebAssembly PWAs backed by a service worker. This page documents the caching contract, why it is
shaped the way it is, and the constraint it places on server-side API changes.

## Why this has its own page

Issue #2591: an installed mobile client kept serving a WASM bundle from a previous release
indefinitely. That bundle predated PR #2543 (#2532), which changed `GET /api/sessions` from a bare
JSON array to a paged object envelope. The stale client called the upgraded gateway, received `{`
where it expected `[`, and the portal failed to start with:

```
Portal failed to load: DeserializeUnableToConvertValue,
System.Collections.Generic.List`1[...SessionSummary] Path: $ | LineNumber: 0 | BytePositionInLine: 1.
```

There was no in-app recovery. The only remedy was clearing site data, which is not discoverable.

## The caching contract

The published service worker (`wwwroot/service-worker.published.js`, one near-identical copy per
portal) splits every request into exactly two classes:

| Class | Strategy | Rationale |
|---|---|---|
| Fingerprinted `/_framework/` assets | **cache-first** | Immutable by construction — a content change produces a *new filename*, never mutated bytes. A cache hit is always correct, and skipping the ~1.6 MB runtime re-download is the whole point of the PWA. |
| Everything else (`index.html`, `service-worker-assets.js`, `blazor.boot.json`, hand-authored css/js) | **network-first, cache fallback** | Served under a *stable* path, so a cache hit may be a previous release. These files are what point at the fingerprinted bundle, so caching them is what makes staleness permanent. The cache fallback keeps the app usable offline. |
| `/api/*`, `/hub/*` | **never cached** | Live gateway state. |
| `mode === 'navigate'` | **network, always** | HTTP auth challenges only surface from a network response (#688). |

This split deliberately **mirrors the server policy** in
`SignalREndpointContributor.ResolveCacheControl`, which emits
`public, max-age=31536000, immutable` for fingerprinted assets and `no-cache` for everything else.
The two layers agree by design; if you change one, change the other.

### The fingerprint rule

A file counts as fingerprinted when it lives under `/_framework/` and carries a base36 content-hash
segment of at least 8 lowercase-alphanumeric characters containing **at least one digit**, between
the base name and the extension:

```
/_framework/dotnet.native.veuqw8a0w9.wasm          -> immutable
/_framework/System.Private.CoreLib.s1cucomlii.wasm -> immutable
/_framework/blazor.webassembly.js                  -> NOT immutable
/_framework/blazor.boot.json                       -> NOT immutable
```

The digit requirement is what separates a real content hash (`veuqw8a0w9`) from a word-like segment
(`webassembly`), keeping loader entry points on the revalidating path. Without it, `blazor.webassembly.js`
would be cached forever and the app could never bootstrap a new build.

The rule lives in the worker as a single regex, `fingerprintPattern`, and `isImmutableAsset` does
nothing but call it. That is a deliberate constraint: `ServiceWorkerCacheStrategyTests` extracts the
regex literal from the file and evaluates it in .NET against real asset names, and separately pins
the function body. Both are needed — a mutation replacing the body with `return true` restored the
original bug while leaving a perfectly correct regex in place, and survived every test until the
body was pinned too.

## The update path

Correct caching is necessary but not sufficient. A service worker is only replaced when the browser
re-fetches `service-worker.js` and sees different bytes, and by default that fetch may itself be
served from the HTTP cache. Three mechanisms, all in `wwwroot/js/swUpdate.js`, close that loop:

1. **`updateViaCache: 'none'`** at registration — the worker script is always fetched from the
   network, so a new deployment is discoverable at all.
2. **`registration.update()` on `visibilitychange`** — an installed PWA may not navigate for days.
   Without an explicit check on foreground return it never asks.
3. **Reload on `controllerchange`** — activating a new worker does not change the already-running
   document. Without the reload the user keeps executing the old bundle until every tab is closed.

The reload is latched on whether a controller existed beforehand: `controllerchange` also fires on
first install, where there is no prior controller and the running document is already newest.
Reloading then would be a gratuitous refresh on every user's first visit.

`index.html` must call `BotNexusSwUpdate.register('service-worker.js')` rather than
`navigator.serviceWorker.register(...)` directly — a bare registration bypasses all three guarantees.
This wiring is asserted by test, not left to convention.

## Constraint on API changes

> **A breaking change to a REST response shape is a breaking change for cached clients.**

The server and the browser bundle are versioned independently in practice: the gateway is whatever
was last deployed, the client is whatever the user's PWA last managed to install. Assuming "the
client is always the one we just built" is what made #2591 unrecoverable.

When changing a response shape consumed by the portal:

- Prefer an additive change (new field, old field retained) over a shape change.
- If the shape must change, expect *some* window where old clients are in the field. The caching
  fix above bounds that window to roughly one foreground return, rather than forever — but it does
  not make it zero, and it does nothing for a client that is offline at the moment of deployment.
- A failure to deserialize a gateway response is a *contract* failure, not a transient error. It
  should not be presented as a generic load error with no path forward.

## Files

| Path | Role |
|---|---|
| `...BlazorClient.Mobile/wwwroot/service-worker.published.js` | Mobile published worker (source of truth for the shared logic) |
| `...BlazorClient/wwwroot/service-worker.published.js` | Desktop published worker — identical except the cache-name prefix |
| `...BlazorClient.Mobile/wwwroot/js/swUpdate.js` | Registration + update-check helper (copied to both portals) |
| `...BlazorClient*/wwwroot/index.html` | Must route registration through the helper |
| `...Channels.SignalR/SignalREndpointContributor.cs` | Server-side `Cache-Control` policy the worker mirrors |
| `...BlazorClient.Mobile.Tests/ServiceWorkerCacheStrategyTests.cs` | Pins the strategy, the regex, the predicate body, and the wiring |

## Related

- #2591 — mobile portal bricked by a stale service-worker cache
- #2532 / PR #2543 — the `/api/sessions` shape change that exposed it
- #2413 — server-side ETag/`Cache-Control` policy for portal assets
- #688 — navigate-mode bypass for HTTP auth challenges
- #1780 — the issue that introduced the mobile service worker
