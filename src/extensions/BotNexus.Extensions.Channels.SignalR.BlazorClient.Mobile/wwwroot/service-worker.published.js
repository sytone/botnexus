// Production service worker - offline-first for immutable assets, network-first for the shell.
//
// #2591: the previous strategy was unconditionally cache-first for every non-navigate GET,
// including index.html and service-worker-assets.js. Those are the two files that tell the app
// which fingerprinted bundle to load, so caching them meant an installed PWA could never learn
// that a new build existed. A client that had cached a pre-#2532 bundle kept running it against
// an upgraded gateway and died on the changed /api/sessions response shape with an opaque
// DeserializeUnableToConvertValue. The cache was not merely stale, it was stale FOREVER.
//
// The split below mirrors the server policy in SignalREndpointContributor.ResolveCacheControl:
//   - /_framework/ assets carrying a content hash are immutable by construction (a content
//     change produces a NEW filename) -> cache-first, so repeat loads still skip the ~1.6 MB
//     runtime download.
//   - everything else is served under a stable path and MUST revalidate -> network-first with
//     a cache fallback, so the app picks up a new deployment on the next load while staying
//     usable offline.
//
// FingerprintPattern is kept byte-identical to the copy in the desktop worker and is pinned by
// ServiceWorkerCacheStrategyTests, which evaluates this exact regex in .NET against sample URLs.
// The two workers must not silently diverge.

self.importScripts('./service-worker-assets.js');
self.addEventListener('install', event => event.waitUntil(onInstall(event)));
self.addEventListener('activate', event => event.waitUntil(onActivate(event)));
self.addEventListener('fetch', event => event.respondWith(onFetch(event)));

const cacheNamePrefix = 'botnexus-mobile-offline-';
const cacheName = `${cacheNamePrefix}${self.assetsManifest.version}`;
const offlineAssetsInclude = [/\.dll$/, /\.pdb$/, /\.wasm/, /\.html/, /\.js$/, /\.json$/, /\.css$/, /\.svg$/, /\.png$/, /\.webp$/];
const offlineAssetsExclude = [/^service-worker\.js$/];

// A fingerprinted framework asset: under /_framework/, with a base36 content-hash segment
// between the base name and the extension (e.g. dotnet.native.veuqw8a0w9.wasm). The hash must be
// >=8 lowercase-alphanumeric chars AND contain a digit -- the digit is what separates a real
// content hash from a word-like segment such as "webassembly" in blazor.webassembly.js, keeping
// loader entry points on the revalidating path. blazor.boot.json fails the digit test too.
//
// The WHOLE predicate is this one regex, deliberately: ServiceWorkerCacheStrategyTests extracts
// this exact literal from the file and evaluates it in .NET against sample URLs, so the test
// exercises the real matching behaviour rather than a re-derived copy of the rule. Do not move
// any part of the condition into surrounding JS -- that would make the test vacuous.
const fingerprintPattern = /\/_framework\/.+\.(?=[a-z0-9]*[0-9])[a-z0-9]{8,}\.[^./]+$/;

function isImmutableAsset(url) {
    return fingerprintPattern.test(url.pathname);
}

async function onInstall(event) {
    self.skipWaiting();
    const assetsRequests = self.assetsManifest.assets
        .filter(asset => offlineAssetsInclude.some(p => p.test(asset.url)))
        .filter(asset => !offlineAssetsExclude.some(p => p.test(asset.url)))
        .map(asset => new Request(asset.url, { integrity: asset.hash, cache: 'no-cache' }));
    const cache = await caches.open(cacheName);
    await cache.addAll(assetsRequests);
}

async function onActivate(event) {
    const cacheKeys = await caches.keys();
    await Promise.all(cacheKeys
        .filter(key => key.startsWith(cacheNamePrefix) && key !== cacheName)
        .map(key => caches.delete(key)));
    // Take control of already-open clients immediately so a newly activated worker does not
    // wait for every tab to close before its strategy applies (#2591).
    await self.clients.claim();
}

async function onFetch(event) {
    // Only cache GET requests
    if (event.request.method !== 'GET') return fetch(event.request);

    // Pass through top-level navigations to the network for auth challenges (#688)
    if (event.request.mode === 'navigate') return fetch(event.request);

    // Never cache SignalR or API requests
    const url = new URL(event.request.url);
    if (url.pathname.startsWith('/hub/') || url.pathname.startsWith('/api/')) {
        return fetch(event.request);
    }

    const cache = await caches.open(cacheName);

    // Immutable fingerprinted assets: cache-first. A content change renames the file, so a hit
    // is always correct and never needs revalidating.
    if (isImmutableAsset(url)) {
        const cachedResponse = await cache.match(event.request);
        return cachedResponse ?? fetch(event.request);
    }

    // Everything else (index.html, service-worker-assets.js, blazor.boot.json, hand-authored
    // css/js): network-first so a new deployment is picked up on the next load, with the cache
    // as an offline fallback. A successful response refreshes the cache for that fallback.
    try {
        const networkResponse = await fetch(event.request);
        if (networkResponse && networkResponse.ok) {
            await cache.put(event.request, networkResponse.clone());
        }
        return networkResponse;
    } catch (err) {
        const cachedResponse = await cache.match(event.request);
        if (cachedResponse) return cachedResponse;
        throw err;
    }
}
