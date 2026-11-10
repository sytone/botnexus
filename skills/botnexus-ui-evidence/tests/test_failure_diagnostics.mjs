import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import assert from 'node:assert/strict';
import test from 'node:test';
import { collectFailure, classifyError } from '../container/failure-diagnostics.mjs';

test('private projection never records raw DOM, URL, text, values or error messages', async () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'sidebar-private-fixture-'));
  try {
    const selectorCalls = [];
    const element = {
      tagName: 'BUTTON', disabled: false, scrollTop: 1, scrollHeight: 2000, clientHeight: 500,
      textContent: 'SYNTHETIC_SECRET', value: 'SYNTHETIC_SECRET', outerHTML: '<secret>SYNTHETIC_SECRET</secret>',
      getAttribute: name => name === 'aria-expanded' ? 'false' : 'SYNTHETIC_SECRET',
      matches: () => false,
      getBoundingClientRect: () => ({ x: 1, y: 2, width: 240, height: 900 })
    };
    const oldDocument = globalThis.document, oldWindow = globalThis.window, oldStyle = globalThis.getComputedStyle;
    globalThis.document = { activeElement: element, querySelectorAll: selector => { selectorCalls.push(selector); return [element]; } };
    globalThis.window = {
      __sidebarDiagnosticEvents: { scroll: 2, resize: 0, focus: 1, pointer: 3 },
      __sidebarDiagnosticTrace: [
        { type: 'marker', phase: 'before-click', sequence: 10, target: 'focused-chat-scroll', scrollTop: 2368, ownerScrollTop: 2368, expanded: false, popover: false },
        { type: 'scroll', phase: 'before-click', sequence: 11, target: 'focused-chat-scroll', scrollTop: 2368, ownerScrollTop: 2368, expanded: true, popover: true },
        { type: 'SYNTHETIC_SECRET', phase: 'SYNTHETIC_SECRET', sequence: 'SYNTHETIC_SECRET', target: 'SYNTHETIC_SECRET', scrollTop: 'SYNTHETIC_SECRET' }
      ]
    };
    globalThis.getComputedStyle = () => ({ display: 'block', visibility: 'visible' });
    try {
      const page = {
        evaluate: async callback => callback(),
        locator: selector => ({ screenshot: async options => {
          assert.equal(selector, '.main-sidebar');
          assert.equal(options.timeout, 5000);
          fs.writeFileSync(options.path, 'synthetic image');
        } })
      };
      await collectFailure(page, { stage: 'section-menu', group: 'scheduled', theme: 'dark', width: 600, secret: 'SYNTHETIC_SECRET' }, ['Timeout SYNTHETIC_SECRET', 'Conversation menu callback failed SYNTHETIC_SECRET'], directory);
    } finally {
      globalThis.document = oldDocument; globalThis.window = oldWindow; globalThis.getComputedStyle = oldStyle;
    }
    const bytes = fs.readFileSync(path.join(directory, 'failure.json'), 'utf8');
    assert.equal(bytes.includes('SYNTHETIC_SECRET'), false);
    const packet = JSON.parse(bytes);
    assert.equal(packet.diagnosticOnly, true);
    assert.equal(packet.group, 'scheduled');
    assert.deepEqual(packet.errors, ['timeout', 'menu-callback']);
    assert.equal(packet.snapshot.events.scroll, 2);
    assert.deepEqual(packet.snapshot.recentEvents[0], { type: 'marker', phase: 'before-click', sequence: 10, target: 'focused-chat-scroll', scrollTop: 2368, ownerScrollTop: 2368, expanded: false, popover: false });
    assert.equal(packet.snapshot.recentEvents[1].sequence, 11);
    assert.equal(packet.snapshot.recentEvents[1].target, 'focused-chat-scroll');
    assert.equal(packet.snapshot.recentEvents[2].phase, 'unknown');
    assert.equal(packet.snapshot.recentEvents[2].target, 'other');
    assert.equal(packet.snapshot.recentEvents[2].sequence, null);
    assert.equal(packet.snapshot.recentEvents[2].scrollTop, null);
    assert.equal(selectorCalls.length, 5);
    assert.deepEqual(fs.readdirSync(directory).sort(), ['failure.json', 'failure.png']);
  } finally { fs.rmSync(directory, { recursive: true, force: true }); }
});

test('raw error payloads reduce to bounded categories', () => {
  assert.equal(classifyError('https://secret.invalid?token=secret'), 'other');
  assert.equal(classifyError('disposed secret'), 'disconnected');
});
