// Minimal service worker for offline capability.
// Caches app shell assets on install and serves from cache when offline.

const CACHE_NAME = 'botnexus-mobile-v1';

const APP_SHELL = [
    './',
    './index.html',
    './css/mobile.css',
    './manifest.json',
];

self.addEventListener('install', event => {
    event.waitUntil(
        caches.open(CACHE_NAME).then(cache => cache.addAll(APP_SHELL))
    );
    self.skipWaiting();
});

self.addEventListener('activate', event => {
    event.waitUntil(
        caches.keys().then(keys =>
            Promise.all(keys.filter(k => k !== CACHE_NAME).map(k => caches.delete(k)))
        )
    );
    self.clients.claim();
});

self.addEventListener('fetch', event => {
    // Only handle GET requests
    if (event.request.method !== 'GET') return;

    // Pass through SignalR/API requests
    const url = new URL(event.request.url);
    if (url.pathname.startsWith('/hub/') || url.pathname.startsWith('/api/')) return;

    event.respondWith(
        caches.match(event.request).then(cached => cached ?? fetch(event.request))
    );
});
