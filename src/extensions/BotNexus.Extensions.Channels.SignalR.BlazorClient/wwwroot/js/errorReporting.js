// errorReporting.js -- reports unrecoverable portal failures to the gateway.
//
// Issue #1481: when Blazor shows the framework "#blazor-error-ui" banner
// ("An error has occurred" / "An unhandled error has occurred"), the failure
// is unrecoverable (dead circuit, WASM crash, unhandled JS interop fault). At
// that point the .NET GlobalErrorBoundary cannot run, so nothing reaches the
// gateway and operators see no message, count, or stack.
//
// This is the only layer that can observe such a failure, so it captures
// window.onerror / unhandledrejection and the #blazor-error-ui banner becoming
// visible, then best-effort POSTs a ChannelErrorReport to the diagnostics API.
// All sends are fire-and-forget and never throw -- the page is already broken.
(function () {
    'use strict';

    // The diagnostics endpoint is rooted at the origin, NOT the Blazor <base href>
    // (which is "/mobile/" on mobile and "/" on desktop). Build an absolute,
    // origin-relative URL so the report lands at /api/diagnostics/channel-error
    // regardless of the app's base path.
    var ENDPOINT;
    try {
        ENDPOINT = new URL('/api/diagnostics/channel-error', window.location.origin).toString();
    } catch (_) {
        ENDPOINT = '/api/diagnostics/channel-error';
    }

    // Avoid flooding the gateway: dedupe identical reports and cap the total.
    var MAX_REPORTS = 20;
    var sent = 0;
    var seen = new Set();

    function report(payload) {
        if (sent >= MAX_REPORTS) {
            return;
        }
        var signature = (payload.message || '') + '|' + (payload.url || '');
        if (seen.has(signature)) {
            return;
        }
        seen.add(signature);
        sent++;

        var body;
        try {
            body = JSON.stringify({
                message: payload.message || 'Unrecoverable portal error',
                stackTrace: payload.stackTrace || null,
                componentStack: null,
                url: payload.url || window.location.href,
                userAgent: navigator.userAgent,
                timestamp: new Date().toISOString(),
                sessionId: null,
                agentId: null
            });
        } catch (_) {
            return;
        }

        // Best-effort. keepalive lets the request survive an unloading/dying page.
        try {
            fetch(ENDPOINT, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: body,
                keepalive: true,
                credentials: 'same-origin'
            }).catch(function () { /* swallow -- page is already broken */ });
        } catch (_) {
            /* swallow */
        }
    }

    // 1) Uncaught synchronous/async errors.
    window.addEventListener('error', function (e) {
        // Resource load errors (img/script) surface here too with no e.error;
        // only report genuine script errors that carry a message.
        if (!e || (!e.error && !e.message)) {
            return;
        }
        report({
            message: e.message || (e.error && e.error.message) || 'Unhandled error',
            stackTrace: e.error && e.error.stack ? String(e.error.stack) : null,
            url: e.filename || window.location.href
        });
    });

    // 2) Unhandled promise rejections (the common WASM/interop failure shape).
    window.addEventListener('unhandledrejection', function (e) {
        var reason = e && e.reason;
        var message = 'Unhandled promise rejection';
        var stack = null;
        if (reason) {
            if (typeof reason === 'string') {
                message = reason;
            } else if (reason.message) {
                message = reason.message;
                stack = reason.stack ? String(reason.stack) : null;
            } else {
                try { message = JSON.stringify(reason); } catch (_) { /* keep default */ }
            }
        }
        report({ message: message, stackTrace: stack, url: window.location.href });
    });

    // 3) The unrecoverable case: #blazor-error-ui becomes visible. This catches
    //    dead-circuit / fatal render failures that never raise a JS error event.
    function watchErrorBanner() {
        var banner = document.getElementById('blazor-error-ui');
        if (!banner) {
            return;
        }
        var observer = new MutationObserver(function () {
            var visible = window.getComputedStyle(banner).display !== 'none';
            if (visible) {
                report({
                    message: 'Blazor unrecoverable error UI displayed (#blazor-error-ui)',
                    stackTrace: null,
                    url: window.location.href
                });
            }
        });
        observer.observe(banner, { attributes: true, attributeFilter: ['style', 'class'] });
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', watchErrorBanner);
    } else {
        watchErrorBanner();
    }
})();
