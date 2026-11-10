// Blazor owns selection/focus. Cancel only menu-navigation keys synchronously so native
// scrolling does not race its asynchronous keyboard handler. Tab and native button activation stay native.
export function initialize(trigger, menu) {
    if (!trigger.dataset.focusedKeyboard) {
        trigger.addEventListener('keydown', event => {
            if (event.key === 'ArrowDown' || event.key === 'ArrowUp') event.preventDefault();
        });
        trigger.dataset.focusedKeyboard = 'true';
    }
    if (menu && !menu.dataset.focusedKeyboard) {
        menu.addEventListener('keydown', event => {
            if (['ArrowDown', 'ArrowUp', 'Home', 'End', 'Escape'].includes(event.key)) event.preventDefault();
        });
        menu.dataset.focusedKeyboard = 'true';
    }
}
