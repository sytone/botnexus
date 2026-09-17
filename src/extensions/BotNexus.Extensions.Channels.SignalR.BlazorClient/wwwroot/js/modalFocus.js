// Own keyboard focus for an aria-modal dialog. The dialog element itself is the fallback focus
// target, while ordinary controls remain the Tab stops. One active record per dialog prevents
// duplicate handlers if a caller opens the same component twice.
(function () {
    'use strict';

    var active = new WeakMap();
    var focusableSelector = [
        'a[href]',
        'button:not([disabled])',
        'input:not([disabled]):not([type="hidden"])',
        'select:not([disabled])',
        'textarea:not([disabled])',
        '[tabindex]:not([tabindex="-1"])'
    ].join(',');

    function visibleFocusable(dialog) {
        return Array.prototype.filter.call(dialog.querySelectorAll(focusableSelector), function (element) {
            return !element.hidden && element.getAttribute('aria-hidden') !== 'true'
                && (element.offsetWidth > 0 || element.offsetHeight > 0 || element.getClientRects().length > 0);
        });
    }

    function activate(dialog, opener) {
        if (!dialog) return;

        deactivate(dialog, false);

        var record = {
            opener: opener && opener.isConnected ? opener : document.activeElement,
            keydown: null,
            focusin: null
        };

        record.keydown = function (event) {
            if (event.key !== 'Tab') return;

            var focusable = visibleFocusable(dialog);
            if (focusable.length === 0) {
                event.preventDefault();
                dialog.focus();
                return;
            }

            var first = focusable[0];
            var last = focusable[focusable.length - 1];
            if (event.shiftKey && (document.activeElement === first || document.activeElement === dialog)) {
                event.preventDefault();
                last.focus();
            } else if (!event.shiftKey && document.activeElement === last) {
                event.preventDefault();
                first.focus();
            }
        };

        record.focusin = function (event) {
            if (!dialog.contains(event.target)) {
                var focusable = visibleFocusable(dialog);
                (focusable[0] || dialog).focus();
            }
        };

        dialog.addEventListener('keydown', record.keydown);
        document.addEventListener('focusin', record.focusin);
        active.set(dialog, record);
        dialog.focus();
    }

    function deactivate(dialog, restoreFocus) {
        if (!dialog) return;

        var record = active.get(dialog);
        if (!record) return;

        dialog.removeEventListener('keydown', record.keydown);
        document.removeEventListener('focusin', record.focusin);
        active.delete(dialog);

        if (restoreFocus && record.opener && record.opener.isConnected) {
            record.opener.focus();
        }
    }

    window.BotNexus = window.BotNexus || {};
    window.BotNexus.modalFocus = { activate: activate, deactivate: deactivate };
})();
