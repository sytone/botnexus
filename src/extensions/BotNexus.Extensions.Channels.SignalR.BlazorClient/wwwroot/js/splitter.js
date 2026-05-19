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

        var containerWidth = container.getBoundingClientRect().width;

        // Restore persisted width, else use default.
        // defaultFraction allows preserving legacy proportional defaults
        // (for example 33% capped by a px value) on first load.
        var defaultFromFraction = defaultPx;
        if (typeof defaultFraction === 'number' && defaultFraction > 0 && defaultFraction <= 1) {
            defaultFromFraction = Math.min(defaultPx, Math.floor(containerWidth * defaultFraction));
        }

        var savedPx = parseInt(localStorage.getItem(storageKey), 10);
        var initialPx = (!isNaN(savedPx) && savedPx > 0) ? savedPx : defaultFromFraction;
        applyWidth(container, leftPane, initialPx, minPx, maxFraction);

        var dragging = false;
        var startX = 0;
        var startWidth = 0;

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
