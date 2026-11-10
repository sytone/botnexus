// Executed by Playwright addInitScript: observations only, no product event interception.
export function installScrollDiagnostics() {
  const events = { scroll: 0, resize: 0, focus: 0, pointer: 0 };
  const trace = [];
  let sequence = 0;
  let phase = 'setup';
  const phases = ['setup', 'before-scroll-into-view', 'after-scroll-into-view', 'before-click', 'after-click', 'menu-visible'];
  const category = target => {
    if (target === document || target === document.documentElement || target === document.body || target === window) return 'document';
    if (target?.matches?.('.focused-chat-scroll')) return 'focused-chat-scroll';
    if (target?.matches?.('[data-testid="focused-section-content"]')) return 'focused-content';
    if (target?.matches?.('[data-testid="section-actions-trigger"]')) return 'section-trigger';
    if (target?.matches?.('[role="menuitem"]')) return 'menu-item';
    if (target?.matches?.('.conversation-action-menu')) return 'menu';
    if (target?.matches?.('.conversation-action-overlay')) return 'overlay';
    return 'other';
  };
  const numeric = value => typeof value === 'number' && Number.isFinite(value) ? Math.min(10000000, Math.max(-10000000, value)) : null;
  const record = (type, target) => {
    const owner = document.querySelector('.focused-chat-scroll');
    trace.push({
      sequence: ++sequence, type, phase, target: category(target),
      scrollTop: numeric(category(target) === 'document' ? document.scrollingElement?.scrollTop : target?.scrollTop),
      ownerScrollTop: numeric(owner?.scrollTop),
      expanded: !!document.querySelector('[data-testid="section-actions-trigger"][aria-expanded="true"]'),
      popover: !!document.querySelector('.conversation-action-overlay:popover-open')
    });
    if (trace.length > 32) trace.shift();
  };
  window.__sidebarDiagnosticEvents = events;
  window.__sidebarDiagnosticTrace = trace;
  window.__sidebarDiagnosticMark = value => {
    phase = phases.includes(value) ? value : 'setup';
    record('marker', document.querySelector('[data-testid="section-actions-trigger"]'));
  };
  for (const [type, key] of [['scroll', 'scroll'], ['resize', 'resize'], ['focusin', 'focus'], ['pointerdown', 'pointer']]) {
    window.addEventListener(type, event => {
      events[key] = Math.min(events[key] + 1, 100000);
      record(type, event.target);
    }, true);
  }
}
