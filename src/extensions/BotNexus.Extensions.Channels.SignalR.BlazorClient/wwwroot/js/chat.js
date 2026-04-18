// BotNexus Blazor Client — Chat scroll & input helpers
window.chatScroll = {
    /**
     * Scrolls to bottom only if the user is already near the bottom (within threshold).
     * This preserves scroll position when the user has scrolled up to read history.
     */
    scrollToBottom: function (element) {
        if (!element) return;
        var threshold = 100;
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
        });
    },

    /**
     * Prevents the default Enter key behaviour (newline insertion) on a textarea
     * so that Blazor's onkeydown handler can send the message without a stray newline.
     * Shift+Enter still inserts a newline normally.
     */
    preventEnterSubmit: function (element) {
        if (!element || element._preventEnterBound) return;
        element.addEventListener('keydown', function (e) {
            if (e.key === 'Enter' && !e.shiftKey) {
                e.preventDefault();
            }
        });
        element._preventEnterBound = true;
    }
};
