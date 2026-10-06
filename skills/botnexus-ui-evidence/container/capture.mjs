import fs from 'node:fs';
import crypto from 'node:crypto';
import playwright from '/app/playwright/node_modules/playwright-core/index.js';
const { chromium } = playwright;

const scenario = JSON.parse(process.env.BOTNEXUS_EVIDENCE_SCENARIO || '{}');
if (!['SLOW_STREAM', 'MATRIX_PRE_TOKEN', 'MATRIX_TOOL_GAP', 'MATRIX_LONG_ACTIVE'].includes(scenario.promptKey)) throw new Error('Unsupported deterministic evidence prompt key.');
const startedUtc = process.env.BOTNEXUS_EVIDENCE_STARTED_UTC;
const assertions = [];
const browserErrors = [];
let browser;
const check = (kind, target, passed, detail = '') => { assertions.push({ kind, target, passed, detail }); if (!passed) throw new Error(`${kind} failed: ${target} ${detail}`); };
const recordState = async (page, name, expectedBusy, options = {}) => {
  const composer = options.mobile
    ? page.locator('[data-testid="mobile-composer"]').first()
    : page.locator('#evidence-agent-conversation-panel [data-testid="chat-composer"]').first();
  await composer.waitFor({ state: 'visible', timeout: 10000 });
  const busy = await composer.getAttribute('aria-busy');
  check('browserState', `${name}:aria-busy`, busy === String(expectedBusy), `observed ${busy}`);
  if (options.mobile && options.conversationId) {
    const expectedPath = `/mobile/agent/evidence-agent/conversation/${options.conversationId}`;
    const actualPath = new URL(page.url()).pathname;
    check('browserState', `${name}:route`, actualPath === expectedPath, 'matched canonical mobile conversation route pattern');
    check('browserState', `${name}:composer-selector`, await page.locator('[data-testid="mobile-composer"]').count() === 1, 'actual mobile app composer selector');
  }
  if (!options.mobile) {
    const stop = page.locator('#evidence-agent-conversation-panel [data-testid="chat-abort-btn"]');
    const stopVisible = await stop.isVisible().catch(() => false);
    check('browserState', `${name}:stop-control`, expectedBusy ? stopVisible : !stopVisible, `observed ${stopVisible}`);
  } else {
    const status = page.locator('[data-testid="mobile-composer-status"]');
    if (expectedBusy) {
      await status.waitFor({ state: 'visible', timeout: 10000 });
      const role = await status.getAttribute('role');
      const text = (await status.textContent())?.trim().replace(/\s+/g, ' ');
      check('browserState', `${name}:stable-status`, role === 'status' && text === 'Agent is working', `role=${role}; text=${text}`);
    } else {
      check('browserState', `${name}:stable-status-absent`, await status.count() === 0, 'idle composer has no active status');
    }
  }
  if (options.focused) {
    const input = page.locator('#evidence-agent-conversation-panel [data-testid="chat-input"]').first();
    await input.focus();
    const focused = await input.evaluate(element => document.activeElement === element);
    check('browserState', `${name}:focused-active-element`, focused, 'document.activeElement is chat input');
  }
  if (options.reducedMotion) {
    const reduced = await page.evaluate(() => matchMedia('(prefers-reduced-motion: reduce)').matches);
    const animationName = await composer.evaluate(element => getComputedStyle(element, '::before').animationName);
    check('browserState', `${name}:reduced-motion`, reduced && animationName === 'none', `media=${reduced}; animation=${animationName}`);
  }
  if (typeof options.animationTime === 'number') {
    const actualTime = await composer.evaluate(element => {
      const item = document.getAnimations().find(candidate => candidate.effect?.target === element && candidate.effect?.pseudoElement === '::before');
      return item?.currentTime;
    });
    check('browserState', `${name}:animation-position`, actualTime === options.animationTime, `expected ${options.animationTime}; observed ${actualTime}`);
    const offsetDistance = await composer.evaluate(element => getComputedStyle(element, '::before').offsetDistance);
    check('browserState', `${name}:before-offset-distance`, /^\d+(?:\.\d+)?%$/.test(offsetDistance), `computed ::before offset-distance ${offsetDistance}`);
  }
  if (options.content) {
    const content = await page.locator('#evidence-agent-conversation-panel [data-testid="streaming-message"]').allTextContents();
    check('browserState', `${name}:rendered-content`, content.join(' ').includes(options.content), `expected visible text ${options.content}`);
  }
  if (options.noContent) {
    const contentCount = await page.locator('#evidence-agent-conversation-panel [data-testid="streaming-message"]').count();
    check('browserState', `${name}:pre-token-content-absent`, contentCount === 0, `visible streaming message count ${contentCount}`);
  }
  if (options.toolCall) {
    const tool = page.locator('#evidence-agent-conversation-panel .message.tool .tool-name').filter({ hasText: /delay/i }).first();
    const visible = await tool.isVisible().catch(() => false);
    check('browserState', `${name}:rendered-tool-call`, visible, `expected visible tool call ${options.toolCall}`);
  }
};
const saveCapture = async (page, captures, name, options = {}) => {
  await recordState(page, name, options.busy, options);
  check('browserErrors', `${name}:pageerror/console.error`, browserErrors.length === 0, browserErrors.join('; '));
  const file = `${name}.png`;
  const path = `/evidence-output/${file}`;
  await page.screenshot({ path, fullPage: false });
  const bytes = fs.readFileSync(path);
  check('screenshot', file, bytes.length > 0, 'captured from the real browser page');
  captures.push({ name, viewport: page.viewportSize(), screenshot: { path: file, sha256: crypto.createHash('sha256').update(bytes).digest('hex') }, assertions: assertions.filter(item => item.target.startsWith(`${name}:`) || (item.kind === 'browserErrors' && item.target === `${name}:pageerror/console.error`)) });
};
const manifestBase = pagePath => ({
  schemaVersion: '1.0',
  source: { commit: fs.readFileSync('/app/source-commit','utf8').trim(), tree: fs.readFileSync('/app/source-tree','utf8').trim() },
  scenario: { name: scenario.name, promptKey: scenario.promptKey, pagePath },
  viewport: scenario.viewport,
  assertions,
  accessibility: { activeRunControls: {} },
  timestamps: { startedUtc, capturedUtc: new Date().toISOString(), completedUtc: new Date().toISOString() },
  cleanup: { gatewayStopped: false, browserClosed: false, temporaryDataRemoved: false, containerRemoved: false }
});
try {
  browser = await chromium.launch({ headless: true });
  if (scenario.captureMatrix === true) {
    const captures = [];
    const desktop = await browser.newContext({ viewport: { width: 1440, height: 1000 }, serviceWorkers: 'block' });
    const desktopPage = await desktop.newPage();
    desktopPage.on('pageerror', error => browserErrors.push(`pageerror: ${error.message}`));
    desktopPage.on('console', msg => { if (msg.type() === 'error') browserErrors.push(`console: ${msg.text()}`); });
    const setup = async page => {
      await page.goto('http://127.0.0.1:5000/chat/evidence-agent', { waitUntil: 'load' });
      await page.locator('[data-testid="agent-panel"]').first().waitFor({ state: 'attached', timeout: 60000 });
      const sidebar = page.locator('.main-sidebar').first();
      if (await sidebar.evaluate(element => element.classList.contains('sidebar-closed'))) {
        await page.locator('.burger-btn').first().click();
        await sidebar.waitFor({ state: 'visible', timeout: 5000 });
      }
      await page.locator('[data-testid="agent-select"]').first().selectOption('evidence-agent');
      const response = await page.request.post('http://127.0.0.1:5000/api/conversations', { data: { agentId: 'evidence-agent', title: 'UI Evidence idle' } });
      check('conversation', 'idle:create', response.ok(), `status ${response.status()}`);
      const idle = await response.json();
      await page.goto(`http://127.0.0.1:5000/chat/evidence-agent/${idle.conversationId}`, { waitUntil: 'load' });
      await page.locator('#evidence-agent-conversation-panel [data-testid="chat-composer"][aria-busy="false"]').waitFor({ state: 'visible', timeout: 15000 });
    };
    await setup(desktopPage);
    await saveCapture(desktopPage, captures, 'desktop-idle', { busy: false });

    const mobile = await browser.newContext({ viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true, serviceWorkers: 'block' });
    const mobilePage = await mobile.newPage();
    mobilePage.on('pageerror', error => browserErrors.push(`pageerror: ${error.message}`));
    mobilePage.on('console', msg => { if (msg.type() === 'error') browserErrors.push(`console: ${msg.text()}`); });
    const mobileRoute = conversationId => `/mobile/agent/evidence-agent/conversation/${conversationId}`;
    const createMobileConversation = async title => {
      const response = await mobilePage.request.post('http://127.0.0.1:5000/api/conversations', { data: { agentId: 'evidence-agent', title } });
      check('conversation', `${title}:create`, response.ok(), `status ${response.status()}`);
      return response.json();
    };
    const mobileIdle = await createMobileConversation('UI Evidence mobile idle');
    await mobilePage.goto(`http://127.0.0.1:5000${mobileRoute(mobileIdle.conversationId)}`, { waitUntil: 'load' });
    await mobilePage.locator('[data-testid="mobile-composer"][aria-busy="false"]').waitFor({ state: 'visible', timeout: 15000 });
    await saveCapture(mobilePage, captures, 'mobile-idle', { busy: false, mobile: true, conversationId: mobileIdle.conversationId });

    const launchRun = async (page, promptKey, waitForText) => {
      const conversationResponse = await page.request.post('http://127.0.0.1:5000/api/conversations', { data: { agentId: 'evidence-agent', title: `UI Evidence ${promptKey}` } });
      check('conversation', `${promptKey}:create`, conversationResponse.ok(), `status ${conversationResponse.status()}`);
      const conversation = await conversationResponse.json();
      const messageUrl = `http://127.0.0.1:5000/api/agents/evidence-agent/conversations/${conversation.conversationId}/messages`;
      // SubscribeAll derives live conversation groups from sessions. Create the session before
      // connecting this browser to the route, or a new conversation has no stream group yet.
      const seeded = await page.request.post(messageUrl, { data: { message: 'Evidence session primed', wake: false, sender: 'ui-evidence' } });
      check('message', `${promptKey}:prime-session`, seeded.ok(), `status ${seeded.status()}`);
      await page.goto(`http://127.0.0.1:5000/chat/evidence-agent/${conversation.conversationId}`, { waitUntil: 'load' });
      await page.locator('#evidence-agent-conversation-panel [data-testid="chat-composer"][aria-busy="false"]').waitFor({ state: 'visible', timeout: 15000 });
      const input = page.locator('#evidence-agent-conversation-panel [data-testid="chat-input"]').first();
      await input.fill(promptKey);
      await page.locator('#evidence-agent-conversation-panel [data-testid="chat-send"]').first().click();
      check('message', `${promptKey}:send`, true, 'sent through the real routed composer and SignalR hub');
      try {
        await page.locator('#evidence-agent-conversation-panel [data-testid="chat-composer"][aria-busy="true"]').waitFor({ state: 'visible', timeout: 15000 });
      } catch (error) {
        const state = await page.evaluate(() => ({
          routeMatches: location.pathname.startsWith('/chat/evidence-agent/'),
          composerCount: document.querySelectorAll('#evidence-agent-conversation-panel [data-testid="chat-composer"]').length,
          busy: document.querySelector('#evidence-agent-conversation-panel [data-testid="chat-composer"]')?.getAttribute('aria-busy') ?? 'absent',
          panelCount: document.querySelectorAll('[data-testid="agent-panel"]').length,
          streamingBadge: !!document.querySelector('#evidence-agent-conversation-panel [data-testid="streaming-badge"]'),
          messageCount: document.querySelectorAll('#evidence-agent-conversation-panel .message').length,
          errorCount: document.querySelectorAll('[data-testid="portal-load-error"]').length
        }));
        throw new Error(`Active-run projection unavailable after accepted ${promptKey} message: ${JSON.stringify(state)}; ${error.message}`);
      }
      if (waitForText) {
        try {
          await page.locator('#evidence-agent-conversation-panel [data-testid="streaming-message"]').filter({ hasText: waitForText }).waitFor({ state: 'visible', timeout: 15000 });
        } catch (error) {
          const state = await page.evaluate(() => ({
            busy: document.querySelector('#evidence-agent-conversation-panel [data-testid="chat-composer"]')?.getAttribute('aria-busy') ?? 'absent',
            streamingText: [...document.querySelectorAll('#evidence-agent-conversation-panel [data-testid="streaming-message"]')].map(element => element.textContent?.trim().slice(0, 70)),
            toolNames: [...document.querySelectorAll('#evidence-agent-conversation-panel .message.tool .tool-name')].map(element => element.textContent?.trim().slice(0, 70)),
            messageCount: document.querySelectorAll('#evidence-agent-conversation-panel .message').length
          }));
          throw new Error(`Scripted ${promptKey} content did not render: ${JSON.stringify(state)}; ${error.message}`);
        }
      }
    };

    await launchRun(desktopPage, 'MATRIX_PRE_TOKEN');
    await saveCapture(desktopPage, captures, 'active-pre-token', { busy: true, noContent: true });
    await desktopPage.locator('#evidence-agent-conversation-panel [data-testid="chat-abort-btn"]').click();
    await desktopPage.locator('#evidence-agent-conversation-panel [data-testid="chat-composer"][aria-busy="false"]').waitFor({ state: 'visible', timeout: 15000 });

    await launchRun(desktopPage, 'MATRIX_TOOL_GAP');
    // Require an actual tool row; a completed text segment alone is not a tool gap.
    const toolCall = desktopPage.locator('#evidence-agent-conversation-panel .message.tool .tool-name').filter({ hasText: 'delay' }).first();
    try {
      await toolCall.waitFor({ state: 'attached', timeout: 15000 });
    } catch (error) {
      const diagnostic = await desktopPage.evaluate(() => ({
        busy: document.querySelector('#evidence-agent-conversation-panel [data-testid="chat-composer"]')?.getAttribute('aria-busy'),
        messages: [...document.querySelectorAll('#evidence-agent-conversation-panel .message')].map(element => element.textContent?.trim().slice(0, 100)).slice(-8),
        toolNames: [...document.querySelectorAll('#evidence-agent-conversation-panel .message.tool .tool-name')].map(element => element.textContent?.trim().slice(0, 100)),
        streaming: [...document.querySelectorAll('#evidence-agent-conversation-panel [data-testid="streaming-message"]')].map(element => element.textContent?.trim().slice(0, 100)),
      }));
      throw new Error(`Tool-gap row missing: ${JSON.stringify(diagnostic)}; ${error.message}`);
    }
    await saveCapture(desktopPage, captures, 'active-tool-gap', { busy: true, toolCall: 'delay' });
    await desktopPage.locator('#evidence-agent-conversation-panel [data-testid="chat-abort-btn"]').click();
    await desktopPage.locator('#evidence-agent-conversation-panel [data-testid="chat-composer"][aria-busy="false"]').waitFor({ state: 'visible', timeout: 15000 });
    await saveCapture(desktopPage, captures, 'returned-idle', { busy: false });

    await launchRun(desktopPage, 'MATRIX_LONG_ACTIVE');
    await saveCapture(desktopPage, captures, 'focused-active', { busy: true, focused: true });
    const activeRunControls = {};
    for (const testId of ['chat-steer-btn', 'chat-redirect-btn', 'chat-followup-btn', 'chat-abort-btn']) {
      const control = desktopPage.locator(`#evidence-agent-conversation-panel [data-testid="${testId}"]`).first();
      await control.waitFor({ state: 'visible', timeout: 10000 });
      activeRunControls[testId] = (await control.evaluate(element => element.getAttribute('aria-label') || element.textContent?.trim() || element.getAttribute('title') || '')).replace(/\\s+/g, ' ').trim();
      check('accessibility', testId, activeRunControls[testId].length > 0, `accessible name: ${activeRunControls[testId]}`);
    }
    const mobileActive = await createMobileConversation('UI Evidence mobile active');
    const mobileMessageUrl = `http://127.0.0.1:5000/api/agents/evidence-agent/conversations/${mobileActive.conversationId}/messages`;
    const mobilePrimed = await mobilePage.request.post(mobileMessageUrl, { data: { message: 'Evidence session primed', wake: false, sender: 'ui-evidence' } });
    check('message', 'mobile-active:prime-session', mobilePrimed.ok(), `status ${mobilePrimed.status()}`);
    await mobilePage.goto(`http://127.0.0.1:5000${mobileRoute(mobileActive.conversationId)}`, { waitUntil: 'load' });
    await mobilePage.locator('[data-testid="mobile-composer"][aria-busy="false"]').waitFor({ state: 'visible', timeout: 15000 });
    await mobilePage.locator('[data-testid="mobile-composer"] textarea').fill('MATRIX_LONG_ACTIVE');
    await mobilePage.locator('[data-testid="mobile-send"]').click();
    check('message', 'mobile-active:send', true, 'sent through the actual mobile composer and SignalR hub');
    await mobilePage.locator('[data-testid="mobile-composer"][aria-busy="true"]').waitFor({ state: 'visible', timeout: 15000 });
    await saveCapture(mobilePage, captures, 'mobile-active', { busy: true, mobile: true, conversationId: mobileActive.conversationId });

    const composer = mobilePage.locator('[data-testid="mobile-composer"]').first();
    const animationName = await composer.evaluate(element => getComputedStyle(element, '::before').animationName);
    check('browserState', 'animation:active-name', animationName !== 'none', `observed ${animationName}`);
    const animation = await composer.evaluate(element => document.getAnimations().find(item => item.effect?.target === element && item.effect?.pseudoElement === '::before' && item.playState !== 'idle'));
    const animationInventory = await composer.evaluate(element => document.getAnimations().map(item => ({ name: item.animationName, pseudo: item.effect?.pseudoElement, target: item.effect?.target?.getAttribute('data-testid'), state: item.playState, time: item.currentTime })).slice(0, 30));
    check('browserState', 'animation:real-browser-animation', Boolean(animation), `CSS animation is running in the browser: ${JSON.stringify(animationInventory)}`);
    if (!animation) throw new Error('Active composer CSS animation was not found.');
    const timing = await composer.evaluate(element => {
      const item = document.getAnimations().find(candidate => candidate.effect?.target === element && candidate.effect?.pseudoElement === '::before' && candidate.playState !== 'idle');
      return item?.effect?.getComputedTiming().duration;
    });
    check('browserState', 'animation:finite-duration', typeof timing === 'number' && Number.isFinite(timing), `iterationDuration=${timing}`);
    const initialAnimationTime = await animation.currentTime;
    const animationPositions = [['animation-start', 0], ['animation-midpoint', timing * 0.5], ['animation-end', Math.max(0, timing - 1)]];
    const offsetDistances = {};
    for (const [name, position] of animationPositions) {
      await composer.evaluate((element, value) => {
        const item = document.getAnimations().find(candidate => candidate.effect?.target === element && candidate.effect?.pseudoElement === '::before' && candidate.playState !== 'idle');
        if (!item) throw new Error('Composer animation disappeared before capture.');
        item.pause();
        item.currentTime = value;
      }, position);
      await composer.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
      const offsetDistance = await composer.evaluate(element => getComputedStyle(element, '::before').offsetDistance);
      check('browserState', `${name}:before-offset-distance`, /^\d+(?:\.\d+)?%$/.test(offsetDistance), `computed ::before offset-distance ${offsetDistance}`);
      offsetDistances[name] = offsetDistance;
      await saveCapture(mobilePage, captures, name, { busy: true, mobile: true, animationTime: position });
    }
    check('browserState', 'animation:offset-distance-changes', offsetDistances['animation-start'] !== offsetDistances['animation-midpoint'], `start=${offsetDistances['animation-start']}; midpoint=${offsetDistances['animation-midpoint']}`);
    await composer.evaluate((element, value) => {
      const item = document.getAnimations().find(candidate => candidate.effect?.target === element && candidate.effect?.pseudoElement === '::before');
      if (item && typeof value === 'number') { item.play(); item.currentTime = value; }
    }, initialAnimationTime);

    const reduced = await browser.newContext({ viewport: { width: 1440, height: 1000 }, reducedMotion: 'reduce', serviceWorkers: 'block' });
    const reducedPage = await reduced.newPage();
    reducedPage.on('pageerror', error => browserErrors.push(`pageerror: ${error.message}`));
    reducedPage.on('console', msg => { if (msg.type() === 'error') browserErrors.push(`console: ${msg.text()}`); });
    await setup(reducedPage);
    await launchRun(reducedPage, 'MATRIX_LONG_ACTIVE');
    await saveCapture(reducedPage, captures, 'reduced-motion', { busy: true, reducedMotion: true });
    await reducedPage.locator('#evidence-agent-conversation-panel [data-testid="chat-abort-btn"]').click();
    await reducedPage.locator('#evidence-agent-conversation-panel [data-testid="chat-composer"][aria-busy="false"]').waitFor({ state: 'visible', timeout: 15000 });
    await reduced.close();
    await mobilePage.locator('[data-testid="mobile-composer"][aria-busy="true"]').waitFor({ state: 'visible', timeout: 15000 });
    await mobile.close();
    await desktopPage.locator('#evidence-agent-conversation-panel [data-testid="chat-abort-btn"]').click();
    await desktopPage.locator('#evidence-agent-conversation-panel [data-testid="chat-composer"][aria-busy="false"]').waitFor({ state: 'visible', timeout: 15000 });
    await desktop.close();
    await browser.close(); browser = undefined;
    const manifest = manifestBase('/chat/evidence-agent');
    manifest.captures = captures;
    manifest.screenshot = captures[0].screenshot;
    manifest.accessibility.activeRunControls = activeRunControls;
    manifest.cleanup.browserClosed = true;
    fs.writeFileSync('/evidence-output/evidence.json', JSON.stringify(manifest, null, 2));
  } else {
    const context = await browser.newContext({ viewport: scenario.viewport });
    const page = await context.newPage();
    page.on('pageerror', error => browserErrors.push(`pageerror: ${error.message}`));
    page.on('console', msg => { if (msg.type() === 'error') browserErrors.push(`console: ${msg.text()}`); });
    await page.goto('http://127.0.0.1:5000/chat/evidence-agent', { waitUntil: 'load' });
    await page.locator('[data-testid="agent-panel"]').first().waitFor({ state: 'attached', timeout: 60000 });
    const sidebar = page.locator('.main-sidebar').first();
    if (await sidebar.evaluate(element => element.classList.contains('sidebar-closed'))) {
      await page.locator('.burger-btn').first().click();
      await sidebar.waitFor({ state: 'visible', timeout: 5000 });
    }
    await page.locator('[data-testid="agent-select"]').first().selectOption('evidence-agent');
    await page.locator('[data-testid="new-conversation-btn"]').first().waitFor({ state: 'visible' });
    const conversationResponse = await page.request.post('http://127.0.0.1:5000/api/conversations', { data: { agentId: 'evidence-agent', title: 'UI Evidence' } });
    check('conversation', 'POST /api/conversations', conversationResponse.ok(), `status ${conversationResponse.status()}`);
    const conversation = await conversationResponse.json();
    const messageResponse = await page.request.post(`http://127.0.0.1:5000/api/agents/evidence-agent/conversations/${conversation.conversationId}/messages`, { data: { message: scenario.promptKey, wake: true, sender: 'ui-evidence' } });
    check('message', 'POST conversation message', messageResponse.ok(), `status ${messageResponse.status()}`);
    await page.reload({ waitUntil: 'load' });
    await page.locator('[data-testid="agent-panel"]').first().waitFor({ state: 'attached', timeout: 60000 });
    await page.locator('#evidence-agent-conversation-panel [data-testid="streaming-badge"]').waitFor({ state: 'visible', timeout: 10000 });
    for (const selector of ['[data-testid="streaming-badge"]', '[data-testid="chat-abort-btn"]']) {
      const locator = page.locator(`#evidence-agent-conversation-panel ${selector}`);
      await locator.waitFor({ state: 'visible', timeout: 10000 });
      check('activeRunSelector', selector, await locator.isVisible());
    }
    for (const name of scenario.accessibleNames || []) {
      const element = page.locator('#evidence-agent-conversation-panel').getByLabel(name, { exact: true });
      check('accessibility', name, await element.isVisible(), 'exact accessible name');
    }
    const activeRunControls = {};
    for (const testId of ['chat-steer-btn', 'chat-redirect-btn', 'chat-followup-btn', 'chat-abort-btn']) {
      const control = page.locator(`#evidence-agent-conversation-panel [data-testid="${testId}"]`).first();
      await control.waitFor({ state: 'visible', timeout: 10000 });
      activeRunControls[testId] = (await control.evaluate(element => element.getAttribute('aria-label') || element.textContent?.trim() || element.getAttribute('title') || '')).replace(/\s+/g, ' ').trim();
      check('accessibility', testId, activeRunControls[testId].length > 0, `accessible name: ${activeRunControls[testId]}`);
    }
    check('activeRun', '[data-testid="chat-abort-btn"]', await page.locator('#evidence-agent-conversation-panel [data-testid="chat-abort-btn"]').isVisible());
    const pagePath = scenario.pagePath || '/chat/evidence-agent';
    if (pagePath !== '/chat/evidence-agent') {
      await page.goto(`http://127.0.0.1:5000${pagePath}`, { waitUntil: 'load' });
      await page.locator('main').first().waitFor({ state: 'visible', timeout: 60000 });
    }
    for (const selector of scenario.selectors || []) {
      const locator = page.locator(selector).first();
      await locator.waitFor({ state: 'visible', timeout: 10000 });
      check('targetSelector', selector, await locator.isVisible());
    }
    check('browserErrors', 'pageerror/console.error', browserErrors.length === 0, browserErrors.join('; '));
    const screenshotPath = '/evidence-output/portal.png';
    await page.screenshot({ path: screenshotPath, fullPage: false });
    const bytes = fs.readFileSync(screenshotPath);
    check('screenshot', 'portal.png', bytes.length > 0);
    const manifest = manifestBase(pagePath);
    manifest.screenshot = { path: 'portal.png', sha256: crypto.createHash('sha256').update(bytes).digest('hex') };
    manifest.accessibility.activeRunControls = activeRunControls;
    await browser.close(); browser = undefined;
    manifest.cleanup.browserClosed = true;
    fs.writeFileSync('/evidence-output/evidence.json', JSON.stringify(manifest, null, 2));
  }
} finally {
  if (browser) await browser.close();
}
