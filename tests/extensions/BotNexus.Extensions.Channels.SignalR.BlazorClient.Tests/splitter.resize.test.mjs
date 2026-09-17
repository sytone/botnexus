import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';

const splitterPath = process.argv[2];
const observers = [];
const storage = new Map([['sidebar-width', '800']]);
const listeners = new Map();
let containerWidth = 2000;

const leftPane = {
    id: '',
    style: {},
    getBoundingClientRect: () => ({ width: Number.parseInt(leftPane.style.width ?? '0', 10) })
};
const splitter = {
    previousElementSibling: leftPane,
    classList: { add() {}, remove() {} },
    attrs: {},
    setAttribute(name, value) { this.attrs[name] = String(value); },
    addEventListener(type, handler) { listeners.set(type, handler); },
    removeEventListener(type, handler) {
        if (listeners.get(type) === handler) listeners.delete(type);
    }
};
const container = {
    querySelector: selector => selector === '.panel-splitter' ? splitter : null,
    getBoundingClientRect: () => ({ width: containerWidth })
};
const documentListeners = new Map();
const document = {
    body: { style: {} },
    getElementById: id => id === 'app-body' ? container : null,
    addEventListener(type, handler) { documentListeners.set(type, handler); },
    removeEventListener(type, handler) {
        if (documentListeners.get(type) === handler) documentListeners.delete(type);
    }
};
class ResizeObserver {
    constructor(callback) {
        this.callback = callback;
        this.observed = [];
        this.disconnected = false;
        observers.push(this);
    }
    observe(element) { this.observed.push(element); }
    disconnect() { this.disconnected = true; }
    trigger() { this.callback([{ target: container }]); }
}
const localStorage = {
    getItem: key => storage.get(key) ?? null,
    setItem: (key, value) => storage.set(key, value)
};
const window = { ResizeObserver };
const context = vm.createContext({ window, document, localStorage, ResizeObserver, console });
vm.runInContext(fs.readFileSync(splitterPath, 'utf8'), context, { filename: splitterPath });

window.BotNexus.splitter.init('app-body', 'sidebar-width', 240, 180, 0.4);
assert.equal(leftPane.style.width, '800px', 'wide container should restore the preferred width');
assert.equal(observers.length, 1, 'init should register one container observer');
assert.deepEqual(observers[0].observed, [container]);

containerWidth = 1000;
observers[0].trigger();
assert.equal(leftPane.style.width, '400px', 'narrowing should enforce the new 40% maximum');
assert.equal(storage.get('sidebar-width'), '800', 'temporary clamping must preserve the user preference');

containerWidth = 2000;
observers[0].trigger();
assert.equal(leftPane.style.width, '800px', 'expansion should restore the persisted preference');

containerWidth = 300;
observers[0].trigger();
assert.equal(leftPane.style.width, '120px', 'when max is below min, the container-relative maximum must win');

window.BotNexus.splitter.init('app-body', 'sidebar-width', 240, 180, 0.4);
assert.equal(observers.length, 2);
assert.equal(observers[0].disconnected, true, 're-init must disconnect the previous observer');
window.BotNexus.splitter.destroy('app-body');
assert.equal(observers[1].disconnected, true, 'destroy must disconnect the current observer');
