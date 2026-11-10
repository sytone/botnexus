// Source/CSS contract only: no browser or host test process.
import fs from 'node:fs';
import assert from 'node:assert/strict';
import test from 'node:test';
const root = new URL('../../../', import.meta.url);
const css = fs.readFileSync(new URL('src/extensions/BotNexus.Extensions.Channels.SignalR.BlazorClient/wwwroot/css/app.css', root), 'utf8');
const panel = fs.readFileSync(new URL('src/extensions/BotNexus.Extensions.Channels.SignalR.BlazorClient/Components/ConversationSectionsPanel.razor', root), 'utf8');
const rules = [...css.matchAll(/([^{}]+)\{([^{}]*)\}/g)].map(match => ({ selectors: match[1].trim().split(',').map(value => value.trim()), body: match[2] }));
function shared(left, right) {
  const rule = rules.find(rule => rule.selectors.includes(left) && rule.selectors.includes(right));
  assert.ok(rule, `${right} must reuse ${left}, not browser-native or duplicate styling.`);
  return rule.body;
}
test('refresh reuses compact themed section button without fixed height or extra margins', () => {
  const body = shared('.section-add-btn', '.sections-refresh');
  for (const declaration of ['background: none', 'border: 1px solid var(--sidebar-border)', 'border-radius: var(--radius-sm)', 'color: inherit', 'font-size: var(--text-caption)', 'padding: 0.1rem 0.4rem']) assert.ok(body.includes(declaration), declaration);
  assert.equal(/(?:min-)?height\s*:|margin\s*:/.test(body), false);
});
test('refresh reuses hover, visible focus and disabled styling', () => {
  assert.ok(shared('.section-add-btn:hover', '.sections-refresh:hover').includes('background: var(--color-wash)'));
  assert.ok(shared('.focused-section-trigger:focus-visible', '.sections-refresh:focus-visible').includes('outline: 2px solid var(--accent)'));
  const disabled = shared('.section-add-btn:disabled', '.sections-refresh:disabled');
  assert.ok(disabled.includes('opacity: 0.5'));
  assert.ok(disabled.includes('cursor: not-allowed'));
});
test('refresh remains a native accessible button with existing busy guard and action', () => {
  assert.match(panel, /<button type="button" data-testid="sections-refresh" class="sections-refresh"\s+disabled="@\(SectionsState.IsLoading \|\| SectionsState.IsSaving\)" @onclick="SectionsState.RetryAsync">Refresh sections<\/button>/);
});
