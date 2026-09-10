// BotNexus Blazor Client — Resizable panel splitter
// Supports any two-pane flex layout.
// Usage: BotNexus.splitter.init(containerId, storageKey, defaultPx, minPx, maxFraction, defaultFraction)
window.BotNexus = window.BotNexus || {};
window.BotNexus.splitter = (function () {
    'use strict';

    var _instances = {};

    function init(containerId, storageKey, defaultPx, minPx, maxFraction, defaultFraction) {
        var container = document.getElementById(containerId);
        if (!container) return;

        var splitter = container.querySelector('.panel-splitter');
        if (!splitter) return;

        var leftPane = splitter.previousElementSibling;
        if (!leftPane) return;

        var dragging = false;
        var startX = 0;
        var startWidth = 0;

        // A width the USER chose. While this is absent the pane follows the proportional default,
        // and once it is present the pane is theirs and is only ever re-clamped, never re-derived.
        var savedPx = parseInt(localStorage.getItem(storageKey), 10);
        var userSized = !isNaN(savedPx) && savedPx > 0;

        // The proportional default is a function of the container width, so it can only be
        // computed once the container HAS one.
        //
        // This is where the skills tree lost its labels. init runs from OnAfterRenderAsync, which
        // fires before the browser has necessarily laid the panel out, so the container measured
        // 0: floor(0 * 0.33) = 0, which applyWidth clamped up to minPx. The pane was pinned at
        // 120px for the life of the page - wide enough for "b." and "e." and nothing else - and
        // resizing the window did not help, because the width had been baked into an inline style
        // and was never recomputed.
        function defaultWidth() {
            var containerWidth = container.getBoundingClientRect().width;

            if (typeof defaultFraction === 'number' && defaultFraction > 0 && defaultFraction <= 1) {
                return Math.min(defaultPx, Math.floor(containerWidth * defaultFraction));
            }

            return defaultPx;
        }

        function applyDefault() {
            applyWidth(container, leftPane, defaultWidth(), minPx, maxFraction);
        }

        if (userSized) {
            applyWidth(container, leftPane, savedPx, minPx, maxFraction);
        } else {
            applyDefault();
        }

        // Re-run as the container's width becomes known and whenever it changes. Two jobs:
        // resolve the proportional default once there is something to take a fraction OF, and
        // keep a user's stored width inside maxFraction when the window shrinks. Guarded because
        // a zero-width container is exactly the state that caused the original defect - acting on
        // it would re-pin the pane at minPx.
        var observer = null;

        if (typeof ResizeObserver === 'function') {
            observer = new ResizeObserver(function () {
                if (dragging) return;
                if (container.getBoundingClientRect().width <= 0) return;

                if (userSized) {
                    var stored = parseInt(localStorage.getItem(storageKey), 10);
                    if (!isNaN(stored) && stored > 0) {
                        // Re-clamp only; the stored value is left alone so widening the window
                        // restores the width the user picked rather than the squeezed one.
                        applyWidth(container, leftPane, stored, minPx, maxFraction);
                    }
                } else {
                    applyDefault();
                }
            });

            observer.observe(container);
        }


        function onMouseDown(e) {
            if (e.button !== 0) return;
            dragging = true;
            startX = e.clientX;
            startWidth = leftPane.getBoundingClientRect().width;
            document.body.style.cursor = 'col-resize';
            document.body.style.userSelect = 'none';
            splitter.classList.add('dragging');
            e.preventDefault();
        }

        function onMouseMove(e) {
            if (!dragging) return;
            var delta = e.clientX - startX;
            var newPx = Math.round(startWidth + delta);
            newPx = applyWidth(container, leftPane, newPx, minPx, maxFraction);
            localStorage.setItem(storageKey, String(newPx));
            userSized = true;
        }

        function onMouseUp() {
            if (!dragging) return;
            dragging = false;
            document.body.style.cursor = '';
            document.body.style.userSelect = '';
            splitter.classList.remove('dragging');
        }

        // Touch support
        function onTouchStart(e) {
            if (e.touches.length !== 1) return;
            dragging = true;
            startX = e.touches[0].clientX;
            startWidth = leftPane.getBoundingClientRect().width;
            splitter.classList.add('dragging');
        }

        function onTouchMove(e) {
            if (!dragging || e.touches.length !== 1) return;
            var delta = e.touches[0].clientX - startX;
            var newPx = Math.round(startWidth + delta);
            newPx = applyWidth(container, leftPane, newPx, minPx, maxFraction);
            localStorage.setItem(storageKey, String(newPx));
            userSized = true;
            e.preventDefault();
        }

        function onTouchEnd() {
            dragging = false;
            splitter.classList.remove('dragging');
        }

        splitter.addEventListener('mousedown', onMouseDown);
        document.addEventListener('mousemove', onMouseMove);
        document.addEventListener('mouseup', onMouseUp);
        splitter.addEventListener('touchstart', onTouchStart, { passive: true });
        document.addEventListener('touchmove', onTouchMove, { passive: false });
        document.addEventListener('touchend', onTouchEnd);

        // Clean up on re-init for the same container
        if (_instances[containerId]) {
            _instances[containerId]();
        }
        _instances[containerId] = function () {
            if (observer) observer.disconnect();
            splitter.removeEventListener('mousedown', onMouseDown);
            document.removeEventListener('mousemove', onMouseMove);
            document.removeEventListener('mouseup', onMouseUp);
            splitter.removeEventListener('touchstart', onTouchStart);
            document.removeEventListener('touchmove', onTouchMove);
            document.removeEventListener('touchend', onTouchEnd);
        };
    }

    function applyWidth(container, leftPane, desiredPx, minPx, maxFraction) {
        var containerWidth = container.getBoundingClientRect().width;
        var maxPx = Math.floor(containerWidth * maxFraction);
        var clamped = Math.max(minPx, Math.min(desiredPx, maxPx));
        leftPane.style.flex = '0 0 ' + clamped + 'px';
        leftPane.style.width = clamped + 'px';
        return clamped;
    }

    function destroy(containerId) {
        if (_instances[containerId]) {
            _instances[containerId]();
            delete _instances[containerId];
        }
    }

    return { init: init, destroy: destroy };
}());
