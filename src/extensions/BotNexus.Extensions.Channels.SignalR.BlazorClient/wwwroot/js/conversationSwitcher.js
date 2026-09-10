// Anchor the conversation switcher panel to its trigger.
//
// The panel CANNOT be positioned with `position: absolute` inside the chat header. Three of its
// ancestors - .chat-title-heading-row, .chat-title-stack and .chat-header - carry `overflow: hidden`
// so a long conversation title truncates instead of painting beneath the header action group
// (#2141, #2441). Those rules are load-bearing, and an absolutely positioned descendant is clipped
// by every one of them, so the panel rendered with a correct 352x423 box and was shaved to nothing
// by a ~28px tall header.
//
// `position: fixed` escapes all three, because none of those ancestors establishes a containing
// block (no transform, filter or will-change). The trade is that fixed offsets are viewport-relative,
// so the panel has to be told where its trigger is - which is all this file does.
window.botnexusConversationSwitcher = {

    // Cmd/Ctrl-K opens the switcher from anywhere in the portal.
    //
    // The control itself is a caret beside the conversation title - discoverable once you know it is
    // there, invisible if you do not, which is how a search feature ended up feeling absent. This is
    // the shortcut people already try first.
    //
    // Returns a disposer so the caller can unregister on dispose: the listener is on `document` and
    // outlives any single component, so leaving it attached would stack a new handler on every
    // re-render of the owner and keep a disposed .NET reference alive.
    registerShortcut: function (dotNetRef) {
        if (!dotNetRef) return null;

        // Resolved once, not per keypress: the platform does not change mid-session.
        const isMac = /mac|iphone|ipad/i.test(navigator.platform || navigator.userAgent || '');

        const handler = function (e) {
            // Cmd-K on macOS, Ctrl-K everywhere else - and strictly one or the other.
            //
            // Accepting either modifier on every platform (which an earlier revision did, despite a
            // comment claiming otherwise) hijacks Ctrl-K on macOS, where it is the readline
            // "kill to end of line" binding that works in every text field. Because this handler
            // also calls preventDefault, that did not merely add a shortcut - it silently broke a
            // standard editing key in every input in the portal.
            const combo = isMac
                ? (e.metaKey && !e.ctrlKey)
                : (e.ctrlKey && !e.metaKey);
            if (!combo || e.altKey || e.shiftKey) return;
            if ((e.key || '').toLowerCase() !== 'k') return;

            // preventDefault BEFORE the interop call: the browser's own Cmd-K (search-bar focus in
            // some browsers) fires synchronously, and awaiting .NET first would let it win.
            e.preventDefault();
            dotNetRef.invokeMethodAsync('OpenFromShortcut');
        };

        document.addEventListener('keydown', handler);
        return {
            dispose: function () { document.removeEventListener('keydown', handler); }
        };
    },

    // Places `panel` under `trigger`, flipping above it when there is not enough room below and
    // clamping horizontally so a switcher near the right edge stays on screen.
    anchor: function (trigger, panel) {
        if (!trigger || !panel) return;

        const margin = 8;
        const gap = 4;
        const r = trigger.getBoundingClientRect();

        const width = panel.offsetWidth;
        const left = Math.min(Math.max(margin, r.left), Math.max(margin, window.innerWidth - width - margin));

        const height = panel.offsetHeight;
        const below = r.bottom + gap;
        const fitsBelow = below + height <= window.innerHeight - margin;
        const top = fitsBelow ? below : Math.max(margin, r.top - gap - height);

        panel.style.left = left + 'px';
        panel.style.top = top + 'px';
    }
};
