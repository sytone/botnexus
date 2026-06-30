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
        // Immediate attempt
        element.scrollTop = element.scrollHeight;
        // rAF attempt — catches most Blazor DOM mutations
        requestAnimationFrame(function () {
            element.scrollTop = element.scrollHeight;
            // Short backstop for late text deltas
            setTimeout(function () {
                element.scrollTop = element.scrollHeight;
            }, 100);
            // Longer backstop for history loads that arrive after hub connect
            setTimeout(function () {
                element.scrollTop = element.scrollHeight;
            }, 600);
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

    /**
     * Prevents the default Enter key behaviour (newline insertion) on a textarea
     * so that Blazor's onkeydown handler can send the message without a stray newline.
     * Shift+Enter still inserts a newline normally.
     */
    /** Returns true when the viewport matches the mobile breakpoint (≤768px). */
    isMobileView: function () {
        return window.innerWidth <= 768;
    },

    preventEnterSubmit: function (element) {
        if (!element || typeof element.addEventListener !== 'function' || element._preventEnterBound) return;
        element.addEventListener('keydown', function (e) {
            if (e.key === 'Enter' && !e.shiftKey) {
                e.preventDefault();
            }
        });
        element._preventEnterBound = true;
    },

    /**
     * #1691: watches the scroll container and invokes the .NET OnScrolledToTop callback when the
     * user scrolls near the top, so the next older page of history can be fetched and prepended.
     * Idempotent per element (binds once). Uses a small top threshold so the fetch starts just
     * before the very top is reached, giving a smoother infinite-scroll feel.
     */
    observeTopForLoadMore: function (element, dotNetRef) {
        if (!element || typeof element.addEventListener !== 'function' || element._loadMoreBound) return;
        var threshold = 60;
        element.addEventListener('scroll', function () {
            if (element.scrollTop <= threshold) {
                try { dotNetRef.invokeMethodAsync('OnScrolledToTop'); } catch (e) { /* ref disposed */ }
            }
        });
        element._loadMoreBound = true;
    },

    /** #1691: captures scrollHeight before an older page is prepended so the view can be restored. */
    captureScrollHeight: function (element) {
        return element ? element.scrollHeight : 0;
    },

    /**
     * #1691: after older messages are prepended at the top, keep the previously-visible message in
     * place by shifting scrollTop down by the height the prepend added (new height minus the height
     * captured before the prepend). Prevents the viewport from jumping to the top.
     */
    restoreScrollAfterPrepend: function (element, previousHeight) {
        if (!element) return;
        var added = element.scrollHeight - previousHeight;
        if (added > 0) {
            element.scrollTop = element.scrollTop + added;
        }
    }
};
