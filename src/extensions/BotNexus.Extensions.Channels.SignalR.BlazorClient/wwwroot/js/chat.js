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

window.portalPrefs = {
    load: function (key) { return localStorage.getItem(key); },
    save: function (key, value) { localStorage.setItem(key, value); }
};

window.chatAttachments = {
    readFiles: async function (files) {
        return await Promise.all(Array.from(files).map(function (file) {
            return new Promise(function (resolve, reject) {
                var reader = new FileReader();
                reader.onload = function () {
                    resolve({
                        fileName: file.name || 'clipboard-image.png',
                        mimeType: file.type || 'application/octet-stream',
                        base64Data: String(reader.result).split(',')[1],
                        size: file.size
                    });
                };
                reader.onerror = reject;
                reader.readAsDataURL(file);
            });
        }));
    },

    bindPaste: function (element, dotNetRef) {
        if (!element || element._attachmentPasteBound) return;
        element.addEventListener('paste', async function (event) {
            var clipboardFiles = event.clipboardData ? event.clipboardData.files : [];
            var images = Array.from(clipboardFiles).filter(function (file) {
                return file.type.startsWith('image/');
            });
            if (!images.length) return;

            event.preventDefault();
            var drafts = await window.chatAttachments.readFiles(images);
            await dotNetRef.invokeMethodAsync('OnAttachmentsPasted', drafts);
        });
        element._attachmentPasteBound = true;
    }
};
