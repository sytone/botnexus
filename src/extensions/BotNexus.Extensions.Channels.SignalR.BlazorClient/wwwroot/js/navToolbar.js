// Measures how many primary-nav items fit in the toolbar and tells .NET, so the rest can move into
// the overflow menu.
//
// Why a measurer rather than a fixed list: the nav is not a fixed set. Plugins contribute entries
// and the user reorders them, so neither the item count nor their widths are known ahead of time.
//
// Why the widths come from the GHOST row: once an item moves into the overflow menu it is no longer
// in the bar, so its natural width can no longer be read there - and the bar could never work out
// that it would fit again on widening. The ghost row always holds the full set, laid out but not
// painted, which keeps every width measurable at all times.
window.botnexusNavToolbar = {
    observe: function (bar, dotNetRef) {
        if (!bar || !dotNetRef) return null;

        let last = -1;

        const measure = function () {
            const ghost = bar.querySelector('.toolbar-ghost');
            const items = ghost ? Array.from(ghost.children) : [];
            if (items.length === 0) return;

            const styles = getComputedStyle(bar);
            const gap = parseFloat(styles.columnGap || styles.gap || '0') || 0;

            // Reserve room for the More button whenever anything might overflow. Measured from the
            // live button when it is present, else a conservative constant - reserving too little
            // is what makes the last item flicker in and out on a slow drag.
            const moreBtn = bar.querySelector('.toolbar-more-btn');
            const moreWidth = moreBtn ? moreBtn.getBoundingClientRect().width : 96;

            const available = bar.clientWidth - parseFloat(styles.paddingLeft || '0') - parseFloat(styles.paddingRight || '0');

            // First pass: does everything fit with no More button at all?
            let total = 0;
            for (let i = 0; i < items.length; i++) {
                total += items[i].getBoundingClientRect().width + (i > 0 ? gap : 0);
            }
            if (total <= available) {
                if (last !== items.length) {
                    last = items.length;
                    dotNetRef.invokeMethodAsync('SetVisibleNavCount', items.length);
                }
                return;
            }

            // Second pass: fit as many as possible while leaving room for the More button.
            const budget = available - moreWidth - gap;
            let used = 0;
            let fit = 0;
            for (let i = 0; i < items.length; i++) {
                const w = items[i].getBoundingClientRect().width + (i > 0 ? gap : 0);
                if (used + w > budget) break;
                used += w;
                fit++;
            }

            if (last !== fit) {
                last = fit;
                dotNetRef.invokeMethodAsync('SetVisibleNavCount', fit);
            }
        };

        const observer = new ResizeObserver(measure);
        observer.observe(bar);
        measure();

        return {
            remeasure: measure,
            dispose: function () { observer.disconnect(); }
        };
    }
};
