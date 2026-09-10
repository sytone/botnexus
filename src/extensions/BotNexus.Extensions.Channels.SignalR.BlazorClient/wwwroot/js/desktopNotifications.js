// desktopNotifications.js -- OS-level toasts for gateway notifications.
//
// The bell in the banner answers "what happened?" for someone who is looking at the portal. This
// answers it for someone who is not: an agent that fails at 2am, or a run that blocks waiting for
// input, should reach the person who started it without them having to keep a tab in front of
// them. That is the whole point of the notification manager.
//
// Three facts shape this file:
//
//   1. Permission can only be asked for from a user gesture. A prompt raised on page load is
//      ignored by Chrome and refused outright by Safari, and a permission that has been DENIED
//      can never be asked for again from script -- the user has to clear it in browser settings.
//      So the request is wired to a button in the panel, never to startup.
//   2. Permission is not the same as consent. A browser may have granted the origin permission
//      long ago for something else; that is not the user asking THIS portal to shout at them.
//      An explicit opt-in is kept alongside it, in localStorage, because it belongs to the
//      browser exactly like the permission does -- not to the gateway, and not to the account.
//   3. localStorage throws, it does not merely return null, when site data is blocked. Every
//      access here is guarded; a browser that refuses storage degrades to "off", not to an
//      exception thrown out of a banner that renders on every page.
window.botnexusDesktopNotifications = window.botnexusDesktopNotifications || {
    _ref: null,
    _key: 'botnexus.desktopNotifications',

    _supported: function () {
        return typeof window.Notification === 'function';
    },

    _enabled: function () {
        try {
            return window.localStorage.getItem(this._key) === 'on';
        } catch (e) {
            return false;
        }
    },

    // Which browser, so the "how do I unblock this" instructions name the right menu rather than
    // describing a generic browser nobody is using. Deliberately coarse: getting the family right
    // is what matters, and UA sniffing beyond that is a losing game. Order matters - Edge and
    // Chrome both claim to be Chrome, and every browser on iOS claims to be Safari.
    _browser: function () {
        var ua = navigator.userAgent || '';

        if (/Edg\//.test(ua)) return 'edge';
        if (/Firefox\//.test(ua)) return 'firefox';
        if (/Chrome\//.test(ua)) return 'chrome';
        if (/Safari\//.test(ua)) return 'safari';

        return 'other';
    },

    // ── Web push ────────────────────────────────────────────────────────────────────────
    //
    // The in-page path above only works while the portal is open. Push works when it is closed:
    // the browser wakes the service worker and IT draws the notification. Both need the same
    // permission, so the one toggle drives both and push is simply used when it is available.

    _pushSupported: function () {
        return 'serviceWorker' in navigator && 'PushManager' in window;
    },

    // Whether THIS browser currently holds a push subscription.
    pushSubscribed: async function () {
        if (!this._pushSupported()) return false;

        try {
            var registration = await navigator.serviceWorker.getRegistration();
            if (!registration) return false;

            return (await registration.pushManager.getSubscription()) !== null;
        } catch (e) {
            return false;
        }
    },

    // Subscribes and registers with the gateway. Returns true only if BOTH happened - a
    // subscription the gateway does not know about would silently never be pushed to.
    enablePush: async function () {
        if (!this._pushSupported() || window.Notification.permission !== 'granted') return false;

        try {
            var registration = await navigator.serviceWorker.ready;
            var existing = await registration.pushManager.getSubscription();

            var subscription = existing || await registration.pushManager.subscribe({
                // Required, and a promise: every push MUST result in something the user sees.
                // The service worker keeps that promise; breaking it costs the subscription.
                userVisibleOnly: true,
                applicationServerKey: this._toUint8((await (await fetch('/api/notifications/push/key')).json()).publicKey)
            });

            var json = subscription.toJSON();

            var response = await fetch('/api/notifications/push/subscribe', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    endpoint: subscription.endpoint,
                    p256dh: json.keys ? json.keys.p256dh : null,
                    auth: json.keys ? json.keys.auth : null
                })
            });

            if (!response.ok) {
                // Do not leave a browser subscribed to a gateway that refused it: the browser
                // would report itself as subscribed and nothing would ever arrive.
                if (!existing) await subscription.unsubscribe();
                return false;
            }

            return true;
        } catch (e) {
            console.warn('[BotNexus] push subscribe failed:', e);
            return false;
        }
    },

    disablePush: async function () {
        if (!this._pushSupported()) return true;

        try {
            var registration = await navigator.serviceWorker.getRegistration();
            if (!registration) return true;

            var subscription = await registration.pushManager.getSubscription();
            if (!subscription) return true;

            // Tell the gateway first. If this browser dropped the subscription and then failed to
            // say so, the gateway would keep pushing to a dead endpoint until the push service
            // reported it gone.
            await fetch('/api/notifications/push/unsubscribe', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ endpoint: subscription.endpoint })
            });

            return await subscription.unsubscribe();
        } catch (e) {
            console.warn('[BotNexus] push unsubscribe failed:', e);
            return false;
        }
    },

    // The VAPID key travels as base64url and applicationServerKey wants raw bytes.
    _toUint8: function (base64url) {
        var padded = (base64url + '='.repeat((4 - base64url.length % 4) % 4))
            .replace(/-/g, '+').replace(/_/g, '/');
        var raw = atob(padded);
        var out = new Uint8Array(raw.length);

        for (var i = 0; i < raw.length; i++) out[i] = raw.charCodeAt(i);

        return out;
    },

    status: function () {
        return {
            supported: this._supported(),
            permission: this._supported() ? window.Notification.permission : 'unsupported',
            enabled: this._enabled(),
            // A page served over plain http to anything but localhost is not a secure context, and
            // browsers report permission as 'denied' there no matter what the user does. Without
            // this flag that denial is indistinguishable from a real one, and the portal ends up
            // sending people to a browser setting that cannot fix it.
            secure: window.isSecureContext === true,
            origin: window.location.origin,
            browser: this._browser(),
            pushSupported: this._pushSupported()
        };
    },

    // Status including the push subscription, which can only be read asynchronously. Kept separate
    // from status() so the synchronous parts stay cheap for the common render.
    statusWithPush: async function () {
        var status = this.status();
        status.pushSubscribed = await this.pushSubscribed();

        return status;
    },

    setEnabled: function (on) {
        try {
            window.localStorage.setItem(this._key, on ? 'on' : 'off');
        } catch (e) {
            // Site data blocked: the toggle cannot be remembered, so it stays off. Reporting the
            // real status back is what lets the UI say so rather than lie about being on.
        }

        return this.status();
    },

    // Called from the button in the notification panel, so it always runs inside a user gesture.
    request: async function () {
        if (!this._supported()) return this.status();

        var permission = window.Notification.permission;

        if (permission === 'default') {
            permission = await window.Notification.requestPermission();
        }

        // Granting IS the opt-in: the user just answered a prompt they asked for. Making them
        // then flip a second switch would be a dark pattern in reverse.
        if (permission === 'granted') this.setEnabled(true);

        return this.status();
    },

    // The portal hands over a reference to the notification centre so a clicked toast can route
    // inside the running app. Without it a click still works, it just costs a full reload.
    register: function (ref) { this._ref = ref; },

    unregister: function () { this._ref = null; },

    show: function (id, title, body, link) {
        if (!this._supported() || window.Notification.permission !== 'granted' || !this._enabled()) {
            return 'inactive';
        }

        // Someone watching the portal has already been told: the badge moved and the list is one
        // click away. An OS toast on top of that is noise, and noise is how notifications get
        // switched off. The toast is for when the portal is NOT what you are looking at.
        if (document.visibilityState === 'visible' && document.hasFocus()) {
            return 'suppressed-visible';
        }

        try {
            var toast = new window.Notification(title, {
                body: body || '',
                // The notification id as tag: a re-push of the same notification REPLACES its
                // toast instead of stacking a second copy of the same news.
                tag: id,
                icon: document.baseURI + 'icon-192.png'
            });

            var self = this;

            toast.onclick = function () {
                window.focus();
                toast.close();

                if (!link) return;

                if (self._ref) {
                    // Routed inside the SPA - clicking a toast must not reboot the WASM app.
                    self._ref.invokeMethodAsync('OpenFromDesktopNotification', link).catch(function () {
                        window.location.href = link;
                    });
                } else {
                    window.location.href = link;
                }
            };

            return 'shown';
        } catch (e) {
            // Android Chrome throws here by design: it permits notifications only through a
            // service worker. That is the web-push path, not this one, so this is an expected
            // failure on those browsers rather than a defect.
            console.warn('[BotNexus] desktop notification failed:', e);
            return 'failed';
        }
    }
};
