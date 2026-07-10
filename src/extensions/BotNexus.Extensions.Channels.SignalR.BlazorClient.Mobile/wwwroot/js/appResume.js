// appResume.js -- Page Lifecycle API integration for the BotNexus mobile PWA.
//
// Detects the full background/foreground lifecycle so the app never shows a frozen
// pre-background snapshot and always drives a liveness-verified hub reset on return.
//
// Events handled (#1841, PBI4 of #1836):
//   - visibilitychange (-> visible) : foreground return; resume + repaint.
//   - pagehide                       : app is being hidden/unloaded; mark state stale.
//   - freeze                         : browser froze the page (bfcache/Page Lifecycle); mark stale.
//   - resume                         : browser un-froze the page; force a stale-state repaint.
//
// All of these funnel into a SINGLE resume path -- the OnAppResumed .NET callback --
// which (via PBI1's HubResumeCoordinator / IPortalLoadService.ResumeAsync) performs the
// liveness-verified hub reset. There is deliberately no second .NET resume entry point so
// resume handling is not duplicated.

window.BotNexusAppResume = {
    // When true, the app was backgrounded/frozen and its rendered snapshot may be stale.
    // Set on freeze/pagehide; consumed (and cleared) on the next foreground resume so the
    // resume always forces a repaint (and a liveness-verified hub reset) rather than trusting
    // a possibly-frozen pre-background view.
    _stale: false,

    /**
     * Registers the full set of Page Lifecycle listeners. When the app returns to the
     * foreground (visibilitychange -> visible, or resume) it calls OnAppResumed on the
     * provided Blazor .NET object reference, forcing a stale-state repaint. Background
     * transitions (freeze, pagehide) mark the state potentially stale.
     * @param {DotNetObjectReference} dotNetRef - Reference to the Blazor component.
     */
    initialize: function (dotNetRef) {
        var self = this;

        // Drives the single resume path: OnAppResumed runs ResumeAsync (PBI1 liveness-verified
        // hub reset) and repaints. Clears the stale flag once the repaint has been requested.
        var resume = function () {
            self._stale = false;
            dotNetRef.invokeMethodAsync('OnAppResumed').catch(function (err) {
                console.warn('[BotNexus] OnAppResumed failed:', err);
            });
        };

        // Marks the snapshot as potentially stale so the next resume forces a repaint.
        var markStale = function () {
            self._stale = true;
        };

        // Foreground return via the Page Visibility API.
        document.addEventListener('visibilitychange', function () {
            if (document.visibilityState === 'visible') {
                resume();
            } else {
                // hidden: the app is going to the background -- its view may be stale on return.
                markStale();
            }
        });

        // pagehide fires when the page is being hidden/unloaded (including entering the
        // back/forward cache). Treat it as a background transition and mark state stale.
        window.addEventListener('pagehide', function () {
            markStale();
        });

        // freeze fires when the browser freezes the page (Page Lifecycle API / bfcache).
        // The rendered snapshot is now frozen; mark it stale so resume repaints.
        document.addEventListener('freeze', function () {
            markStale();
        });

        // resume fires when the browser un-freezes a previously frozen page. Force a
        // stale-state repaint so the user never sees the frozen pre-background snapshot.
        document.addEventListener('resume', function () {
            resume();
        });
    }
};
