/* ============================================================
   BotNexus Probe — Shared Utilities & API Client
   ============================================================ */

// --------------- API Client ---------------

class ProbeApi {
  static #base = '';

  static async #get(url) {
    const resp = await fetch(this.#base + url);
    if (!resp.ok) throw new Error(`API ${resp.status}: ${resp.statusText}`);
    return resp.json();
  }

  /** Get log entries with optional filters */
  static async getLogs(params = {}) {
    const q = new URLSearchParams();
    for (const [k, v] of Object.entries(params)) {
      if (v !== undefined && v !== null && v !== '') q.set(k, v);
    }
    const qs = q.toString();
    return this.#get('/api/logs' + (qs ? '?' + qs : ''));
  }

  /** List available log files */
  static async getLogFiles() {
    return this.#get('/api/logs/files');
  }

  /** List all sessions */
  static async getSessions(params = {}) {
    const q = new URLSearchParams();
    for (const [k, v] of Object.entries(params)) {
      if (v !== undefined && v !== null && v !== '') q.set(k, v);
    }
    const qs = q.toString();
    return this.#get('/api/sessions' + (qs ? '?' + qs : ''));
  }

  /** Get session detail with pagination */
  static async getSession(id, skip = 0, take = 50) {
    return this.#get(`/api/sessions/${encodeURIComponent(id)}?skip=${skip}&take=${take}`);
  }

  static async searchSession(id, q, skip = 0, take = 50) {
    return this.#get(`/api/sessions/${encodeURIComponent(id)}/search?q=${encodeURIComponent(q)}&skip=${skip}&take=${take}`);
  }

  /** List recent traces */
  static async getTraces() {
    return this.#get('/api/traces');
  }

  /** Get trace detail by traceId */
  static async getTrace(traceId) {
    return this.#get(`/api/traces/${encodeURIComponent(traceId)}`);
  }

  /** Gateway connection status */
  static async getGatewayStatus() {
    return this.#get('/api/gateway/status');
  }

  /** Gateway recent logs */
  static async getGatewayLogs() {
    return this.#get('/api/gateway/logs');
  }

  /** Gateway registered agents */
  static async getGatewayAgents() {
    return this.#get('/api/gateway/agents');
  }

  /** Correlation pivot — search across all sources */
  static async correlate(id) {
    return this.#get(`/api/correlate/${encodeURIComponent(id)}`);
  }

  /** Gateway live activity stream (SSE) */
  static getGatewayActivity(onMessage, onError) {
    const src = new EventSource(this.#base + '/api/gateway/activity');
    src.onmessage = (e) => {
      try { onMessage(JSON.parse(e.data)); }
      catch { onMessage(e.data); }
    };
    src.onerror = (e) => {
      if (onError) onError(e);
    };
    return src;
  }
}

// --------------- Formatting Helpers ---------------

function formatTimestamp(iso) {
  if (!iso) return '—';
  const d = new Date(iso);
  if (isNaN(d.getTime())) return iso;
  const pad = (n) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ` +
         `${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}.${String(d.getMilliseconds()).padStart(3, '0')}`;
}

function formatTimestampShort(iso) {
  if (!iso) return '—';
  const d = new Date(iso);
  if (isNaN(d.getTime())) return iso;
  const pad = (n) => String(n).padStart(2, '0');
  return `${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}.${String(d.getMilliseconds()).padStart(3, '0')}`;
}

function formatTimestampUTC(iso) {
  if (!iso) return '';
  const d = new Date(iso);
  return isNaN(d.getTime()) ? '' : d.toISOString();
}

function formatDuration(ms) {
  if (ms === undefined || ms === null) return '—';
  if (ms < 1) return '<1ms';
  if (ms < 1000) return `${Math.round(ms)}ms`;
  if (ms < 60000) return `${(ms / 1000).toFixed(1)}s`;
  const m = Math.floor(ms / 60000);
  const s = Math.round((ms % 60000) / 1000);
  return `${m}m ${s}s`;
}

function escapeHtml(str) {
  if (!str) return '';
  const div = document.createElement('div');
  div.textContent = str;
  return div.innerHTML;
}

function truncate(str, maxLen = 80) {
  if (!str) return '';
  return str.length > maxLen ? str.slice(0, maxLen) + '…' : str;
}

function levelClass(level) {
  if (!level) return 'badge-info';
  const l = level.toLowerCase().replace('warning', 'warn').replace('critical', 'fatal');
  const map = { debug: 'badge-debug', trace: 'badge-debug', verbose: 'badge-debug',
                info: 'badge-info', information: 'badge-info',
                warn: 'badge-warn', warning: 'badge-warn',
                error: 'badge-error', err: 'badge-error',
                fatal: 'badge-fatal', critical: 'badge-fatal' };
  return map[l] || 'badge-info';
}

function roleClass(role) {
  if (!role) return 'badge-system';
  const r = role.toLowerCase();
  if (r === 'user') return 'badge-user';
  if (r === 'assistant') return 'badge-assistant';
  if (r === 'tool') return 'badge-tool';
  return 'badge-system';
}

// --------------- Utility ---------------

function debounce(fn, ms = 300) {
  let t;
  return (...args) => { clearTimeout(t); t = setTimeout(() => fn(...args), ms); };
}

function copyToClipboard(text) {
  navigator.clipboard.writeText(text).then(() => {
    showToast('Copied to clipboard');
  }).catch(() => {});
}

function showToast(msg, durationMs = 2000) {
  let toast = document.getElementById('probe-toast');
  if (!toast) {
    toast = document.createElement('div');
    toast.id = 'probe-toast';
    toast.style.cssText = 'position:fixed;bottom:24px;right:24px;background:#0f3460;color:#eee;' +
      'padding:10px 20px;border-radius:6px;font-size:13px;z-index:9999;opacity:0;transition:opacity 0.3s;' +
      'border:1px solid #2a2a4a;pointer-events:none;';
    document.body.appendChild(toast);
  }
  toast.textContent = msg;
  toast.style.opacity = '1';
  clearTimeout(toast._timer);
  toast._timer = setTimeout(() => { toast.style.opacity = '0'; }, durationMs);
}

// --------------- Navigation ---------------

function initNav() {
  const path = window.location.pathname;
  document.querySelectorAll('.nav-links a').forEach(a => {
    const href = a.getAttribute('href');
    if (href === '/' && (path === '/' || path === '/index.html')) {
      a.classList.add('active');
    } else if (href !== '/' && path.startsWith(href)) {
      a.classList.add('active');
    }
  });
}

function navigateTo(url) {
  window.location.href = url;
}

function goCorrelate(id) {
  if (id) navigateTo(`/correlate.html?id=${encodeURIComponent(id)}`);
}

function goSession(id) {
  if (id) navigateTo(`/sessions.html?id=${encodeURIComponent(id)}`);
}

// --------------- DOM Helpers ---------------

function $(sel, parent) { return (parent || document).querySelector(sel); }
function $$(sel, parent) { return [...(parent || document).querySelectorAll(sel)]; }

function el(tag, attrs, children) {
  const e = document.createElement(tag);
  if (attrs) {
    for (const [k, v] of Object.entries(attrs)) {
      if (k === 'class') e.className = v;
      else if (k === 'text') e.textContent = v;
      else if (k === 'html') e.innerHTML = v;
      else if (k.startsWith('on')) e.addEventListener(k.slice(2), v);
      else if (k === 'style' && typeof v === 'object') Object.assign(e.style, v);
      else if (k === 'title') e.title = v;
      else e.setAttribute(k, v);
    }
  }
  if (children) {
    if (typeof children === 'string') e.textContent = children;
    else if (Array.isArray(children)) children.forEach(c => { if (c) e.appendChild(c); });
    else e.appendChild(children);
  }
  return e;
}

function setLoading(container) {
  container.innerHTML = '<div class="loading">Loading…</div>';
}

function setEmpty(container, icon, message) {
  container.innerHTML = `<div class="empty-state"><div class="icon">${icon}</div><p>${escapeHtml(message)}</p></div>`;
}

function setError(container, message) {
  container.innerHTML = `<div class="error-state"><div class="icon">⚠️</div><p>${escapeHtml(message)}</p></div>`;
}

// --------------- URL Params ---------------

function getParam(name) {
  return new URLSearchParams(window.location.search).get(name);
}

function setParams(params) {
  const u = new URL(window.location);
  for (const [k, v] of Object.entries(params)) {
    if (v === null || v === undefined || v === '') u.searchParams.delete(k);
    else u.searchParams.set(k, v);
  }
  window.history.replaceState(null, '', u);
}

// --------------- Init ---------------
document.addEventListener('DOMContentLoaded', initNav);
