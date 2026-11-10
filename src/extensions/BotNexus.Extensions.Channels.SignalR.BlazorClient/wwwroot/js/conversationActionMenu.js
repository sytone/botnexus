window.conversationActionMenu = (() => {
    let active = null;
    let epoch = 0;
    let focusPermit = null;
    // Element-local watermarks reject close-before-late-open and same-element ABA without
    // retaining detached components in a global token registry.
    const revisions = new WeakMap();
    const triggers = new WeakMap();
    const disposedTriggers = new WeakSet();

    function identity(token) {
        if (typeof token !== 'string') return null;
        const match = /^([^:]+):([1-9]\d*)$/.exec(token);
        if (!match) return null;
        const revision = Number(match[2]);
        return Number.isSafeInteger(revision) ? { owner: match[1], revision } : null;
    }

    function mark(token, overlay) {
        const id = identity(token);
        if (!id || !overlay) return false;
        const previous = revisions.get(overlay);
        if (previous && previous.owner !== id.owner) return false;
        if (!previous || id.revision > previous.revision) revisions.set(overlay, id);
        return true;
    }

    function current(record) {
        return active === record && !record.retired;
    }

    function enabled(element) {
        return element?.isConnected && !element.disabled && element.getAttribute('aria-disabled') !== 'true';
    }

    function reportCallbackError(error) {
        // A reference can be disposed while a notification is in flight. Other failures remain
        // visible, but no rejected callback can run cleanup or become an unhandled rejection.
        if (!/cannot find.*(?:\.NET object|object reference)|disposed|disconnected/i.test(String(error)))
            console.error('Conversation menu callback failed', error);
    }

    function notify(record, reason) {
        if (record.notified) return;
        record.notified = true;
        try {
            Promise.resolve(record.dotnet.invokeMethodAsync('DismissFromBrowser', reason, record.token))
                .catch(reportCallbackError);
        } catch (error) { reportCallbackError(error); }
    }

    function retire(record, restoreFocus) {
        if (!current(record)) return false;
        record.retired = true;
        active = null;
        mark(record.token, record.overlay);
        cancelAnimationFrame(record.frame);
        document.removeEventListener('pointerdown', record.dismiss, true);
        window.removeEventListener('scroll', record.dismiss, true);
        window.removeEventListener('resize', record.dismiss, true);
        record.menu.removeEventListener('keydown', record.keydown, true);
        focusPermit = restoreFocus ? { token: record.token, trigger: record.trigger, epoch } : null;
        if (record.overlay.matches(':popover-open')) record.overlay.hidePopover();
        return true;
    }

    function close(token, overlay, restoreFocus = false) {
        if (!mark(token, overlay)) return;
        if (!restoreFocus && focusPermit?.token === token) focusPermit = null;
        if (active?.token === token && active.overlay === overlay) retire(active, restoreFocus);
    }

    function focus(token, trigger) {
        const permit = focusPermit;
        if (!permit || permit.token !== token || permit.trigger !== trigger || permit.epoch !== epoch || active) return;
        focusPermit = null; // Single use, including a disconnected/disabled trigger.
        if (enabled(trigger)) trigger.focus({ preventScroll: true });
    }

    function items(record) {
        return Array.from(record.menu.querySelectorAll('[role="menuitem"]')).filter(enabled);
    }

    function focusItem(token, menu, index) {
        const record = active;
        if (!record || record.token !== token || record.menu !== menu || !current(record) || !menu.isConnected) return;
        const candidates = items(record);
        const item = index < 0 ? candidates[candidates.length - 1] : candidates[index];
        item?.focus({ preventScroll: true });
    }

    function dismiss(record, reason) {
        if (!current(record)) return;
        const restore = reason === 'Escape' || reason === 'dismiss' ||
            ((reason === 'scroll' || reason === 'resize') && record.menu.contains(document.activeElement));
        if (reason === 'Tab' && enabled(record.trigger)) record.trigger.focus({ preventScroll: true });
        retire(record, restore);
        if (restore) focus(record.token, record.trigger);
        notify(record, reason);
    }

    function open(token, overlay, menu, trigger, dotnet) {
        const id = identity(token);
        if (!id || !overlay?.isConnected || !menu?.isConnected || !enabled(trigger) || !dotnet) return;
        const previous = revisions.get(overlay);
        if (previous && (previous.owner !== id.owner || previous.revision >= id.revision)) return;
        // Supersession retires the old owner before notifying .NET; callback latency/rejection
        // never controls the new record or its listeners.
        if (active) {
            const old = active;
            retire(old, false);
            notify(old, 'superseded');
        }
        ++epoch;
        focusPermit = null;
        mark(token, overlay);
        const record = { token, overlay, menu, trigger, dotnet, retired: false, notified: false, frame: 0 };
        record.dismiss = event => {
            if (!current(record) || (event.type === 'pointerdown' && menu.contains(event.target))) return;
            const reason = event.type === 'pointerdown' && event.target?.classList?.contains('conversation-action-menu-dismiss')
                ? 'dismiss' : event.type;
            dismiss(record, reason);
        };
        record.keydown = event => {
            if (!current(record) || !overlay.isConnected || !menu.isConnected) return;
            if (['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(event.key)) {
                event.preventDefault();
                event.stopPropagation();
                const candidates = items(record);
                if (!candidates.length) return;
                const index = candidates.indexOf(document.activeElement);
                const next = event.key === 'Home' ? 0 : event.key === 'End' ? candidates.length - 1
                    : event.key === 'ArrowUp' ? (index < 0 ? candidates.length - 1 : (index - 1 + candidates.length) % candidates.length)
                    : (index + 1) % candidates.length;
                candidates[next].focus({ preventScroll: true });
            } else if (event.key === 'Escape') {
                event.preventDefault();
                event.stopPropagation();
                dismiss(record, 'Escape');
            } else if (event.key === 'Tab') {
                // Restore the traversal origin BEFORE hiding the menu, then let native
                // Tab/Shift+Tab default handling proceed. Never restore on callback completion.
                dismiss(record, 'Tab');
            }
            // Enter/Space belong exclusively to the focused native button's click default.
        };
        active = record;
        document.addEventListener('pointerdown', record.dismiss, true);
        window.addEventListener('scroll', record.dismiss, true);
        window.addEventListener('resize', record.dismiss, true);
        menu.addEventListener('keydown', record.keydown, true);
        try {
            overlay.showPopover();
            record.frame = requestAnimationFrame(() => {
                if (!current(record) || !overlay.isConnected || !menu.isConnected || !overlay.matches(':popover-open')) return;
                const rect = menu.getBoundingClientRect();
                const margin = 8;
                const left = Math.max(margin, Math.min(rect.left, window.innerWidth - rect.width - margin));
                const top = Math.max(margin, Math.min(rect.top, window.innerHeight - rect.height - margin));
                menu.style.left = `${left}px`;
                menu.style.top = `${top}px`;
            });
        } catch (error) {
            retire(record, false);
            throw error;
        }
    }

    function bindTrigger(trigger, dotnet, contextOnly = false) {
        if (!trigger?.isConnected || disposedTriggers.has(trigger) || triggers.has(trigger)) return;
        const keydown = event => {
            if (!enabled(trigger) || (contextOnly && event.target !== trigger)) return;
            const contextKey = event.key === 'ContextMenu' || (event.shiftKey && event.key === 'F10');
            if (!contextKey && (contextOnly || !['ArrowDown', 'ArrowUp'].includes(event.key))) return;
            event.preventDefault();
            event.stopPropagation();
            try {
                Promise.resolve(dotnet.invokeMethodAsync('OpenFromBrowser', event.key === 'ArrowUp')).catch(reportCallbackError);
            } catch (error) { reportCallbackError(error); }
        };
        triggers.set(trigger, keydown);
        trigger.addEventListener('keydown', keydown, true);
    }

    function unbindTrigger(trigger) {
        if (!trigger) return;
        disposedTriggers.add(trigger); // A delayed bind issued before disposal must not resurrect it.
        const keydown = triggers.get(trigger);
        if (keydown) trigger.removeEventListener('keydown', keydown, true);
        triggers.delete(trigger);
    }

    return { open, close, focus, focusItem, bindTrigger, unbindTrigger };
})();
