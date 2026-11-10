// Executes only the production setup fragment against a synthetic Playwright boundary.
// Does not import Playwright or start a browser/gateway/container.
import fs from 'node:fs';
import assert from 'node:assert/strict';
import test from 'node:test';

const source = fs.readFileSync(new URL('../container/capture-sidebar.mjs', import.meta.url), 'utf8');
const helper = source.slice(source.indexOf('const openSidebar ='), source.indexOf('\ntry {', source.indexOf('const openSidebar =')));
const start = source.indexOf('      await page.goto(');
const end = source.indexOf("      await page.locator('[data-testid=\"focused-sidebar-toggle\"]')", start);
assert.ok(start > 0 && end > start, 'Actual setup fragment must remain addressable.');
const setup = source.slice(start, end);
const AsyncFunction = Object.getPrototypeOf(async function () {}).constructor;

for (const narrow of [false, true]) {
  for (const initiallyClosed of [false, true]) {
    test(`actual setup: ${narrow ? 'narrow' : 'wide'}, initially ${initiallyClosed ? 'closed' : 'open'}`, async () => {
      let closed = initiallyClosed;
      let attached = false;
      let overlayVisible = narrow && !closed;
      let dismissed = !overlayVisible;
      const events = [];
      const page = {
        goto: async () => { events.push('goto'); },
        locator: selector => {
          const locator = {
            first: () => locator,
            waitFor: async options => {
              events.push(`${selector}:${options.state}`);
              if (selector === '.main-sidebar' && options.state === 'attached') attached = true;
              if (selector.includes('agent-select') && options.state === 'visible') assert.equal(closed, false, 'Required visible selector cannot precede real burger opening.');
              if (selector === '.sidebar-overlay' && options.state === 'hidden') {
                assert.equal(overlayVisible, false);
                dismissed = true;
              }
            },
            evaluate: async callback => {
              assert.equal(attached, true, 'Wait for actual sidebar attachment before inspecting it.');
              return callback({ classList: { contains: () => closed } });
            },
            isVisible: async () => overlayVisible,
            click: async options => {
              events.push(`click:${selector}`);
              assert.equal(options?.force, undefined, 'No forced clicks.');
              if (selector === '.burger-btn') { closed = false; overlayVisible = narrow; dismissed = !narrow; }
              if (selector === '.sidebar-overlay') {
                assert.ok(options.position.x > 260, 'Dismissal point must be exposed beyond the sidebar.');
                overlayVisible = false;
              }
              if (selector.includes('banner-settings-btn')) {
                assert.equal(overlayVisible, false, 'Overlay must not cover settings.');
                assert.equal(dismissed, true, 'Await overlay hidden before settings.');
              }
            }
          };
          return locator;
        }
      };
      const box = async locator => ({ x: 0, y: 0, width: 600, height: 844 });
      await new AsyncFunction('page', 'origin', 'box', `${helper}\n${setup}`)(page, 'http://synthetic.invalid', box);
      assert.ok(events.includes('[data-testid="agent-select"]:visible'), 'Preserve required visibility wait.');
      assert.equal(events.filter(event => event === 'click:.burger-btn').length, initiallyClosed ? 1 : 0);
      assert.ok(events.includes('click:[data-testid="banner-settings-btn"]'));
    });
  }
}
