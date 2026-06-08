// Production service worker — offline-first caching strategy.
// Uses the .NET-generated asset manifest to cache app shell resources.

self.importScripts('./service-worker-assets.js');
self.addEventListener('install', event => event.waitUntil(onInstall(event)));
self.addEventListener('activate', event => event.waitUntil(onActivate(event)));
self.addEventListener('fetch', event => event.respondWith(onFetch(event)));

const cacheNamePrefix = 'botnexus-offline-';
const cacheName = `${cacheNamePrefix}${self.assetsManifest.version}`;
const offlineAssetsInclude = [/\.dll$/, /\.pdb$/, /\.wasm/, /\.html/, /\.js$/, /\.json$/, /\.css$/, /\.svg$/, /\.png$/, /\.webp$/];
const offlineAssetsExclude = [/^service-worker\.js$/];

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
}

async function onFetch(event) {
    // Only cache GET requests
    if (event.request.method !== 'GET') return fetch(event.request);

    // Pass through top-level navigations to the network for auth challenges
    if (event.request.mode === 'navigate') return fetch(event.request);

    // Never cache SignalR or API requests
    const url = new URL(event.request.url);
    if (url.pathname.startsWith('/hub/') || url.pathname.startsWith('/api/')) {
        return fetch(event.request);
    }

    const cache = await caches.open(cacheName);
    const cachedResponse = await cache.match(event.request);
    return cachedResponse ?? fetch(event.request);
}
