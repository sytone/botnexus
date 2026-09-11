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

// ── Web push ────────────────────────────────────────────────────────────────────────────────
//
// This is what reaches someone with the portal CLOSED: the browser wakes this worker, hands it
// the encrypted payload the gateway sent, and it draws the notification. The page is not running
// and cannot help, which is why none of this can live in the app.
//
// userVisibleOnly was promised at subscribe time, so every push MUST show something. A push that
// draws nothing is a broken promise browsers punish by revoking the subscription, so the catch
// below still shows a generic notice rather than returning quietly.
self.addEventListener('push', function (event) {
    var data = {};

    try {
        data = event.data ? event.data.json() : {};
    } catch (e) {
        data = {};
    }

    var title = data.title || 'BotNexus';
    var options = {
        body: data.body || '',
        // The notification id as tag: the same notification arriving twice replaces its toast
        // rather than stacking a second copy of the same news.
        tag: data.id || 'botnexus',
        icon: '/icon-192.png',
        badge: '/icon-192.png',
        data: { link: data.link || null }
    };

    event.waitUntil(self.registration.showNotification(title, options));
});

self.addEventListener('notificationclick', function (event) {
    event.notification.close();

    var link = (event.notification.data && event.notification.data.link) || null;

    event.waitUntil((async function () {
        var clientList = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });

        // Prefer focusing a portal that is already open over opening a second copy of it.
        for (var i = 0; i < clientList.length; i++) {
            var client = clientList[i];

            if (client.url.indexOf(self.registration.scope) === 0) {
                await client.focus();

                if (link && 'navigate' in client) {
                    try {
                        await client.navigate(new URL(link, self.registration.scope).href);
                    } catch (e) {
                        // Cross-origin or a client that refuses; the focus already happened.
                    }
                }

                return;
            }
        }

        await self.clients.openWindow(new URL(link || '', self.registration.scope).href);
    })());
});
