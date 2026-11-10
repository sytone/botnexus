// Private failure-only projection. Never copies HTML, text, URLs, input values or raw logs.
import fs from 'node:fs';

export const classifyError = value => {
  const text = String(value);
  if (/timeout/i.test(text)) return 'timeout';
  if (/conversation menu callback failed/i.test(text)) return 'menu-callback';
  if (/disconnected|disposed/i.test(text)) return 'disconnected';
  return 'other';
};

export async function collectFailure(page, context, errors, directory = '/evidence-output/private-failure') {
  fs.mkdirSync(directory, { recursive: true });
  const snapshot = await page.evaluate(() => {
    const rect = element => {
      const box = element.getBoundingClientRect();
      return { x: box.x, y: box.y, width: box.width, height: box.height };
    };
    const project = selector => Array.from(document.querySelectorAll(selector)).slice(0, 24).map(element => {
      const style = getComputedStyle(element);
      return {
        tag: element.tagName.toLowerCase(),
        expanded: element.getAttribute('aria-expanded'),
        disabled: element.disabled === true,
        focused: document.activeElement === element,
        popover: element.matches('[popover]') && element.matches(':popover-open'),
        visible: style.display !== 'none' && style.visibility !== 'hidden' && element.getBoundingClientRect().width > 0 && element.getBoundingClientRect().height > 0,
        rect: rect(element),
        scrollTop: element.scrollTop, scrollHeight: element.scrollHeight, clientHeight: element.clientHeight
      };
    });
    return {
      triggers: project('[data-testid="section-actions-trigger"]'),
      overlays: project('.conversation-action-overlay'),
      menus: project('.conversation-action-menu'),
      sidebar: project('.main-sidebar'),
      scrollOwners: project('.focused-chat-scroll'),
      events: Object.fromEntries(['scroll', 'resize', 'focus', 'pointer'].map(key => [key, Math.min(100000, Math.max(0, Number(window.__sidebarDiagnosticEvents?.[key]) || 0))])),
      recentEvents: (window.__sidebarDiagnosticTrace || []).slice(-32).map(item => ({
        type: ['scroll', 'resize', 'focusin', 'pointerdown'].includes(item.type) ? item.type : 'other',
        expanded: item.expanded === true, popover: item.popover === true
      }))
    };
  });
  // Whitelist scenario coordinates, not arbitrary failure/error strings or page content.
  const packet = {
    schemaVersion: 1, diagnosticOnly: true,
    stage: ['setup', 'section-menu', 'journey'].includes(context.stage) ? context.stage : 'unknown',
    group: ['pinned', 'scheduled', 'automated', 'conversations'].includes(context.group) ? context.group : null,
    width: [600, 1440].includes(context.width) ? context.width : null,
    theme: ['light', 'dark'].includes(context.theme) ? context.theme : null,
    errors: errors.slice(0, 20).map(classifyError), snapshot
  };
  fs.writeFileSync(`${directory}/failure.json`, JSON.stringify(packet, null, 2));
  // Capture only the task-seeded sidebar, not the conversation/configuration page.
  await page.locator('.main-sidebar').screenshot({ path: `${directory}/failure.png`, timeout: 5000 });
}
