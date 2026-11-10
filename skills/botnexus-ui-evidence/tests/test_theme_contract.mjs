// Execute the actual producer predicates without Playwright or a browser.
import fs from 'node:fs';
import assert from 'node:assert/strict';
import test from 'node:test';
import vm from 'node:vm';
const source = fs.readFileSync(new URL('../container/capture-sidebar.mjs', import.meta.url), 'utf8');
const waitLine = source.split('\n').find(line => line.includes('page.waitForFunction(expected =>') && line.includes('data-theme'));
const checkLine = source.split('\n').find(line => line.includes('check(`${name}:theme`'));
assert.ok(waitLine && checkLine, 'Both real producer theme checks must be exercised.');
const AsyncFunction = Object.getPrototypeOf(async function () {}).constructor;
for (const [attribute, theme, expected] of [[null, 'dark', true], ['light', 'light', true], ['light', 'dark', false], [null, 'light', false], ['invalid', 'dark', false], ['invalid', 'light', false], ['', 'dark', false]]) {
  test(`actual wait and capture theme checks: attribute=${JSON.stringify(attribute)}, requested=${theme}`, async () => {
    let waited, checked;
    const document = { documentElement: { getAttribute: name => { assert.equal(name, 'data-theme'); return attribute; } } };
    const page = {
      waitForFunction: async (predicate, value) => { waited = vm.runInNewContext(`(${predicate.toString()})(expected)`, { document, expected: value }); },
      locator: selector => { assert.equal(selector, 'html'); return { getAttribute: async name => document.documentElement.getAttribute(name) }; }
    };
    await new AsyncFunction('page', 'theme', waitLine)(page, theme);
    await new AsyncFunction('page', 'theme', 'name', 'check', checkLine)(page, theme, 'fixture', (target, passed) => { checked = passed; });
    assert.equal(waited, expected, 'Initial readiness predicate must match effective theme.');
    assert.equal(checked, expected, 'Capture assertion must match effective theme.');
  });
}
