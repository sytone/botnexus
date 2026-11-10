// Deterministic contract tests against the ACTUAL copied production helper, not a reimplementation.
import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';
import test from 'node:test';
const source = fs.readFileSync(process.argv[2], 'utf8');

function fixture() {
    const log = [], frames = new Map(), cancelled = [], errors = [];
    let reportError;
    const errorReported = new Promise(resolve => { reportError = resolve; });
    let nextFrame = 0;
    class Target {
        listeners = [];
        removals = [];
        addEventListener(type, fn, capture = false) { this.listeners.push({ type, fn, capture }); }
        removeEventListener(type, fn, capture = false) {
            assert.ok(this.listeners.some(x => x.type === type && x.fn === fn && x.capture === capture), 'exact removal');
            this.listeners = this.listeners.filter(x => !(x.type === type && x.fn === fn && x.capture === capture));
            this.removals.push({ type, fn, capture });
        }
        dispatch(type, options = {}) {
            const event = { type, target: this, key: '', shiftKey: false, prevented: false, stopped: false,
                preventDefault() { this.prevented = true; log.push('prevent'); },
                stopPropagation() { this.stopped = true; }, ...options };
            for (const x of [...this.listeners]) if (x.type === type) x.fn(event);
            return event;
        }
    }
    const document = new Target(), window = new Target();
    Object.assign(window, { innerWidth: 800, innerHeight: 600 });
    const context = { window, document, console: { error: (...args) => { errors.push(args); reportError(args); } },
        requestAnimationFrame(fn) { frames.set(++nextFrame, fn); return nextFrame; },
        cancelAnimationFrame(id) { cancelled.push(id); } };
    vm.runInNewContext(source, context, { filename: 'conversationActionMenu.js' });
    class Element extends Target {
        isConnected = true; disabled = false; style = {}; children = []; open = false;
        shows = 0; hides = 0; measures = 0; attributes = {};
        contains(element) { return element === this || this.children.includes(element); }
        getAttribute(name) { return this.attributes[name] ?? null; }
        querySelectorAll() { return this.children; }
        matches() { return this.open; }
        showPopover() { if (this.failShow) throw new Error('show failed'); this.open = true; this.shows++; }
        hidePopover() { this.open = false; this.hides++; log.push('hide'); }
        getBoundingClientRect() { this.measures++; return { left: 790, top: 590, width: 120, height: 100 }; }
        focus(options) { document.activeElement = this; log.push(this.name); this.focusOptions = options; }
    }
    function owner(name, revision = 1, existing) {
        const overlay = existing?.overlay ?? new Element(), menu = existing?.menu ?? new Element();
        const trigger = existing?.trigger ?? new Element(); trigger.name = name + '-trigger';
        if (!existing) menu.children = Array.from({ length: 4 }, (_, i) => Object.assign(new Element(), { name: name + '-' + i }));
        const calls = [];
        const dotnet = { invokeMethodAsync(...args) { calls.push(args); return dotnet.reply?.(...args) ?? Promise.resolve(); } };
        return { token: name + ':' + revision, overlay, menu, trigger, calls, dotnet };
    }
    const api = window.conversationActionMenu;
    const open = a => api.open(a.token, a.overlay, a.menu, a.trigger, a.dotnet);
    const close = (a, restore = false) => api.close(a.token, a.overlay, restore);
    return { api, document, window, frames, cancelled, log, errors, errorReported, owner, open, close };
}

for (const [first, second] of [['row', 'section'], ['section', 'row']]) {
    test(`ownership ${first} to ${second}: stale close, RAF, listener, callback and focus are inert`, async () => {
        const f = fixture(), a = f.owner(first), b = f.owner(second);
        let reject;
        a.dotnet.reply = () => new Promise((_, no) => { reject = no; });
        f.open(a);
        const oldFrame = [...f.frames.values()][0];
        const oldEvent = f.document.listeners[0].fn;
        const oldKey = a.menu.listeners[0].fn;
        f.open(b);
        assert.deepEqual(a.calls, [['DismissFromBrowser', 'superseded', a.token]]);
        assert.equal(a.overlay.hides, 1);
        assert.deepEqual(f.cancelled, [1]);
        f.close(a, true); f.close(a, true);
        oldFrame(); oldEvent({ type: 'resize' });
        f.api.focusItem(a.token, a.menu, 0); f.api.focus(a.token, a.trigger);
        assert.equal(a.menu.measures, 0);
        assert.equal(b.overlay.open, true);
        assert.equal(f.document.listeners.length, 1);
        assert.equal(f.window.listeners.length, 2);
        assert.deepEqual(f.log, ['hide']);
        assert.equal(b.overlay.open, true);
        f.close(b, true); f.api.focus(b.token, b.trigger);
        reject(new Error('Cannot find .NET object reference (disposed)'));
        await Promise.resolve(); await Promise.resolve();
        assert.equal(f.errors.length, 0, 'expected disposed-reference rejection is handled');
        const focusCount = f.log.length;
        f.api.focus(a.token, a.trigger);
        oldFrame(); oldEvent({ type: 'scroll' });
        oldKey({ key: 'Escape', preventDefault() { assert.fail('stale key prevention'); }, stopPropagation() {} });
        assert.equal(f.log.length, focusCount, 'A work is inert even after B closes');
        assert.equal(a.calls.length, 1);
        assert.equal(f.document.listeners.length, 0);
        assert.equal(f.window.listeners.length, 0);
    });
}

test('A1/B/A2 reused elements reject all A1 work and allow exactly one A2 dismissal', () => {
    const f = fixture(), a1 = f.owner('A'), b = f.owner('B'), a2 = f.owner('A', 2, a1);
    f.open(a1); const stale = f.document.listeners[0].fn; const raf = [...f.frames.values()][0];
    f.open(b); f.open(a2); f.close(a1); stale({ type: 'resize' }); raf();
    assert.equal(a2.overlay.open, true); assert.equal(a2.menu.measures, 0);
    f.window.dispatch('resize'); f.window.dispatch('scroll'); f.document.dispatch('pointerdown');
    assert.deepEqual(a2.calls, [['DismissFromBrowser', 'resize', a2.token]]);
    assert.equal(a1.calls.length, 1); assert.equal(b.calls.length, 1);
});

test('close before late open, old revision and disconnected elements fail closed without superseding B', () => {
    const f = fixture(), a = f.owner('A'), b = f.owner('B');
    f.close(a); f.open(b); f.open(a);
    const older = f.owner('A', 0, a); f.open(older);
    const detached = f.owner('C'); detached.menu.isConnected = false; f.open(detached);
    f.api.close(); f.api.focus(a.trigger); f.api.focusItem(a.menu, 0);
    assert.equal(a.overlay.shows, 0); assert.equal(detached.overlay.shows, 0);
    assert.equal(b.overlay.open, true); assert.equal(b.calls.length, 0);
});

test('show failure cleans its own partial record/listeners and propagates the actual failure', () => {
    const f = fixture(), a = f.owner('A'), b = f.owner('B'); f.open(a); b.overlay.failShow = true;
    assert.throws(() => f.open(b), /show failed/);
    assert.equal(f.document.listeners.length, 0); assert.equal(f.window.listeners.length, 0);
    assert.equal(a.calls.length, 1); assert.equal(b.calls.length, 0);
    f.open(f.owner('C'));
    f.close(b); assert.equal(f.document.listeners.length, 1);
});

for (const type of ['pointerdown', 'scroll', 'resize']) {
    test(`${type} retires synchronously exactly once; listeners removed at original targets/capture`, async () => {
        const f = fixture(), a = f.owner('A'); let done;
        a.dotnet.reply = () => new Promise(resolve => { done = resolve; });
        f.open(a); a.menu.children[0].focus();
        f.document.dispatch('pointerdown', { target: a.menu.children[0] }); assert.equal(a.calls.length, 0);
        const target = type === 'pointerdown' ? f.document : f.window;
        const listener = target.listeners.find(x => x.type === type).fn;
        target.dispatch(type, { target: {} }); listener({ type, target: {} });
        assert.equal(a.overlay.open, false); assert.equal(a.calls.length, 1);
        assert.deepEqual(a.calls[0], ['DismissFromBrowser', type, a.token]);
        assert.deepEqual(f.document.removals.map(x => [x.type, x.capture]), [['pointerdown', true]]);
        assert.deepEqual(f.window.removals.map(x => [x.type, x.capture]), [['scroll', true], ['resize', true]]);
        assert.equal(a.menu.removals[0].type, 'keydown');
        assert.equal(f.document.activeElement, type === 'pointerdown' ? a.menu.children[0] : a.trigger);
        const count = f.log.length; done(); await Promise.resolve(); assert.equal(f.log.length, count);
    });
}

test('scroll never steals focus from a different surface; pending callback cannot restore it later', async () => {
    const f = fixture(), a = f.owner('A'), b = f.owner('B'); f.open(a); b.trigger.focus();
    f.window.dispatch('scroll'); await Promise.resolve();
    assert.equal(f.document.activeElement, b.trigger);
    f.api.focus(a.token, a.trigger); assert.equal(f.document.activeElement, b.trigger);
});

test('guarded RAF retains viewport clamp and cannot measure disconnected menus', () => {
    const f = fixture(), a = f.owner('A'); f.open(a); [...f.frames.values()][0]();
    assert.equal(a.menu.style.left, '672px'); assert.equal(a.menu.style.top, '492px');
    const b = f.owner('B'); f.open(b); b.menu.isConnected = false; [...f.frames.values()][1]();
    assert.equal(b.menu.measures, 0);
});

test('navigation prevents defaults synchronously, wraps using real focus, and skips disabled items', () => {
    const f = fixture(), a = f.owner('A'); a.menu.children[1].disabled = true;
    a.menu.children[2].attributes['aria-disabled'] = 'true'; f.open(a);
    a.menu.children[0].focus();
    for (const [key, index] of [['ArrowDown', 3], ['ArrowDown', 0], ['ArrowUp', 3], ['Home', 0], ['End', 3]]) {
        const event = a.menu.dispatch('keydown', { key });
        assert.equal(event.prevented, true); assert.equal(event.stopped, true);
        assert.equal(f.document.activeElement, a.menu.children[index]);
        assert.equal(f.log.at(-2), 'prevent');
    }
    assert.equal(a.calls.length, 0);
});

test('all-disabled navigation has no focus/modulo error; native Enter/Space never activate through JS', () => {
    const f = fixture(), a = f.owner('A'); a.menu.children.forEach(x => { x.disabled = true; }); f.open(a);
    f.api.focusItem(a.token, a.menu, 0);
    for (const key of ['ArrowDown', 'ArrowUp', 'Home', 'End']) assert.equal(a.menu.dispatch('keydown', { key }).prevented, true);
    for (const key of ['Enter', ' ']) assert.equal(a.menu.dispatch('keydown', { key }).prevented, false);
    assert.equal(f.document.activeElement, undefined); assert.equal(a.calls.length, 0);
});

test('Escape restores only matching connected enabled trigger, once', () => {
    const f = fixture(), a = f.owner('A'); f.open(a);
    const event = a.menu.dispatch('keydown', { key: 'Escape' });
    assert.equal(event.prevented, true); assert.equal(event.stopped, true);
    assert.deepEqual(a.calls, [['DismissFromBrowser', 'Escape', a.token]]);
    assert.equal(f.document.activeElement, a.trigger);
    const count = f.log.length; f.api.focus(a.token, a.trigger); assert.equal(f.log.length, count);
});

for (const shiftKey of [false, true]) {
    test(`Tab shift=${shiftKey} focuses trigger before hide, leaves native traversal unprevented, never late-focuses`, async () => {
        const f = fixture(), a = f.owner('A'); f.open(a); a.menu.children[0].focus(); f.log.length = 0;
        const event = a.menu.dispatch('keydown', { key: 'Tab', shiftKey });
        assert.equal(event.prevented, false); assert.equal(a.overlay.open, false);
        assert.deepEqual(f.log, ['A-trigger', 'hide']);
        assert.deepEqual(a.calls, [['DismissFromBrowser', 'Tab', a.token]]);
        f.api.focus(a.token, a.trigger); await Promise.resolve(); assert.deepEqual(f.log, ['A-trigger', 'hide']);
    });
}

test('trigger recognized open keys prevent synchronously, native Enter/Space untouched; exact unbind blocks delayed rebind', () => {
    const f = fixture(), a = f.owner('A'); f.api.bindTrigger(a.trigger, a.dotnet, false);
    for (const [key, shiftKey, last] of [['ArrowDown', false, false], ['ArrowUp', false, true], ['ContextMenu', false, false], ['F10', true, false]]) {
        const e = a.trigger.dispatch('keydown', { key, shiftKey }); assert.equal(e.prevented, true);
        assert.deepEqual(a.calls.at(-1), ['OpenFromBrowser', last]);
    }
    for (const key of ['Enter', ' ', 'F10']) assert.equal(a.trigger.dispatch('keydown', { key }).prevented, false);
    assert.equal(a.calls.length, 4);
    f.api.unbindTrigger(a.trigger); f.api.bindTrigger(a.trigger, a.dotnet, false);
    assert.equal(a.trigger.listeners.length, 0); assert.equal(a.trigger.removals.length, 1);
});

test('context target binding leaves descendant trigger keys alone and only recognizes context keys', () => {
    const f = fixture(), a = f.owner('A'); f.api.bindTrigger(a.menu, a.dotnet, true);
    assert.equal(a.menu.dispatch('keydown', { key: 'ArrowDown' }).prevented, false);
    assert.equal(a.menu.dispatch('keydown', { key: 'ContextMenu', target: a.trigger }).prevented, false);
    assert.equal(a.menu.dispatch('keydown', { key: 'ContextMenu' }).prevented, true);
    assert.equal(a.calls.length, 1);
});

test('closed restore permit is revoked by disposal; disconnected/disabled trigger never receives focus', () => {
    const f = fixture(), a = f.owner('A'); f.open(a); f.close(a, true); f.close(a, false);
    f.api.focus(a.token, a.trigger); assert.equal(f.document.activeElement, undefined);
    for (const property of ['disabled', 'isConnected']) {
        const b = f.owner(property); f.open(b); f.close(b, true);
        b.trigger[property] = property === 'disabled'; f.api.focus(b.token, b.trigger);
        assert.equal(f.document.activeElement, undefined);
    }
});

test('Tab unavailable-trigger fallback closes without cancelling default or attempting late focus', () => {
    const f = fixture(), a = f.owner('A'); f.open(a); a.trigger.isConnected = false;
    const event = a.menu.dispatch('keydown', { key: 'Tab' });
    assert.equal(event.prevented, false); assert.equal(a.overlay.open, false);
    assert.equal(f.document.activeElement, undefined); assert.equal(a.calls.length, 1);
});

test('unexpected rejected notification remains observable without affecting a new owner', { timeout: 5000 }, async () => {
    const f = fixture(), a = f.owner('A'), b = f.owner('B');
    const fault = new Error('unexpected callback fault');
    a.dotnet.reply = () => Promise.reject(fault);
    f.open(a); f.open(b);
    // The production helper adopts a host-realm promise in its VM realm. Await the actual
    // reporting boundary, not a guessed number of microtasks across those two realms.
    const reported = await f.errorReported;
    assert.equal(f.errors.length, 1);
    assert.deepEqual(reported, ['Conversation menu callback failed', fault]);
    assert.equal(b.overlay.open, true);
    assert.equal(f.document.listeners.length, 1);
    assert.equal(f.window.listeners.length, 2);
    assert.deepEqual(a.calls, [['DismissFromBrowser', 'superseded', a.token]]);
    assert.equal(b.calls.length, 0);
});

test('explicit dismiss button restores trigger while an ordinary outside pointer retains its target focus', () => {
    const f = fixture(), a = f.owner('A'); f.open(a);
    f.document.dispatch('pointerdown', { target: { classList: { contains: name => name === 'conversation-action-menu-dismiss' } } });
    assert.equal(f.document.activeElement, a.trigger);
    assert.deepEqual(a.calls, [['DismissFromBrowser', 'dismiss', a.token]]);
    const b = f.owner('B'), outside = f.owner('outside'); f.open(b); outside.trigger.focus();
    f.document.dispatch('pointerdown', { target: outside.trigger });
    assert.equal(f.document.activeElement, outside.trigger);
    assert.deepEqual(b.calls, [['DismissFromBrowser', 'pointerdown', b.token]]);
});

for (const transition of ['scope', 'scope-aba', 'disabled-aba']) {
    test(`retired section ${transition} revokes permit before queued browser focus executes`, () => {
        const f = fixture(), a = f.owner('section'), outside = f.owner('outside');
        let scope = 'a';
        f.open(a); f.close(a, true); // Close executed in browser; owner already logically closed.
        outside.trigger.focus();
        const deliveries = [() => f.api.focus(a.token, a.trigger)];
        if (transition === 'disabled-aba') {
            a.trigger.disabled = true;
            f.close(a, false); // Parameter invalidation must emit this even when _open is false.
            a.trigger.disabled = false;
        } else {
            scope = 'b'; f.close(a, false);
            if (transition === 'scope-aba') { scope = 'a'; f.close(a, false); }
        }
        assert.equal(a.overlay.open, false);
        assert.equal(f.document.listeners.length, 0);
        const beforeDelivery = f.log.length;
        deliveries.shift()(); // No focus call executes until AFTER scope/disabled invalidation and ABA.
        assert.equal(f.log.length, beforeDelivery);
        assert.equal(f.document.activeElement, outside.trigger);
        assert.equal(a.trigger.disabled, false);
        assert.equal(scope, transition === 'scope' ? 'b' : 'a');
    });
}

test('equivalent section rerender does not revoke a queued single-use focus permit', () => {
    const f = fixture(), a = f.owner('section'); f.open(a); f.close(a, true);
    const deliver = () => f.api.focus(a.token, a.trigger);
    // Equivalent label/delegate updates emit no invalidating close.
    deliver(); assert.equal(f.document.activeElement, a.trigger);
    const count = f.log.length; deliver(); assert.equal(f.log.length, count);
});
