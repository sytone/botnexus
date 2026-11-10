// Real candidate-only diagnostic. No substituted DOM, CSS, screenshots or scroll input.
import fs from 'node:fs';
import crypto from 'node:crypto';
import { collectFailure } from './failure-diagnostics.mjs';
import { installScrollDiagnostics } from './scroll-diagnostics.mjs';
import playwright from '/app/playwright/node_modules/playwright-core/index.js';

const origin = 'http://127.0.0.1:5000';
const output = '/evidence-output';
const assertions = [], captures = [], errors = [];
const check = (target, passed, detail = '') => {
  const assertion = { kind: 'browserState', target, passed: passed === true, detail };
  assertions.push(assertion);
  if (!assertion.passed) throw new Error(`${target}: ${detail}`);
};
const hash = path => crypto.createHash('sha256').update(fs.readFileSync(path)).digest('hex');
const browser = await playwright.chromium.launch({ headless: true });
let browserClosed = false, fatal;
let diagnosticPage;
const diagnosticContext = { stage: 'setup' };
const ids = [];
let sectionId;
const api = await playwright.request.newContext({ baseURL: origin });
const request = async (method, path, data) => {
  const response = await api[method](path, data === undefined ? {} : { data });
  if (!response.ok()) throw new Error(`Candidate API ${method} failed: HTTP ${response.status()}`);
  return response;
};
const box = async locator => {
  const result = await locator.boundingBox();
  if (!result) throw new Error('Required candidate element has no geometry.');
  return result;
};
const stationary = (before, after) => ['x', 'y', 'width', 'height'].every(key => Math.abs(before[key] - after[key]) <= 1);
const geometry = locator => locator.evaluate(el => ({ scrollTop: el.scrollTop, scrollHeight: el.scrollHeight, clientHeight: el.clientHeight }));
const focused = locator => locator.evaluate(el => document.activeElement === el);
// Observable panel contract: CanEdit controls require a confirmed current snapshot and no writer.
const sectionsReady = ({ sectionId, memberId, expanded }) => {
  if (document.querySelector('[data-testid="agent-select"]')?.value !== 'evidence-agent') return false;
  if (document.querySelector('.conversation-list-loading, [data-testid="sections-loading"], [data-testid="sections-saving"], [data-testid="sections-unavailable"], [data-testid="sections-reconciliation"], [data-testid="sections-unsupported"], [data-testid="sections-saved-refresh-failed"], [data-testid="sections-mutation-error"]')) return false;
  const enabled = selector => { const element = document.querySelector(selector); return !!element && !element.disabled; };
  if (!enabled('[data-testid="section-add-btn"]') || !enabled('[data-testid="sections-refresh"]')) return false;
  for (const key of ['pinned', 'scheduled', 'automated', 'conversations']) {
    if (!enabled(`.conversation-list-scroll > [data-testid="conversation-group-${key}"] [data-testid="section-actions-trigger"]`)) return false;
  }
  const custom = `.conversation-list-scroll > .conversation-group[data-section-id="${sectionId}"]`;
  if (!enabled(`${custom} [data-testid="section-collapse-toggle"]`)) return false;
  return !expanded || (document.querySelector(`${custom} [data-testid="section-collapse-toggle"]`)?.getAttribute('aria-expanded') === 'true'
    && !!document.querySelector(`${custom} [data-conversation-id="${memberId}"] a.conversation-list-item-btn`));
};
const openSidebar = async page => {
  const sidebar = page.locator('.main-sidebar');
  await sidebar.waitFor({ state: 'attached', timeout: 60000 });
  if (await sidebar.evaluate(el => el.classList.contains('sidebar-closed'))) await page.locator('.burger-btn').first().click();
};
try {
  for (let index = 0; index < 48; index += 1) {
    const response = await request('post', '/api/conversations', { agentId: 'evidence-agent', title: `Sidebar evidence conversation ${String(index).padStart(2, '0')}` });
    const conversation = await response.json();
    if (!conversation.conversationId) throw new Error('Candidate omitted conversation identity.');
    ids.push(conversation.conversationId);
  }
  await request('post', `/api/conversations/${ids[1]}/pin`);
  sectionId = (await (await request('post', '/api/agents/evidence-agent/sections', { name: 'Sidebar evidence custom section' })).json()).sectionId;
  if (!sectionId) throw new Error('Candidate omitted section identity.');
  await request('put', `/api/agents/evidence-agent/sections/${sectionId}/conversations/${ids[0]}`);
  for (const theme of ['light', 'dark']) {
    for (const [width, height] of [[1440, 900], [600, 844]]) {
      const context = await browser.newContext({ viewport: { width, height }, serviceWorkers: 'block', isMobile: false, hasTouch: false });
      const page = await context.newPage();
      diagnosticPage = page;
      Object.assign(diagnosticContext, { stage: 'setup', group: null, width, theme });
      await page.addInitScript(installScrollDiagnostics);
      page.setDefaultTimeout(20000);
      page.on('pageerror', error => errors.push(error.message));
      page.on('console', message => { if (message.type() === 'error') errors.push(message.text()); });
      await page.goto(`${origin}/chat/evidence-agent`, { waitUntil: 'load' });
      await openSidebar(page);
      await page.locator('[data-testid="agent-select"]').waitFor({ state: 'visible', timeout: 60000 });
      // Match the existing diagnostic: set preferences through the real settings UI.
      const overlay = page.locator('.sidebar-overlay');
      if (await overlay.isVisible()) {
        const bounds = await box(overlay);
        await overlay.click({ position: { x: bounds.width - 8, y: bounds.height - 8 } });
      }
      await overlay.waitFor({ state: 'hidden' });
      await page.locator('[data-testid="banner-settings-btn"]').click();
      await page.locator('[data-testid="focused-sidebar-toggle"]').setChecked(true);
      await page.locator('[data-testid="theme-select"]').selectOption(theme);
      await page.waitForFunction(expected => {
        const prefs = JSON.parse(localStorage.getItem('botnexus.portal.prefs') || '{}');
        return prefs.FocusedSidebar === true && prefs.Theme === expected;
      }, theme);
      await page.locator('[data-testid="portal-settings-close"]').click();
      await page.reload({ waitUntil: 'load' });
      await openSidebar(page);
      await page.waitForFunction(expected => (document.documentElement.getAttribute('data-theme') ?? 'dark') === expected, theme);
      const trigger = page.locator('[data-testid="focused-section-trigger"]');
      const content = page.locator('[data-testid="focused-section-content"]');
      const select = async section => {
        await trigger.click();
        const option = page.locator(`#focused-section-menu [role="menuitemradio"][data-section="${section}"]`);
        if (await option.locator('svg').count() !== 1) throw new Error('Picker option icon missing.');
        await option.click();
        await page.waitForFunction(value => document.querySelector('[data-testid="focused-section-content"]')?.dataset.section === value, section);
      };
      const agentSelect = page.locator('[data-testid="agent-select"]');
      if (await agentSelect.inputValue() !== 'evidence-agent') await agentSelect.selectOption('evidence-agent');
      await page.waitForFunction(sectionsReady, { sectionId, memberId: ids[0], expanded: false }, { timeout: 20000 });
      const custom = page.locator(`.conversation-list-scroll > .conversation-group[data-section-id="${sectionId}"]`);
      const toggle = custom.locator('[data-testid="section-collapse-toggle"]');
      if (await toggle.getAttribute('aria-expanded') === 'false') await toggle.click();
      await custom.locator('[data-testid="conversation-section-body"]').waitFor({ state: 'visible' });
      await page.waitForFunction(sectionsReady, { sectionId, memberId: ids[0], expanded: true }, { timeout: 20000 });
      const member = custom.locator(`[data-conversation-id="${ids[0]}"] a.conversation-list-item-btn`);
      const peerGroups = await page.locator('.conversation-list-scroll > .conversation-group').count() >= 5
        && await page.locator('[data-testid="sections-group-toggle"], [data-testid="chat-sections-body"]').count() === 0;
      const customMember = await member.count() === 1 && await member.getAttribute('href') === `agent/evidence-agent/conversation/${ids[0]}`
        && await page.locator(`[data-testid="conversation-group-conversations"] [data-conversation-id="${ids[0]}"]`).count() === 0;
      let builtInMenus = true;
      for (const key of ['pinned', 'scheduled', 'automated', 'conversations']) {
        Object.assign(diagnosticContext, { stage: 'section-menu', group: key });
        await page.waitForFunction(sectionsReady, { sectionId, memberId: ids[0], expanded: true }, { timeout: 20000 });
        const group = page.locator(`.conversation-list-scroll > [data-testid="conversation-group-${key}"]`);
        const menuTrigger = group.locator('[data-testid="section-actions-trigger"]');
        await page.evaluate(() => window.__sidebarDiagnosticMark('before-scroll-into-view'));
        await menuTrigger.scrollIntoViewIfNeeded();
        await page.evaluate(() => window.__sidebarDiagnosticMark('after-scroll-into-view'));
        // Observe ordering first: this is not a claim that scroll events have drained.
        await page.evaluate(() => window.__sidebarDiagnosticMark('before-click'));
        await menuTrigger.click();
        await page.evaluate(() => window.__sidebarDiagnosticMark('after-click'));
        const menu = group.getByRole('menu');
        await menu.waitFor({ state: 'visible' });
        await page.evaluate(() => window.__sidebarDiagnosticMark('menu-visible'));
        builtInMenus &&= await menu.getByRole('menuitem').count() === 2
          && await menu.locator('[data-testid="section-move-up"]').count() === 1
          && await menu.locator('[data-testid="section-move-down"]').count() === 1
          && await menu.locator('[data-testid="section-rename-btn"], [data-testid="section-delete-btn"]').count() === 0;
        await group.locator('[data-testid="section-menu-dismiss"]').click();
      }
      // Geometry qualification uses real layout after native input preparation; later capture setup resets top.
      const filterBar = page.locator('[data-testid="conversation-filter-bar"]');
      await filterBar.scrollIntoViewIfNeeded();
      const filterOwner = await page.locator('.focused-chat-scroll').evaluate(element => {
        const rect = element.getBoundingClientRect();
        return { x: rect.x + element.clientLeft, y: rect.y + element.clientTop, width: element.clientWidth, height: element.clientHeight };
      });
      const filterPills = filterBar.locator('.conversation-filter-btn');
      check(`${theme}-${width}:filter-pill-count`, await filterPills.count() === 3);
      const filterLabels = ['None', 'Today', 'This Week'];
      for (let index = 0; index < 3; index += 1) {
        const pill = filterPills.nth(index);
        const bounds = await box(pill);
        const minimum = await pill.evaluate(element => parseFloat(getComputedStyle(element).getPropertyValue('--hit-pointer')));
        check(`${theme}-${width}:filter-pill-${index}:fully-contained`, bounds.x >= Math.max(0, filterOwner.x) - 0.5 && bounds.y >= Math.max(0, filterOwner.y) - 0.5 && bounds.x + bounds.width <= Math.min(width, filterOwner.x + filterOwner.width) + 0.5 && bounds.y + bounds.height <= Math.min(height, filterOwner.y + filterOwner.height) + 0.5);
        check(`${theme}-${width}:filter-pill-${index}:hit-target`, Number.isFinite(minimum) && minimum > 0 && bounds.height >= minimum - 0.5 && bounds.width >= minimum - 0.5);
        check(`${theme}-${width}:filter-pill-${index}:label`, (await pill.textContent()).trim() === filterLabels[index] && await pill.evaluate(element => element.scrollWidth <= element.clientWidth));
      }
      diagnosticContext.stage = 'journey';
      // Picker keyboard behavior stays native. Escape must restore focus.
      await trigger.focus();
      await page.keyboard.press('ArrowDown');
      const pickerMenu = page.locator('#focused-section-menu');
      await pickerMenu.waitFor({ state: 'visible' });
      check(`${theme}-${width}:picker-focus`, await focused(pickerMenu.locator('[data-section="chat"]')));
      await page.keyboard.press('End');
      check(`${theme}-${width}:picker-end`, await focused(pickerMenu.getByRole('menuitemradio').last()));
      await page.keyboard.press('Home');
      check(`${theme}-${width}:picker-home`, await focused(pickerMenu.getByRole('menuitemradio').first()));
      await page.keyboard.press('Escape');
      check(`${theme}-${width}:picker-escape`, await focused(trigger) && await trigger.getAttribute('aria-expanded') === 'false');
      for (const section of ['chat', 'agents']) {
        if (section === 'agents') await select('agents');
        const ownerSelector = section === 'chat' ? '.focused-chat-scroll' : '[data-testid="focused-section-content"]';
        const owner = page.locator(ownerSelector);
        if (await owner.count() !== 1) throw new Error('Ambiguous scroll owner.');
        const controls = section === 'chat' ? [page.locator('[data-testid="agent-select"]'), content.locator('.agent-conversation-list-header'), page.locator('[data-testid="new-conversation-btn"]'), page.locator('[data-testid="section-add-btn"]')] : [];
        await page.waitForFunction(current => current === 'chat'
          ? document.querySelectorAll('[data-testid="conversation-group-conversations"] .conversation-list-item').length >= 36
          : document.querySelectorAll('[data-testid="focused-section-content"] [data-testid^="subnav-agents-"]').length >= 48, section);
        const populated = section === 'chat'
          ? await page.locator('[data-testid="conversation-group-conversations"] .conversation-list-item').count() >= 36
          : await content.locator('[data-testid^="subnav-agents-"]').count() >= 48;
        // Scripted reset is setup only; movement evidence below uses real wheel and PageDown.
        await owner.evaluate(el => { el.scrollTop = 0; });
        const pickerBefore = await box(trigger), documentBefore = await page.evaluate(() => window.scrollY);
        const controlBefore = await Promise.all(controls.map(box));
        const keyboardTarget = section === 'chat' ? owner : content.locator('[data-testid^="subnav-agents-"]').first();
        const state = { wheel: false, keyboard: false, focus: false };
        const save = async suffix => {
          const name = `${theme}-${width}x${height}-${section}-${suffix}`;
          const observed = await geometry(owner), outer = await geometry(content);
          const bounds = await box(content);
          check(`${name}:overflow`, observed.scrollHeight > observed.clientHeight + 20 && observed.clientHeight > 100);
          check(`${name}:fixed-picker`, stationary(pickerBefore, await box(trigger)));
          check(`${name}:fixed-document`, documentBefore === await page.evaluate(() => window.scrollY));
          check(`${name}:outer-owner`, (section === 'chat' ? outer.scrollTop === 0 : ownerSelector === '[data-testid="focused-section-content"]') && bounds.y >= pickerBefore.y + pickerBefore.height - 1 && bounds.y + bounds.height <= height + 1);
          check(`${name}:populated`, populated && (suffix !== 'populated' || observed.scrollTop === 0));
          check(`${name}:theme`, (await page.locator('html').getAttribute('data-theme') ?? 'dark') === theme);
          check(`${name}:desktop-route`, !new URL(page.url()).pathname.startsWith('/mobile'));
          check(`${name}:no-browser-errors`, errors.length === 0, errors.join('; '));
          if (section === 'chat') {
            check(`${name}:fixed-controls`, (await Promise.all(controls.map(box))).every((value, index) => stationary(controlBefore[index], value)));
            check(`${name}:peer-groups`, peerGroups);
            check(`${name}:custom-member`, customMember);
            check(`${name}:built-in-menus`, builtInMenus);
          }
          if (suffix === 'scrolled') {
            check(`${name}:wheel-moved`, state.wheel);
            check(`${name}:keyboard-moved`, state.keyboard && observed.scrollTop > 0);
            check(`${name}:keyboard-focus`, state.focus && await focused(keyboardTarget));
          }
          const metadata = { theme, section, viewport: { width, height }, scrollOwner: { selector: ownerSelector, geometry: observed }, contentGeometry: outer, picker: await box(trigger), route: new URL(page.url()).pathname };
          const sidecar = `${name}.json`, screenshot = `${name}.png`;
          fs.writeFileSync(`${output}/${sidecar}`, JSON.stringify(metadata, null, 2));
          await page.screenshot({ path: `${output}/${screenshot}`, fullPage: false });
          captures.push({ name, viewport: { width, height }, screenshot: { path: screenshot, sha256: hash(`${output}/${screenshot}`) }, metadata: { path: sidecar, sha256: hash(`${output}/${sidecar}`) }, assertions: assertions.filter(value => value.target.startsWith(`${name}:`)) });
        };
        await save('populated');
        await owner.hover();
        check(`${theme}-${width}-${section}:wheel-start`, (await geometry(owner)).scrollTop === 0);
        await page.mouse.wheel(0, 1200);
        await page.waitForFunction(selector => document.querySelector(selector).scrollTop > 0, ownerSelector);
        state.wheel = (await geometry(owner)).scrollTop > 0;
        check(`${theme}-${width}-${section}:wheel-fixed-picker`, stationary(pickerBefore, await box(trigger)));
        check(`${theme}-${width}-${section}:wheel-fixed-document`, documentBefore === await page.evaluate(() => window.scrollY));
        if (section === 'chat') {
          check(`${theme}-${width}-${section}:wheel-fixed-controls`, (await Promise.all(controls.map(box))).every((value, index) => stationary(controlBefore[index], value)));
          check(`${theme}-${width}-${section}:wheel-outer`, (await geometry(content)).scrollTop === 0);
        }
        await owner.evaluate(el => { el.scrollTop = 0; });
        if (section === 'chat') {
          await controls[2].focus();
          await page.keyboard.press('Tab');
          check(`${theme}-${width}:new-section-tab-focus`, await focused(controls[3]));
          await page.keyboard.press('Tab');
        } else {
          await trigger.focus();
          await page.keyboard.press('Tab');
        }
        state.focus = await focused(keyboardTarget);
        check(`${theme}-${width}-${section}:keyboard-start`, state.focus && (await geometry(owner)).scrollTop === 0);
        await page.keyboard.press('PageDown');
        await page.waitForFunction(selector => document.querySelector(selector).scrollTop > 0, ownerSelector);
        state.keyboard = (await geometry(owner)).scrollTop > 0;
        await save('scrolled');
      }
      await context.close();
    }
  }
  check('scenario-completed', captures.length === 16);
} catch (error) {
  if (diagnosticPage) {
    try { await collectFailure(diagnosticPage, diagnosticContext, errors); }
    catch { /* Missing/incomplete diagnostics must never turn a failed run into success. */ }
  }
  fatal = error.message;
  assertions.push({ kind: 'browserState', target: 'scenario-completed', passed: false, detail: fatal });
} finally {
  try {
    if (sectionId) await request('delete', `/api/agents/evidence-agent/sections/${sectionId}`);
    for (const id of ids) await request('delete', `/api/conversations/${id}`);
  } catch (error) {
    fatal = error.message;
    assertions.push({ kind: 'browserState', target: 'fixture-cleanup', passed: false, detail: fatal });
  }
  await api.dispose();
  await browser.close();
  browserClosed = true;
  const now = new Date().toISOString();
  fs.writeFileSync(`${output}/evidence.json`, JSON.stringify({ schemaVersion: '1.0', source: { commit: fs.readFileSync('/app/source-commit', 'utf8').trim(), tree: fs.readFileSync('/app/source-tree', 'utf8').trim() }, scenario: { name: 'focused-sidebar', promptKey: 'SLOW_STREAM' }, viewport: { width: 1440, height: 900 }, screenshot: captures[0]?.screenshot, captures, assertions, accessibility: { focusedSidebar: true }, timestamps: { startedUtc: process.env.BOTNEXUS_EVIDENCE_STARTED_UTC, capturedUtc: now, completedUtc: now }, cleanup: { browserClosed, gatewayStopped: false, temporaryDataRemoved: false, containerRemoved: false } }, null, 2));
}
if (fatal) throw new Error(fatal);
