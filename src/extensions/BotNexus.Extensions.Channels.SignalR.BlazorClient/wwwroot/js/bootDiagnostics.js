// bootDiagnostics.js -- renders an actionable panel when the Blazor WASM boot fails.
//
// Issue #2880: with default autostart, a failed `_framework/*.wasm` download rejects Blazor's
// start promise with nothing attached. The static `.loading-screen` div in index.html is never
// torn down, so the user sees "Loading BotNexus..." indefinitely and the only diagnosis available
// is the devtools console. `#blazor-error-ui` does NOT cover this: it is Blazor's *runtime* error
// bar and is not shown for a pre-start platform failure.
//
// The subtle part is WHY a message is not enough. When an authenticating reverse proxy (NetBird's
// OAuth2 proxy, in the observed incident) answers an unauthenticated sub-resource request with
// `200 OK` and an HTML login page, Blazor hashes that HTML, the digest does not match
// blazor.boot.json, and the browser blocks the resource under SRI. SRI failures reach the fetch
// API as an opaque `TypeError: Failed to fetch` -- the real cause (auth) is erased before any
// application code can observe it, and is byte-for-byte identical to a genuine platform fault.
// The only way to tell them apart is to re-probe a `_framework/` path afterwards and look at the
// response content type. A `text/html` body on that path means an interstitial, not our bug.
//
// Everything here is defensive: this code runs when the app is already broken, so it must never
// throw, and must never leave the user staring at a spinner because the diagnostics failed too.
(function () {
    'use strict';

    var KIND_AUTH = 'auth-interstitial';
    var KIND_PLATFORM = 'platform';

    function escapeHtml(value) {
        return String(value === undefined || value === null ? '' : value)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#39;');
    }

    function errorText(err) {
        if (!err) {
            return 'Unknown error.';
        }
        if (typeof err === 'string') {
            return err;
        }
        if (err.message) {
            return String(err.message);
        }
        try {
            return JSON.stringify(err);
        } catch (_) {
            return String(err);
        }
    }

    function originRoot() {
        try {
            return (window.location && window.location.origin) || '/';
        } catch (_) {
            return '/';
        }
    }

    // Probe a `_framework/` path rooted at the ORIGIN, not the Blazor <base href> -- the proxy
    // interstitial is served for any path under the origin, and the desktop and mobile clients
    // have different base hrefs ("/" and "/mobile/").
    function probeUrl() {
        try {
            return new URL('/_framework/blazor.boot.json', originRoot()).toString();
        } catch (_) {
            return '/_framework/blazor.boot.json';
        }
    }

    /// Classifies a boot failure by re-requesting a _framework asset and inspecting its content
    /// type. Resolves to KIND_AUTH only on positive evidence of an HTML body; anything else --
    /// including a probe that itself fails -- stays KIND_PLATFORM rather than guessing.
    function classify(fetchFn) {
        var url = probeUrl();
        var request;
        try {
            request = fetchFn(url, { credentials: 'same-origin', cache: 'no-store' });
        } catch (_) {
            return Promise.resolve(KIND_PLATFORM);
        }
        if (!request || typeof request.then !== 'function') {
            return Promise.resolve(KIND_PLATFORM);
        }
        return request.then(
            function (response) {
                var contentType = '';
                try {
                    contentType = (response && response.headers && response.headers.get('content-type')) || '';
                } catch (_) {
                    contentType = '';
                }
                return String(contentType).toLowerCase().indexOf('html') >= 0 ? KIND_AUTH : KIND_PLATFORM;
            },
            function () {
                return KIND_PLATFORM;
            });
    }

    // Styling is inlined rather than taken from app.css on purpose: the stylesheet itself may be
    // the asset the proxy intercepted, so a class-only panel could render as unstyled text on the
    // exact failure this exists to explain.
    var PANEL_STYLE = 'display:flex;flex-direction:column;align-items:center;justify-content:center;'
        + 'min-height:100vh;gap:1rem;padding:2rem;box-sizing:border-box;text-align:center;'
        + 'font-family:system-ui,-apple-system,sans-serif;background:#0a1628;color:#d0dff0;';
    var TITLE_STYLE = 'margin:0;font-size:1.375rem;font-weight:600;color:#f2f7ff;';
    var GUIDANCE_STYLE = 'margin:0;max-width:44rem;line-height:1.5;';
    var DETAIL_STYLE = 'max-width:44rem;width:100%;overflow-x:auto;text-align:left;padding:0.75rem;'
        + 'border-radius:6px;background:#0f2138;color:#9fb6cf;font-size:0.8125rem;white-space:pre-wrap;';
    var ACTIONS_STYLE = 'display:flex;gap:0.75rem;flex-wrap:wrap;justify-content:center;';
    var ACTION_STYLE = 'display:inline-block;padding:0.5rem 1.125rem;border-radius:6px;border:1px solid #3c5b80;'
        + 'background:#16324f;color:#e8f1ff;font:inherit;text-decoration:none;cursor:pointer;';

    function buildPanel(kind, message) {
        var root = escapeHtml(originRoot());
        var detail = escapeHtml(message);

        var headline;
        var guidance;
        var action;

        if (kind === KIND_AUTH) {
            headline = 'Sign-in required';
            guidance =
                'A sign-in page was returned in place of an application file. Your session with the '
                + 'authenticating proxy in front of BotNexus has most likely expired. This is not a '
                + 'BotNexus fault -- sign in again and reload.';
            action =
                '<a class="boot-failure-action" data-testid="boot-failure-reauth" style="' + ACTION_STYLE
                + '" href="' + root + '">Sign in again</a>';
        } else {
            headline = 'BotNexus could not start';
            guidance =
                'The application failed to load one of its files. If this persists, check that the '
                + 'gateway is running and reachable, then reload.';
            action = '';
        }

        return ''
            + '<div class="boot-failure" data-testid="boot-failure-panel" '
            + 'data-boot-failure-kind="boot-failure-kind-' + escapeHtml(kind) + '" role="alert" '
            + 'style="' + PANEL_STYLE + '">'
            + '<h1 class="boot-failure-title" style="' + TITLE_STYLE + '">' + escapeHtml(headline) + '</h1>'
            + '<p class="boot-failure-guidance" style="' + GUIDANCE_STYLE + '">' + escapeHtml(guidance) + '</p>'
            + '<pre class="boot-failure-detail" data-testid="boot-failure-detail" style="' + DETAIL_STYLE
            + '">' + detail + '</pre>'
            + '<div class="boot-failure-actions" style="' + ACTIONS_STYLE + '">'
            + action
            + '<button type="button" class="boot-failure-action" data-testid="boot-failure-reload" '
            + 'style="' + ACTION_STYLE + '">Reload</button>'
            + '</div>'
            + '</div>';
    }

    function renderPanel(kind, message) {
        var app;
        try {
            app = document.getElementById('app');
        } catch (_) {
            return;
        }
        if (!app) {
            return;
        }

        // Replacing #app wholesale is what removes the static .loading-screen. Leaving the spinner
        // beside an error panel would be a worse outcome than either alone.
        app.innerHTML = buildPanel(kind, message);

        try {
            var reload = app.querySelector && app.querySelector('[data-testid="boot-failure-reload"]');
            if (reload && reload.addEventListener) {
                reload.addEventListener('click', function () {
                    try { window.location.reload(); } catch (_) { /* nothing left to do */ }
                });
            }
        } catch (_) {
            /* the anchor and a manual refresh still work */
        }
    }

    function defaultReport(payload) {
        // Reuse the errorReporting.js seam so `Channel error reported` carries the CLASSIFIED
        // cause instead of the erased TypeError. Absent (or throwing) reporting must never stop
        // the panel from rendering.
        try {
            if (window.BotNexusErrorReporting && window.BotNexusErrorReporting.report) {
                window.BotNexusErrorReporting.report(payload);
                return;
            }
        } catch (_) {
            /* fall through */
        }
    }

    /// Starts Blazor with a rejection handler attached, so a boot failure can never become an
    /// unhandled rejection. Always resolves: the caller has no recovery to perform.
    function startBlazor(options) {
        var opts = options || {};
        var blazorStart = opts.blazorStart || function () {
            return window.Blazor.start();
        };
        var fetchFn = opts.fetchFn || function (url, init) { return window.fetch(url, init); };
        var reportFn = opts.reportFn || defaultReport;

        var started;
        try {
            started = blazorStart();
        } catch (err) {
            started = Promise.reject(err);
        }
        if (!started || typeof started.then !== 'function') {
            started = Promise.resolve(started);
        }

        return started.then(
            function () { /* Blazor owns the DOM from here; leave the loading screen to it. */ },
            function (err) {
                var message = errorText(err);
                return classify(fetchFn).then(function (kind) {
                    renderPanel(kind, message);
                    try {
                        reportFn({
                            message: 'Blazor boot failure (' + kind + '): ' + message,
                            stackTrace: (err && err.stack) ? String(err.stack) : null,
                            url: (window.location && window.location.href) || null
                        });
                    } catch (_) {
                        /* reporting is best-effort */
                    }
                });
            });
    }

    window.BotNexusBoot = window.BotNexusBoot || {};
    window.BotNexusBoot.startBlazor = startBlazor;
})();
