// appResume.js -- Page Visibility API integration for BotNexus mobile app.
// Detects when the app returns to foreground after being backgrounded (e.g. PWA home screen mode).

window.BotNexusAppResume = {
    /**
     * Registers a visibility change listener that calls OnAppResumed on the
     * provided Blazor .NET object reference when the page becomes visible.
     * @param {DotNetObjectReference} dotNetRef - Reference to the Blazor component.
     */
    initialize: function (dotNetRef) {
        document.addEventListener('visibilitychange', function () {
            if (document.visibilityState === 'visible') {
                dotNetRef.invokeMethodAsync('OnAppResumed').catch(function (err) {
                    console.warn('[BotNexus] OnAppResumed failed:', err);
                });
            }
        });
    }
};
