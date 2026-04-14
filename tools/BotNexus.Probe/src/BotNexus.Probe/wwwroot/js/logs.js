/* Log Browser — BotNexus Probe */

(function () {
  'use strict';

  const PAGE_SIZE = 50;
  let currentSkip = 0;
  let totalCount = 0;
  let allFiles = [];

  document.addEventListener('DOMContentLoaded', () => {
    loadFiles();
    applyParamsToFilters();
    doSearch();

    // Enter key triggers search on filter inputs
    $$('#toolbar input').forEach(inp => {
      inp.addEventListener('keydown', (e) => { if (e.key === 'Enter') doSearch(); });
    });
  });

  async function loadFiles() {
    try {
      const data = await ProbeApi.getLogFiles();
      allFiles = Array.isArray(data) ? data : (data.files || []);
      const sel = $('#file-select');
      allFiles.forEach(f => {
        const name = f.name || f.fileName || f;
        const opt = el('option', { value: name, text: name });
        sel.appendChild(opt);
      });
      // Pre-select from URL param
      const fp = getParam('file');
      if (fp) sel.value = fp;
    } catch { /* ignore */ }
  }

  function applyParamsToFilters() {
    const map = {
      level: '#filter-level', session: '#filter-session',
      correlation: '#filter-correlation', agent: '#filter-agent',
      text: '#filter-text', from: '#filter-from', to: '#filter-to'
    };
    for (const [param, sel] of Object.entries(map)) {
      const v = getParam(param);
      if (v) $(sel).value = v;
    }
  }

  function getFilters() {
    return {
      level: $('#filter-level').value || undefined,
      sessionId: $('#filter-session').value || undefined,
      correlationId: $('#filter-correlation').value || undefined,
      agentId: $('#filter-agent').value || undefined,
      search: $('#filter-text').value || undefined,
      from: $('#filter-from').value ? new Date($('#filter-from').value).toISOString() : undefined,
      to: $('#filter-to').value ? new Date($('#filter-to').value).toISOString() : undefined,
      file: $('#file-select').value || undefined,
      skip: currentSkip,
      take: PAGE_SIZE
    };
  }

  window.doSearch = async function () {
    currentSkip = 0;
    await loadLogs();
  };

  window.doClear = function () {
    $$('#toolbar input, #toolbar select').forEach(el => {
      if (el.id !== 'file-select') el.value = '';
    });
    currentSkip = 0;
    loadLogs();
  };

  window.onFileChange = function () {
    currentSkip = 0;
    loadLogs();
  };

  window.nextPage = function () {
    if (currentSkip + PAGE_SIZE < totalCount) {
      currentSkip += PAGE_SIZE;
      loadLogs();
    }
  };

  window.prevPage = function () {
    if (currentSkip > 0) {
      currentSkip = Math.max(0, currentSkip - PAGE_SIZE);
      loadLogs();
    }
  };

  async function loadLogs() {
    const body = $('#log-body');
    body.innerHTML = '<tr><td colspan="6"><div class="loading">Loading…</div></td></tr>';

    try {
      const params = getFilters();
      const data = await ProbeApi.getLogs(params);
      const entries = Array.isArray(data) ? data : (data.items || data.entries || data.logs || []);
      totalCount = data.totalCount || data.total || data.count || entries.length + currentSkip + (entries.length === PAGE_SIZE ? 1 : 0);

      updateStats(entries, totalCount);
      updatePagination();

      if (entries.length === 0) {
        body.innerHTML = '<tr><td colspan="6"><div class="empty-state"><div class="icon">📄</div><p>No log entries match your filters</p></div></td></tr>';
        return;
      }

      body.innerHTML = '';
      entries.forEach((entry, idx) => {
        const row = createLogRow(entry, idx);
        body.appendChild(row);
        const detailRow = createDetailRow(entry);
        body.appendChild(detailRow);
      });
    } catch (err) {
      body.innerHTML = `<tr><td colspan="6"><div class="error-state">⚠️ ${escapeHtml(err.message)}</div></td></tr>`;
    }
  }

  function createLogRow(entry, idx) {
    const level = entry.level || 'Information';
    const shortLevel = level.substring(0, 4).toUpperCase();
    const tr = el('tr', { class: 'clickable', 'data-idx': String(idx) });
    tr.onclick = () => toggleDetail(idx);

    tr.appendChild(el('td', { class: 'mono-sm', title: formatTimestampUTC(entry.timestamp) }, [
      document.createTextNode(formatTimestamp(entry.timestamp))
    ]));
    tr.appendChild(el('td', {}, [el('span', { class: `badge ${levelClass(level)}`, text: shortLevel })]));
    tr.appendChild(el('td', { text: truncate(entry.message || entry.renderedMessage || '', 120) }));
    tr.appendChild(el('td', {}, [
      entry.sessionId
        ? el('span', { class: 'id-link', text: truncate(entry.sessionId, 12), onclick: (e) => { e.stopPropagation(); goSession(entry.sessionId); } })
        : document.createTextNode('—')
    ]));
    tr.appendChild(el('td', {}, [
      entry.correlationId
        ? el('span', { class: 'id-link', text: truncate(entry.correlationId, 12), onclick: (e) => { e.stopPropagation(); goCorrelate(entry.correlationId); } })
        : document.createTextNode('—')
    ]));
    tr.appendChild(el('td', { class: 'mono-sm', text: truncate(entry.agentId || entry.agent || '—', 12) }));
    return tr;
  }

  function createDetailRow(entry) {
    const tr = el('tr', { class: 'log-detail' });
    const td = el('td', { colspan: '6' });

    // Properties table
    const props = entry.properties || {};
    const allProps = { ...props };
    if (entry.exception) allProps['Exception'] = entry.exception;
    if (entry.sourceContext) allProps['SourceContext'] = entry.sourceContext;

    const keys = Object.keys(allProps);
    if (keys.length > 0) {
      const tbl = el('table', { class: 'props-table' });
      keys.forEach(k => {
        const row = el('tr', {}, [
          el('td', { text: k }),
          el('td', {}, [el('code', { text: typeof allProps[k] === 'object' ? JSON.stringify(allProps[k], null, 2) : String(allProps[k]) })])
        ]);
        tbl.appendChild(row);
      });
      td.appendChild(tbl);
    }

    if (entry.exception) {
      td.appendChild(el('pre', { class: 'mt-8', text: entry.exception }));
    }

    if (entry.renderedMessage && entry.renderedMessage !== entry.message) {
      const p = el('div', { class: 'mt-8 text-sm text-muted' });
      p.innerHTML = `<strong>Full message:</strong> ${escapeHtml(entry.renderedMessage)}`;
      td.appendChild(p);
    }

    tr.appendChild(td);
    return tr;
  }

  function toggleDetail(idx) {
    const details = $$('.log-detail');
    const rows = $$('tr[data-idx]');
    if (details[idx]) {
      const isOpen = details[idx].classList.contains('open');
      // Close all
      details.forEach(d => d.classList.remove('open'));
      rows.forEach(r => r.classList.remove('selected'));
      // Toggle current
      if (!isOpen) {
        details[idx].classList.add('open');
        rows[idx].classList.add('selected');
      }
    }
  }

  function updateStats(entries, total) {
    const bar = $('#stats-bar');
    const levels = {};
    entries.forEach(e => {
      const l = (e.level || 'info').toLowerCase();
      levels[l] = (levels[l] || 0) + 1;
    });
    const parts = [`Showing ${entries.length} of ${total}`];
    for (const [l, c] of Object.entries(levels)) {
      parts.push(`<span class="${levelClass(l)}" style="font-size:11px">${l.substring(0,4).toUpperCase()}: ${c}</span>`);
    }
    bar.innerHTML = parts.join(' &nbsp;|&nbsp; ');
  }

  function updatePagination() {
    const page = Math.floor(currentSkip / PAGE_SIZE) + 1;
    const maxPage = Math.max(1, Math.ceil(totalCount / PAGE_SIZE));
    $('#page-num').textContent = `Page ${page} of ${maxPage}`;
    $('#page-info').textContent = `${currentSkip + 1}–${Math.min(currentSkip + PAGE_SIZE, totalCount)} of ${totalCount}`;
    $('#btn-prev').disabled = currentSkip === 0;
    $('#btn-next').disabled = currentSkip + PAGE_SIZE >= totalCount;
  }
})();
