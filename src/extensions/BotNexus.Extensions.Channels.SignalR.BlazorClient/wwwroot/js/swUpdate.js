// swUpdate.js -- service-worker registration plus a live update check (#2591).
//
// Registering alone is not enough for a PWA. The browser only re-fetches service-worker.js when
// it decides to, and by default it may satisfy that fetch from the HTTP cache -- so a worker that
// is itself cached can keep an entire stale bundle alive indefinitely. That is exactly how an
// installed mobile client kept running a pre-#2532 bundle against an upgraded gateway and failed
// with an opaque DeserializeUnableToConvertValue on /api/sessions.
//
// Three things are needed, and all three are here:
//   1. updateViaCache: 'none'  -- the worker script itself is ALWAYS fetched from the network,
//      never from the HTTP cache, so a new deployment is discoverable.
//   2. registration.update() on foreground return -- a long-lived installed PWA may not
//      navigate for days; without an explicit check it never asks.
//   3. reload on 'controllerchange' -- once a new worker takes control the running document is
//      still the old bundle, so it must reload for the update to actually take effect.
//
// The reload is latched: 'controllerchange' also fires on the FIRST install (when there was no
// previous controller), and reloading then would be a pointless refresh on first visit.
window.BotNexusSwUpdate = {
    _reloading: false,

    register: function (scriptUrl) {
        if (!('serviceWorker' in navigator)) return;

        var self = this;

        navigator.serviceWorker.register(scriptUrl, { updateViaCache: 'none' }).then(function (registration) {
            // Check for a new worker whenever the app comes back to the foreground. This is the
            // only update trigger an installed PWA reliably gets -- it may never re-navigate.
            document.addEventListener('visibilitychange', function () {
                if (document.visibilityState === 'visible') {
                    registration.update().catch(function (err) {
                        console.warn('[BotNexus] service worker update check failed:', err);
                    });
                }
            });
        }, function (err) {
            console.warn('[BotNexus] service worker registration failed:', err);
        });

        // Only reload when we are REPLACING a controller. On a first install there is no prior
        // controller and the current document is already the newest bundle.
        var hadController = !!navigator.serviceWorker.controller;
        navigator.serviceWorker.addEventListener('controllerchange', function () {
            if (!hadController) return;
            if (self._reloading) return;
            self._reloading = true;
            window.location.reload();
        });
    }
};
