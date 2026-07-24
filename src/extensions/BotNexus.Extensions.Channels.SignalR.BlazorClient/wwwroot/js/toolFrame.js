// Tool iframe host watchdog for the portal route /tools/{id} (#2234).
//
// Sites that send X-Frame-Options: DENY / SAMEORIGIN or a CSP frame-ancestors directive that
// excludes us are blocked by the browser BEFORE any content paints. The browser does NOT fire a
// usable 'load' event we can distinguish from a successful load, and cross-origin access to the
// frame's document is forbidden, so there is no synchronous way to detect the refusal.
//
// The pragmatic signal is time: an embeddable site raises 'load' quickly, so if no load promotes
// the component out of its "framing" state within the timeout, we treat it as refused and the
// Blazor component swaps in the "open in new tab" fallback. The component's @onload handler wins
// the race for embeddable sites and simply leaves this timer to expire harmlessly.
window.toolFrame = window.toolFrame || {
    watch: function (dotNetRef, timeoutMs) {
        setTimeout(function () {
            try {
                // MarkRefused is a no-op on the .NET side unless still in the framing state,
                // so an already-loaded embeddable frame is unaffected by this late call.
                dotNetRef.invokeMethodAsync('MarkRefused');
            } catch (e) {
                // Circuit disconnected or component disposed - nothing to do.
            }
        }, timeoutMs);
    }
};
