// Development service worker — passes all requests through to network.
// No caching in dev mode to avoid stale content during development.

self.addEventListener('install', () => self.skipWaiting());
self.addEventListener('activate', event => event.waitUntil(self.clients.claim()));

self.addEventListener('fetch', event => {
    // Pass through top-level navigations directly to the network.
    // HTTP auth challenges (Basic, NTLM, Negotiate) only trigger the browser's
    // native credentials dialog when the response comes directly from the network.
    if (event.request.mode === 'navigate') return;
});
