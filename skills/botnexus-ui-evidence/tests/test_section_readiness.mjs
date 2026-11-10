import fs from 'node:fs';
import vm from 'node:vm';
import assert from 'node:assert/strict';
import test from 'node:test';
const source = fs.readFileSync(new URL('../container/capture-sidebar.mjs', import.meta.url), 'utf8');
const AsyncFunction = Object.getPrototypeOf(async function () {}).constructor;
test('actual agent input fragment avoids same-agent refresh but selects a different agent', async () => {
  const start = source.indexOf("      await page.locator('[data-testid=\"agent-select\"]').selectOption('evidence-agent');");
  const guarded = source.indexOf('      const agentSelect =');
  const end = source.indexOf('      const custom =', guarded >= 0 ? guarded : start);
  const fragment = source.slice(guarded >= 0 ? guarded : start, end);
  for (const value of ['evidence-agent', 'other-agent']) {
    let selected = 0;
    const page = { locator: () => ({ inputValue: async () => value, selectOption: async actual => { assert.equal(actual, 'evidence-agent'); selected++; } }), waitForFunction: async () => {} };
    await new AsyncFunction('page', 'sectionsReady', 'sectionId', 'ids', fragment)(page, () => true, 'section', ['member']);
    assert.equal(selected, value === 'evidence-agent' ? 0 : 1);
  }
});
test('actual readiness predicate requires current agent, clean state, enabled canonical peers and expanded membership', () => {
  const start = source.indexOf('const sectionsReady =');
  assert.ok(start >= 0, 'Actual authoritative readiness predicate required.');
  const end = source.indexOf('\nconst openSidebar', start);
  const fixture = { agent: 'evidence-agent', busy: false, enabled: true, member: true, expanded: true, peers: true };
  const document = { querySelector: selector => {
    if (selector === '[data-testid="agent-select"]') return { value: fixture.agent };
    if (selector.includes('sections-loading')) return fixture.busy ? {} : null;
    if (selector.includes('section-collapse-toggle')) return fixture.peers ? { disabled: !fixture.enabled, getAttribute: () => fixture.expanded ? 'true' : 'false' } : null;
    if (selector.includes('a.conversation-list-item-btn')) return fixture.member ? {} : null;
    if (selector.includes('section-actions-trigger') || selector.includes('section-add-btn') || selector.includes('sections-refresh')) return fixture.peers ? { disabled: !fixture.enabled } : null;
    return null;
  } };
  const predicate = vm.runInNewContext(source.slice(start, end) + '\nsectionsReady', { document });
  const data = { sectionId: 'section', memberId: 'member', expanded: true };
  assert.equal(predicate(data), true);
  for (const [key, bad] of [['agent', 'other'], ['busy', true], ['enabled', false], ['member', false], ['expanded', false], ['peers', false]]) {
    const old = fixture[key]; fixture[key] = bad;
    assert.equal(predicate(data), false, key); fixture[key] = old;
  }
  fixture.expanded = false; fixture.member = false;
  assert.equal(predicate({ ...data, expanded: false }), true, 'Collapsed snapshot can become ready before the one real expansion input.');
});
test('readiness gates bracket expansion and precede every real menu preparation without retry input', () => {
  const selection = source.indexOf('      const agentSelect =');
  const firstWait = source.indexOf('await page.waitForFunction(sectionsReady', selection);
  const expansion = source.indexOf("await toggle.getAttribute('aria-expanded')", selection);
  const expandedWait = source.indexOf('await page.waitForFunction(sectionsReady', expansion);
  const loop = source.indexOf("for (const key of ['pinned', 'scheduled', 'automated', 'conversations'])", expandedWait);
  const perMenuWait = source.indexOf('await page.waitForFunction(sectionsReady', loop);
  const prepare = source.indexOf('await menuTrigger.scrollIntoViewIfNeeded()', loop);
  assert.ok(selection < firstWait && firstWait < expansion && expansion < expandedWait && expandedWait < loop && loop < perMenuWait && perMenuWait < prepare);
  assert.equal((source.slice(loop, source.indexOf('diagnosticContext.stage', loop)).match(/menuTrigger\.click\(/g) || []).length, 1);
  assert.ok(source.includes('expanded: true }, { timeout: 20000 }'));
});
