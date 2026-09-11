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
                return;
            }
            // #2918 prompt-history recall. The caret edge is measured EXACTLY ONCE per keystroke,
            // here, synchronously, while the DOM selection still reflects the key that was pressed.
            // The measurement is latched onto the element and handed to C# verbatim by
            // caretLinePosition below, so the decision that suppressed (or allowed) the native
            // caret movement is necessarily the same decision the recall logic acts on. Measuring
            // a second time inside caretLinePosition would re-read the selection after the interop
            // round trip and could disagree with the preventDefault already applied.
            if (e.key === 'ArrowUp' || e.key === 'ArrowDown') {
                var edge = measureCaretEdge(element);
                element._caretEdgeLatch = edge;
                if (e.altKey || (e.key === 'ArrowUp' && edge.onFirstLine) || (e.key === 'ArrowDown' && edge.onLastLine)) {
                    e.preventDefault();
                }
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
    },

    /**
     * #2918: hands C# the caret-edge measurement latched by the keydown handler above -- the
     * SINGLE authority for whether the caret sits on the first/last logical line. The latch is
     * consumed (cleared) on read so a stale measurement can never serve a later probe. If no
     * latch exists (no keydown ran, e.g. programmatic invocation) the edge is measured now.
     * Read-only with respect to the textarea's value and selection.
     */
    caretLinePosition: function (element) {
        if (!element) return { onFirstLine: true, onLastLine: true };
        var latched = element._caretEdgeLatch;
        if (latched) {
            element._caretEdgeLatch = null;
            return latched;
        }
        return measureCaretEdge(element);
    },

    /**
     * #2918: places the caret at the end of the textarea and focuses it, so a recalled prompt
     * leaves the caret somewhere predictable rather than mid-text.
     */
    moveCaretToEnd: function (element) {
        if (!element) return;
        try {
            element.focus();
            var len = (element.value || '').length;
            element.setSelectionRange(len, len);
        } catch (e) { /* element detached */ }
    }
};

/**
 * #2918: the one and only caret-edge test. Both the keydown suppression decision and the value
 * reported to C# derive from this function, so they cannot drift apart.
 */
function measureCaretEdge(element) {
    var value = element.value || '';
    var start = typeof element.selectionStart === 'number' ? element.selectionStart : 0;
    var end = typeof element.selectionEnd === 'number' ? element.selectionEnd : start;
    return {
        onFirstLine: value.lastIndexOf('\n', start - 1) === -1,
        onLastLine: value.indexOf('\n', end) === -1
    };
}

window.portalPrefs = {
    load: function (key) { return localStorage.getItem(key); },
    save: function (key, value) { localStorage.setItem(key, value); },

    // Dark is the default and lives on :root, so it is expressed by the ABSENCE of the attribute
    // rather than by data-theme="dark". That keeps the default path free of any attribute the
    // pre-first-paint script would also have to write.
    applyTheme: function (theme) {
        if (theme === 'light') {
            document.documentElement.setAttribute('data-theme', 'light');
        } else {
            document.documentElement.removeAttribute('data-theme');
        }
    }
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
