// BotNexus Blazor Client — Chat scroll & input helpers
window.chatScroll = {
    /**
     * Scrolls to bottom only if the user is already near the bottom.
     * Uses a larger threshold during streaming (200px) so the viewport keeps up
     * with rapidly growing content, and a tighter threshold (100px) otherwise.
     * Preserves scroll position when the user has scrolled up to read history.
     */
    scrollToBottom: function (element, isStreaming) {
        if (!element) return;
        var threshold = isStreaming ? 200 : 100;
        var isNearBottom = element.scrollHeight - element.scrollTop - element.clientHeight < threshold;
        if (isNearBottom) {
            element.scrollTop = element.scrollHeight;
        }
    },

    /** Force-scrolls to bottom regardless of current position. Defers to next frame
     *  so the element is visible (hidden panels have scrollHeight=0). */
    forceScrollToBottom: function (element) {
        if (!element) return;
        requestAnimationFrame(function () {
            element.scrollTop = element.scrollHeight;
            // Backstop: re-scroll after a short delay to catch any late DOM mutations
            setTimeout(function () {
                element.scrollTop = element.scrollHeight;
            }, 50);
        });
    },

    /** Finds the currently visible chat panel and scrolls it to the bottom.
     *  Uses setTimeout to ensure Blazor has finished its DOM update cycle. */
    scrollActiveToBottom: function () {
        setTimeout(function () {
            var active = document.querySelector('.chat-panel-wrapper.active .messages-container');
            if (active) active.scrollTop = active.scrollHeight;
        }, 100);
    },

    /** Returns true when the viewport matches the mobile breakpoint (≤768px). */
    isMobileView: function () {
        return window.innerWidth <= 768;
    },

    /** Auto-resizes a textarea to fit its content, capped at maxRows rows. */
    autoResizeTextarea: function (element, maxRows) {
        if (!element) return;
        element.style.height = 'auto';
        var lineHeight = parseInt(getComputedStyle(element).lineHeight) || 20;
        var maxHeight = lineHeight * maxRows;
        element.style.height = Math.min(element.scrollHeight, maxHeight) + 'px';
        element.style.overflowY = element.scrollHeight > maxHeight ? 'auto' : 'hidden';
    },

    /** Resets a textarea height to its natural (CSS) default. */
    resetTextareaHeight: function (element) {
        if (element) { element.style.height = ''; element.style.overflowY = ''; }
    },

    /**
     * Prevents the default Enter key behaviour (newline insertion) on a textarea
     * so that Blazor's onkeydown handler can send the message without a stray newline.
     * Shift+Enter still inserts a newline normally.
     */
    preventEnterSubmit: function (element) {
        if (!element || typeof element.addEventListener !== 'function' || element._preventEnterBound) return;
        element.addEventListener('keydown', function (e) {
            if (e.key === 'Enter' && !e.shiftKey) {
                e.preventDefault();
            }
        });
        element._preventEnterBound = true;
    }
};

window.portalPrefs = {
    load: function (key) { return localStorage.getItem(key); },
    save: function (key, value) { localStorage.setItem(key, value); }
};
