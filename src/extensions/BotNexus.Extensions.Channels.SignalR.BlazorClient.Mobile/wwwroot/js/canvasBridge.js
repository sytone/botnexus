// BotNexus Canvas Bridge — parent-side postMessage handler for iframe ↔ state API communication.
// The Blazor CanvasPanel component registers a DotNetObjectReference that this module calls
// when the iframe sends canvas-state messages via postMessage.

window.canvasBridge = {
    /** @type {Map<string, object>} Active listeners keyed by element data-testid or ref */
    _listeners: new Map(),

    /**
     * Registers a postMessage listener for a specific iframe. Called from Blazor via JS interop.
     * @param {HTMLIFrameElement} iframe - The canvas iframe element.
     * @param {object} dotNetRef - DotNetObjectReference for callbacks.
     */
    register: function (iframe, dotNetRef) {
        if (!iframe) return;

        var handler = function (event) {
            // Only accept messages from our canvas iframe
            if (event.source !== iframe.contentWindow) return;
            var data = event.data;
            if (!data || !data.type || !data.type.startsWith('canvas-state-')) return;

            // Route to .NET
            dotNetRef.invokeMethodAsync('HandleCanvasMessage', JSON.stringify(data));
        };

        window.addEventListener('message', handler);
        canvasBridge._listeners.set(iframe, handler);
    },

    /**
     * Removes the postMessage listener for an iframe. Called on dispose.
     * @param {HTMLIFrameElement} iframe - The canvas iframe element.
     */
    unregister: function (iframe) {
        if (!iframe) return;
        var handler = canvasBridge._listeners.get(iframe);
        if (handler) {
            window.removeEventListener('message', handler);
            canvasBridge._listeners.delete(iframe);
        }
    },

    /**
     * Posts a response back to the iframe.
     * @param {HTMLIFrameElement} iframe - The canvas iframe element.
     * @param {object} response - The response payload.
     */
    respond: function (iframe, response) {
        if (!iframe || !iframe.contentWindow) return;
        iframe.contentWindow.postMessage(response, '*');
    }
};
