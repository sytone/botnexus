import fs from 'node:fs';
import crypto from 'node:crypto';
import playwright from '/app/playwright/node_modules/playwright-core/index.js';
const { chromium } = playwright;

const scenario = JSON.parse(process.env.BOTNEXUS_EVIDENCE_SCENARIO || '{}');
if (scenario.promptKey !== 'SLOW_STREAM') throw new Error('Only deterministic SLOW_STREAM evidence is accepted.');
const startedUtc = process.env.BOTNEXUS_EVIDENCE_STARTED_UTC;
const assertions = [];
const browserErrors = [];
let browser;
const check = (kind, target, passed, detail = '') => { assertions.push({ kind, target, passed, detail }); if (!passed) throw new Error(`${kind} failed: ${target} ${detail}`); };
try {
  browser = await chromium.launch({ headless: true });
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
  const agent = page.locator('[data-testid="agent-select"]').first();
  await agent.waitFor({ state: 'visible' });
  await agent.selectOption('evidence-agent');
  const create = page.locator('[data-testid="new-conversation-btn"]').first();
  await create.waitFor({ state: 'visible' });
  const conversationResponse = await page.request.post('http://127.0.0.1:5000/api/conversations', {
    data: { agentId: 'evidence-agent', title: 'UI Evidence' }
  });
  check('conversation', 'POST /api/conversations', conversationResponse.ok(), `status ${conversationResponse.status()}`);
  const conversation = await conversationResponse.json();
  const messageResponse = await page.request.post(`http://127.0.0.1:5000/api/agents/evidence-agent/conversations/${conversation.conversationId}/messages`, {
    data: { message: scenario.promptKey, wake: true, sender: 'ui-evidence' }
  });
  check('message', 'POST conversation message', messageResponse.ok(), `status ${messageResponse.status()}`);
  await page.reload({ waitUntil: 'load' });
  await page.locator('[data-testid="agent-panel"]').first().waitFor({ state: 'attached', timeout: 60000 });
  await page.locator('#evidence-agent-conversation-panel [data-testid="streaming-badge"]').waitFor({ state: 'visible', timeout: 10000 });
  for (const selector of ['[data-testid="streaming-badge"]', '[data-testid="chat-abort-btn"]']) {
    const locator = page.locator(`#evidence-agent-conversation-panel ${selector}`);
    await locator.waitFor({ state: 'visible', timeout: 10000 });
    check('activeRunSelector', selector, await locator.isVisible());
  }
  for (const name of scenario.accessibleNames) {
    const element = page.locator('#evidence-agent-conversation-panel').getByLabel(name, { exact: true });
    const visible = await element.isVisible();
    check('accessibility', name, visible, 'exact accessible name');
  }
  const activeRunControls = {};
  for (const testId of ['chat-steer-btn', 'chat-redirect-btn', 'chat-followup-btn', 'chat-abort-btn']) {
    const control = page.locator(`#evidence-agent-conversation-panel [data-testid="${testId}"]`).first();
    await control.waitFor({ state: 'visible', timeout: 10000 });
    const name = await control.evaluate(element => element.getAttribute('aria-label') || element.textContent?.trim() || element.getAttribute('title') || '');
    activeRunControls[testId] = name.replace(/\s+/g, ' ').trim();
    check('accessibility', testId, activeRunControls[testId].length > 0, `accessible name: ${activeRunControls[testId]}`);
  }
  check('activeRun', '[data-testid="chat-abort-btn"]', await page.locator('#evidence-agent-conversation-panel [data-testid="chat-abort-btn"]').isVisible());
  const pagePath = scenario.pagePath || '/chat/evidence-agent';
  if (pagePath !== '/chat/evidence-agent') {
    await page.goto(`http://127.0.0.1:5000${pagePath}`, { waitUntil: 'load' });
    await page.locator('main').first().waitFor({ state: 'visible', timeout: 60000 });
  }
  for (const selector of scenario.selectors) {
    const locator = page.locator(selector).first();
    await locator.waitFor({ state: 'visible', timeout: 10000 });
    check('targetSelector', selector, await locator.isVisible());
  }
  check('browserErrors', 'pageerror/console.error', browserErrors.length === 0, browserErrors.join('; '));
  const screenshotPath = '/evidence-output/portal.png';
  await page.screenshot({ path: screenshotPath, fullPage: false });
  const bytes = fs.readFileSync(screenshotPath);
  check('screenshot', 'portal.png', bytes.length > 0);
  const capturedUtc = new Date().toISOString();
  const manifest = {
    schemaVersion: '1.0',
    source: { commit: fs.readFileSync('/app/source-commit','utf8').trim(), tree: fs.readFileSync('/app/source-tree','utf8').trim() },
    scenario: { name: scenario.name, promptKey: scenario.promptKey, pagePath },
    viewport: scenario.viewport,
    screenshot: { path: 'portal.png', sha256: crypto.createHash('sha256').update(bytes).digest('hex') },
    assertions,
    accessibility: { activeRunControls },
    timestamps: { startedUtc, capturedUtc, completedUtc: new Date().toISOString() },
    cleanup: { gatewayStopped: false, browserClosed: false, temporaryDataRemoved: false, containerRemoved: false }
  };
  await browser.close(); browser = undefined;
  manifest.cleanup.browserClosed = true;
  
  fs.writeFileSync('/evidence-output/evidence.json', JSON.stringify(manifest, null, 2));
} finally {
  if (browser) await browser.close();
}
