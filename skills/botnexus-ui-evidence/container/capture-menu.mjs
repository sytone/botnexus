import fs from 'node:fs';
import crypto from 'node:crypto';
import playwright from '/app/playwright/node_modules/playwright-core/index.js';
const scenario = JSON.parse(process.env.BOTNEXUS_EVIDENCE_SCENARIO || '{}');
const assertions = [], captures = [], errors = [];
const check = (target, passed, detail = '') => { assertions.push({ target, passed, detail }); console.log(`${passed ? 'PASS' : 'FAIL'} ${target} ${detail}`); };
const browser = await playwright.chromium.launch({ headless: true });
let fatal;
try {
  const context = await browser.newContext({ viewport: { width: 1280, height: 800 }, serviceWorkers: 'block' });
  const page = await context.newPage();
  page.on('pageerror', e => errors.push(e.message));
  const response = await page.request.post('http://127.0.0.1:5000/api/conversations', { data: { agentId: 'evidence-agent', title: 'Synthetic menu evidence 4462' } });
  check('synthetic-conversation-created', response.ok(), `HTTP ${response.status()}`);
  const conversation = await response.json();
  await page.goto(`http://127.0.0.1:5000/chat/evidence-agent/${conversation.conversationId}`, { waitUntil: 'load' });
  await page.locator('#evidence-agent-conversation-panel [data-testid="chat-composer"]').waitFor({ state: 'visible', timeout: 60000 });
  const sidebar = page.locator('.main-sidebar').first();
  if (await sidebar.evaluate(e => e.classList.contains('sidebar-closed'))) await page.locator('.burger-btn').first().click();
  await page.locator('[data-testid="agent-select"]').first().selectOption('evidence-agent');
  const row = page.locator('.conversation-list-item:has(.conversation-list-item-btn.active)').first();
  await row.waitFor({ state: 'visible', timeout: 15000 });
  const menu = page.locator('[data-testid="conversation-action-menu"]');
  const trigger = row.locator('[data-testid="conversation-actions-trigger"]');
  const screenshot = async name => {
    const file = `${name}.png`;
    await page.screenshot({ path: `/evidence-output/${file}`, fullPage: false });
    captures.push({ name, path: file, sha256: crypto.createHash('sha256').update(fs.readFileSync(`/evidence-output/${file}`)).digest('hex') });
  };
  const settle = async () => {
    await menu.waitFor({ state: 'visible', timeout: 5000 });
    await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
  };
  const geometry = async (name, crossing = false) => {
    const result = await page.evaluate(({ crossing }) => {
      const menu = document.querySelector('[data-testid="conversation-action-menu"]');
      const sidebar = document.querySelector('.main-sidebar');
      if (!menu || !sidebar) return null;
      const m = menu.getBoundingClientRect(), s = sidebar.getBoundingClientRect();
      const x = crossing ? Math.min(m.right - 2, s.right + 20) : m.left + m.width / 2;
      const y = m.top + m.height / 2;
      const hit = document.elementFromPoint(x, y);
      return { menu: { x: m.x, y: m.y, width: m.width, height: m.height, right: m.right, bottom: m.bottom }, sidebar: { x: s.x, width: s.width, right: s.right }, crossing: m.left < s.right && m.right > s.right, clamp: m.left >= 7.9 && m.top >= 7.9 && m.right <= innerWidth - 7.9 && m.bottom <= innerHeight - 7.9, hitNonNull: hit !== null, exactMenuHit: hit !== null && hit.closest('[data-testid="conversation-action-menu"]') === menu, hit: hit ? `${hit.tagName}.${hit.className}` : null, point: { x, y } };
    }, { crossing });
    check(`${name}:real-rects`, result !== null, JSON.stringify(result));
    if (crossing) check(`${name}:crosses-sidebar`, result?.crossing === true);
    check(`${name}:viewport-clamp`, result?.clamp === true);
    check(`${name}:non-null-exact-topmost-menu-hit`, result?.hitNonNull === true && result?.exactMenuHit === true, JSON.stringify(result));
    await screenshot(name);
  };
  const dismiss = async (name, action) => {
    await action();
    await menu.waitFor({ state: 'detached', timeout: 5000 });
    check(`${name}:dismissed`, await menu.count() === 0);
  };
  for (const width of [240, 180]) {
    await sidebar.evaluate((e, width) => { e.style.flex = `0 0 ${width}px`; e.style.width = `${width}px`; }, width);
    const s = await sidebar.boundingBox(), r = await row.boundingBox();
    if (!s || !r) throw new Error('Sidebar/row rect missing');
    check(`sidebar-${width}:actual-width`, Math.abs(s.width - width) < 1, JSON.stringify(s));
    const targetBox = await row.locator('.conversation-list-item-btn').boundingBox();
    if (!targetBox) throw new Error('Conversation link rect missing');
    await page.mouse.click(targetBox.x + targetBox.width - 2, targetBox.y + targetBox.height / 2, { button: 'right' });
    await settle();
    await geometry(`sidebar-${width}`, true);
    await dismiss(`sidebar-${width}:outside`, () => page.mouse.click(1100, 700));
  }
  await trigger.click(); await settle();
  check('keyboard:initial-focus', await menu.locator('[role="menuitem"]').first().evaluate(e => document.activeElement === e));
  await page.keyboard.press('ArrowDown');
  check('keyboard:arrow-down', await menu.locator('[role="menuitem"]').nth(1).evaluate(e => document.activeElement === e));
  await page.keyboard.press('ArrowUp');
  check('keyboard:arrow-up', await menu.locator('[role="menuitem"]').first().evaluate(e => document.activeElement === e));
  await page.keyboard.press('End');
  check('keyboard:end', await menu.locator('[role="menuitem"]').last().evaluate(e => document.activeElement === e));
  await page.keyboard.press('Home');
  check('keyboard:home', await menu.locator('[role="menuitem"]').first().evaluate(e => document.activeElement === e));
  await dismiss('keyboard:escape', () => page.keyboard.press('Escape'));
  check('keyboard:escape-restores-trigger-focus', await trigger.evaluate(e => document.activeElement === e));
  await trigger.click(); await settle();
  const pin = menu.locator('[data-action-id="pin"]');
  await pin.focus();
  // Navigate through the component's own active-index state before invoking Enter.
  await page.keyboard.press('Home'); await page.keyboard.press('ArrowDown');
  check('keyboard:pin-selected', await pin.evaluate(e => document.activeElement === e));
  await dismiss('keyboard:enter-action', () => page.keyboard.press('Enter'));
  const pinnedRow = page.locator('[data-testid="conversation-group-pinned"] .conversation-list-item:has(.conversation-list-item-btn.active)');
  await pinnedRow.waitFor({ state: 'visible', timeout: 5000 });
  check('keyboard:pin-action-real-state', await pinnedRow.count() === 1);
  const headerTrigger = page.locator('#evidence-agent-conversation-panel [data-testid="conversation-actions-trigger"]').first();
  await headerTrigger.click(); await settle();
  await geometry('header-menu');
  await dismiss('header:escape', () => page.keyboard.press('Escape'));
  check('header:focus-restored', await headerTrigger.evaluate(e => document.activeElement === e));
  // Dispatch real contextmenu events on the real row with viewport-edge coordinates.
  // Only input coordinates are supplied; no menu DOM, CSS, or geometry is substituted.
  for (const [name, x, y] of [['bottom-right',1278,798], ['top-left',0,0]]) {
    await row.locator('.conversation-list-item-btn').evaluate((element, point) => element.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: point.x, clientY: point.y, button: 2 })), { x, y });
    await settle(); await geometry(name);
    const edgeBox = await menu.boundingBox();
    check(`${name}:expected-edge-position`, edgeBox !== null && (name === 'bottom-right' ? Math.abs(edgeBox.x + edgeBox.width - 1272) < 1 && Math.abs(edgeBox.y + edgeBox.height - 792) < 1 : Math.abs(edgeBox.x - 8) < 1 && Math.abs(edgeBox.y - 8) < 1));
    await dismiss(`${name}:escape`, () => page.keyboard.press('Escape'));
  }
  await trigger.click(); await settle();
  await dismiss('scroll', () => sidebar.evaluate(e => e.dispatchEvent(new Event('scroll', { bubbles: true }))));
  await trigger.click(); await settle();
  await dismiss('resize', () => page.setViewportSize({ width: 1100, height: 700 }));
  await trigger.click(); await settle(); await geometry('after-resize');
  await dismiss('after-resize:escape', () => page.keyboard.press('Escape'));
  await page.setViewportSize({ width: 768, height: 800 });
  if (await sidebar.evaluate(e => e.classList.contains('sidebar-closed'))) await page.locator('.burger-btn').first().click();
  await trigger.click(); await settle(); await geometry('mobile-sidebar');
  await dismiss('mobile-sidebar:escape', () => page.keyboard.press('Escape'));
  await page.locator('.sidebar-overlay').click({ position: { x: 700, y: 500 } });
  check('mobile-sidebar:close-transform-retained', await sidebar.evaluate(e => e.classList.contains('sidebar-closed')));
  check('browser:no-page-errors', errors.length === 0, errors.join('; '));
} catch (error) {
  fatal = error.stack;
  check('scenario-completed', false, fatal);
} finally {
  await browser.close();
  fs.writeFileSync('/evidence-output/evidence.json', JSON.stringify({ schemaVersion: '1.0', source: { commit: fs.readFileSync('/app/source-commit','utf8').trim(), tree: fs.readFileSync('/app/source-tree','utf8').trim() }, scenario, assertions, captures, fatal, timestamps: { startedUtc: process.env.BOTNEXUS_EVIDENCE_STARTED_UTC, completedUtc: new Date().toISOString() }, cleanup: { browserClosed: true, gatewayStopped: false, temporaryDataRemoved: false, containerRemoved: false } }, null, 2));
}
