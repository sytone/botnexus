window.conversationActionMenu = (() => {
    let cleanup = null;

    function close() {
        if (cleanup) cleanup();
        cleanup = null;
    }

    function open(menu, dotnet) {
        close();
        requestAnimationFrame(() => {
            const rect = menu.getBoundingClientRect();
            const margin = 8;
            const left = Math.max(margin, Math.min(rect.left, window.innerWidth - rect.width - margin));
            const top = Math.max(margin, Math.min(rect.top, window.innerHeight - rect.height - margin));
            menu.style.left = `${left}px`;
            menu.style.top = `${top}px`;
        });
        const dismiss = event => {
            if (event.type === 'pointerdown' && menu.contains(event.target)) return;
            dotnet.invokeMethodAsync('DismissFromBrowser', event.type);
        };
        document.addEventListener('pointerdown', dismiss, true);
        window.addEventListener('scroll', dismiss, true);
        window.addEventListener('resize', dismiss, true);
        cleanup = () => {
            document.removeEventListener('pointerdown', dismiss, true);
            window.removeEventListener('scroll', dismiss, true);
            window.removeEventListener('resize', dismiss, true);
        };
    }

    function focus(element) { element?.focus(); }
    function focusItem(menu, index) { menu?.querySelectorAll('[role="menuitem"]')[index]?.focus(); }
    return { open, close, focus, focusItem };
})();
