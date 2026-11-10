import assert from 'node:assert/strict';
import test from 'node:test';
import fs from 'node:fs';
import vm from 'node:vm';
import { installScrollDiagnostics } from '../container/scroll-diagnostics.mjs';

test('actual init-script records target category, position and monotonic phase/event order without consuming events', () => {
  const listeners = new Map();
  const window = { addEventListener(type, handler, capture) { assert.equal(capture, true); listeners.set(type, handler); } };
  const owner = { scrollTop: 2368, matches: selector => selector === '.focused-chat-scroll' };
  const trigger = { scrollTop: 0, matches: selector => selector.includes('section-actions-trigger') };
  const document = { body: {}, documentElement: {}, scrollingElement: { scrollTop: 0 }, querySelector: selector => selector === '.focused-chat-scroll' ? owner : selector.includes('aria-expanded') || selector.includes('popover-open') ? null : trigger };
  vm.runInNewContext(`(${installScrollDiagnostics.toString()})()`, { window, document });
  window.__sidebarDiagnosticMark('before-scroll-into-view');
  let consumed = false;
  listeners.get('scroll')({ target: owner, preventDefault() { consumed = true; }, stopPropagation() { consumed = true; } });
  window.__sidebarDiagnosticMark('after-scroll-into-view');
  window.__sidebarDiagnosticMark('before-click');
  listeners.get('pointerdown')({ target: trigger });
  listeners.get('focusin')({ target: { scrollTop: 0, matches: selector => selector === '[role="menuitem"]' } });
  listeners.get('scroll')({ target: document });
  window.__sidebarDiagnosticMark('after-click');
  const trace = JSON.parse(JSON.stringify(window.__sidebarDiagnosticTrace));
  assert.deepEqual(trace.map(item => item.sequence), [1, 2, 3, 4, 5, 6, 7, 8]);
  assert.equal(trace[1].target, 'focused-chat-scroll');
  assert.equal(trace[1].scrollTop, 2368);
  assert.equal(trace[5].target, 'menu-item');
  assert.equal(trace[6].target, 'document');
  assert.equal(trace[6].scrollTop, 0);
  assert.equal(trace[6].ownerScrollTop, 2368);
  assert.equal(trace[6].phase, 'before-click');
  assert.equal(consumed, false);
  for (let index = 0; index < 40; index++) listeners.get('resize')({ target: window });
  assert.equal(window.__sidebarDiagnosticTrace.length, 32);
  assert.equal(window.__sidebarDiagnosticTrace.at(-1).sequence, 48);
});

test('actual menu input fragment preserves explicit preparation then one real click and visibility assertion', async () => {
  const source = fs.readFileSync(new URL('../container/capture-sidebar.mjs', import.meta.url), 'utf8');
  const start = source.indexOf('        const menuTrigger =');
  const end = source.indexOf('        builtInMenus &&=', start);
  assert.ok(start > 0 && end > start);
  const fragment = source.slice(start, end);
  const calls = [];
  const group = {
    locator: () => ({ scrollIntoViewIfNeeded: async () => calls.push('scrollIntoView'), click: async () => calls.push('click') }),
    getByRole: role => ({ waitFor: async options => { assert.equal(role, 'menu'); assert.equal(options.state, 'visible'); calls.push('visible'); } })
  };
  const page = { evaluate: async callback => {
    const window = { __sidebarDiagnosticMark: phase => calls.push(phase) };
    vm.runInNewContext(`(${callback.toString()})()`, { window });
  } };
  const AsyncFunction = Object.getPrototypeOf(async function () {}).constructor;
  await new AsyncFunction('page', 'group', fragment)(page, group);
  assert.deepEqual(calls, ['before-scroll-into-view', 'scrollIntoView', 'after-scroll-into-view', 'before-click', 'click', 'after-click', 'visible', 'menu-visible']);
  assert.equal(/setTimeout|waitForTimeout|force:|scrollTop\s*=/.test(fragment), false);
});
