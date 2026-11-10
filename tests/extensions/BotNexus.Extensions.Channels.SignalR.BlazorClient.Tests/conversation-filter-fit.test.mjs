import fs from 'node:fs';
import assert from 'node:assert/strict';
import test from 'node:test';
const root = new URL('../../../', import.meta.url);
const css = fs.readFileSync(new URL('src/extensions/BotNexus.Extensions.Channels.SignalR.BlazorClient/wwwroot/css/app.css', root), 'utf8');
const capture = fs.readFileSync(new URL('skills/botnexus-ui-evidence/container/capture-sidebar.mjs', root), 'utf8');
test('focused filters wrap whole intrinsic-width pills without shrinking labels or pointer targets', () => {
  const bar = css.match(/\.focused-chat-scroll \.conversation-filter-bar\s*\{([^}]+)\}/)?.[1];
  const pill = css.match(/\.focused-chat-scroll \.conversation-filter-btn\s*\{([^}]+)\}/)?.[1];
  assert.ok(bar?.includes('flex-wrap: wrap'));
  assert.ok(pill?.includes('flex: 1 0 auto'));
  assert.match(css, /\.conversation-filter-btn\s*\{[^}]*white-space: nowrap/s);
  assert.match(css, /\.conversation-new-btn,\s*\.conversation-filter-btn\s*\{\s*min-height: var\(--hit-pointer\)/);
  assert.equal(/ellipsis|display:\s*none|font-size|padding|min-height/.test(pill), false);
});
test('future browser run measures each actual pill against owner and viewport with hit minimum', () => {
  assert.ok(capture.includes('filter-pill-${index}:fully-contained'));
  assert.ok(capture.includes('filter-pill-${index}:hit-target'));
  assert.ok(capture.includes('filter-pill-${index}:label'));
  assert.ok(capture.includes("['None', 'Today', 'This Week']"));
});
test('actual geometry assertions reject a clipped rounded edge, lost label and undersized target', async () => {
  const start = capture.indexOf('      const filterBar =');
  const end = capture.indexOf("      diagnosticContext.stage = 'journey';", start);
  assert.ok(start > 0 && end > start);
  const AsyncFunction = Object.getPrototypeOf(async function () {}).constructor;
  for (const defect of ['none', 'clipped', 'label', 'target']) {
    const checks = [];
    const pills = ['None', 'Today', 'This Week'].map((label, index) => ({
      bounds: { x: 10, y: 100 + index * 36, width: defect === 'clipped' && index === 2 ? 220 : 195, height: defect === 'target' && index === 2 ? 20 : 32 },
      textContent: async () => defect === 'label' && index === 2 ? 'This W…' : label,
      evaluate: async callback => callback.toString().includes('getComputedStyle') ? 32 : true
    }));
    const page = { locator: selector => selector.includes('filter-bar') ? {
      scrollIntoViewIfNeeded: async () => {}, locator: () => ({ count: async () => 3, nth: index => pills[index] })
    } : { evaluate: async () => ({ x: 0, y: 50, width: 215, height: 600 }) } };
    await new AsyncFunction('page', 'box', 'check', 'theme', 'width', 'height', capture.slice(start, end))(page, async pill => pill.bounds, (name, value) => checks.push({ name, value }), 'dark', 600, 844);
    assert.equal(checks.every(check => check.value), defect === 'none', defect);
  }
});
