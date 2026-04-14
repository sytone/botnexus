/* Trace Viewer — BotNexus Probe */

(function () {
  'use strict';

  let allTraces = [];
  let currentTrace = null;
  let selectedSpanId = null;

  document.addEventListener('DOMContentLoaded', loadTraces);

  async function loadTraces() {
    const body = $('#trace-body');
    body.innerHTML = '<tr><td colspan="7"><div class="loading">Loading…</div></td></tr>';
    try {
      const data = await ProbeApi.getTraces();
      allTraces = Array.isArray(data) ? data : (data.traces || []);
      $('#trace-count').textContent = `${allTraces.length} trace(s)`;

      if (allTraces.length === 0) {
        body.innerHTML = '';
        const emptyDiv = $('#trace-list-empty');
        // Check if OTLP is likely disabled
        if (data.otlpEnabled === false) {
          $('#otlp-notice').classList.remove('hidden');
        }
        setEmpty(emptyDiv, '🔗', 'No traces captured yet');
        return;
      }

      body.innerHTML = '';
      allTraces.forEach(t => {
        const traceId = t.traceId || t.id || '';
        const shortId = traceId.length > 16 ? traceId.substring(0, 16) + '…' : traceId;
        const status = t.status || t.rootStatus || 'OK';
        const statusClass = status.toLowerCase().includes('error') ? 'text-error' : 'text-success';

        const tr = el('tr', { class: 'clickable', onclick: () => openTraceDetail(traceId) }, [
          el('td', { class: 'mono-sm' }, [el('span', { class: 'id-link', text: shortId, title: traceId })]),
          el('td', { text: t.rootService || t.serviceName || '—' }),
          el('td', { text: truncate(t.rootOperation || t.operationName || '—', 40) }),
          el('td', { class: 'mono-sm', text: formatDuration(t.duration || t.durationMs) }),
          el('td', { class: 'mono-sm', text: String(t.spanCount || t.spans?.length || '—') }),
          el('td', {}, [el('span', { class: statusClass, text: status })]),
          el('td', { class: 'mono-sm', text: formatTimestamp(t.timestamp || t.startTime), title: formatTimestampUTC(t.timestamp || t.startTime) })
        ]);
        body.appendChild(tr);
      });

      // Auto-open from URL param
      const tid = getParam('traceId');
      if (tid) openTraceDetail(tid);
    } catch (err) {
      body.innerHTML = `<tr><td colspan="7"><div class="error-state">⚠️ ${escapeHtml(err.message)}</div></td></tr>`;
      // Likely OTLP not enabled
      if (err.message.includes('404')) {
        $('#otlp-notice').classList.remove('hidden');
      }
    }
  }

  async function openTraceDetail(traceId) {
    $('#trace-list-section').classList.add('hidden');
    const detailSection = $('#trace-detail-section');
    detailSection.classList.remove('hidden');
    $('#trace-detail-title').textContent = `Trace: ${traceId.substring(0, 24)}…`;
    setParams({ traceId });

    const wfContainer = $('#waterfall-container');
    setLoading(wfContainer);
    $('#span-detail').classList.add('hidden');

    try {
      const data = await ProbeApi.getTrace(traceId);
      currentTrace = data;
      renderWaterfall(data);
    } catch (err) {
      setError(wfContainer, `Failed to load trace: ${err.message}`);
    }
  }

  window.closeTraceDetail = function () {
    $('#trace-list-section').classList.remove('hidden');
    $('#trace-detail-section').classList.add('hidden');
    setParams({ traceId: null });
    currentTrace = null;
    selectedSpanId = null;
  };

  function renderWaterfall(data) {
    const container = $('#waterfall-container');
    container.innerHTML = '';

    const spans = data.spans || data.resourceSpans || [];
    if (spans.length === 0) {
      setEmpty(container, '🔗', 'No spans in this trace');
      return;
    }

    // Build hierarchy
    const flat = flattenSpans(spans);
    if (flat.length === 0) {
      setEmpty(container, '🔗', 'No spans in this trace');
      return;
    }

    // Calculate time boundaries
    const traceStart = Math.min(...flat.map(s => s.startNano));
    const traceEnd = Math.max(...flat.map(s => s.endNano));
    const traceDuration = traceEnd - traceStart || 1;

    // Header
    const header = el('div', { class: 'waterfall-header' }, [
      el('div', { text: 'Service / Operation' }),
      el('div', { text: `Duration: ${formatDuration((traceDuration) / 1e6)}` })
    ]);
    container.appendChild(header);

    // Build tree order
    const tree = buildTree(flat);

    tree.forEach(node => {
      renderSpanRow(container, node, traceStart, traceDuration, 0);
    });
  }

  function flattenSpans(spans) {
    const result = [];
    // Handle both OTLP format and simple array
    if (Array.isArray(spans) && spans.length > 0 && spans[0].spanId) {
      // Simple flat array
      spans.forEach(s => {
        result.push({
          spanId: s.spanId || s.id,
          parentSpanId: s.parentSpanId || s.parentId || null,
          service: s.serviceName || s.service || '—',
          operation: s.operationName || s.name || '—',
          startNano: toNano(s.startTime || s.start),
          endNano: toNano(s.endTime || s.end),
          status: s.status || s.statusCode || 'Unset',
          attributes: s.attributes || {},
          raw: s
        });
      });
    } else {
      // OTLP ResourceSpans format
      spans.forEach(rs => {
        const resource = rs.resource || {};
        const svcName = resource.attributes?.find?.(a => a.key === 'service.name')?.value?.stringValue || '—';
        const scopeSpans = rs.scopeSpans || rs.instrumentationLibrarySpans || [];
        scopeSpans.forEach(ss => {
          (ss.spans || []).forEach(s => {
            result.push({
              spanId: s.spanId,
              parentSpanId: s.parentSpanId || null,
              service: svcName,
              operation: s.name || '—',
              startNano: Number(s.startTimeUnixNano || 0),
              endNano: Number(s.endTimeUnixNano || 0),
              status: s.status?.code === 2 ? 'Error' : (s.status?.code === 1 ? 'OK' : 'Unset'),
              attributes: s.attributes || [],
              raw: s
            });
          });
        });
      });
    }
    return result.sort((a, b) => a.startNano - b.startNano);
  }

  function toNano(t) {
    if (!t) return 0;
    if (typeof t === 'number' && t > 1e15) return t; // already nano
    if (typeof t === 'number') return t * 1e6; // ms to nano
    return new Date(t).getTime() * 1e6;
  }

  function buildTree(flat) {
    const map = new Map();
    const roots = [];
    flat.forEach(s => map.set(s.spanId, { ...s, children: [] }));
    flat.forEach(s => {
      const node = map.get(s.spanId);
      if (s.parentSpanId && map.has(s.parentSpanId)) {
        map.get(s.parentSpanId).children.push(node);
      } else {
        roots.push(node);
      }
    });
    return roots;
  }

  function renderSpanRow(container, node, traceStart, traceDuration, depth) {
    const durationNano = node.endNano - node.startNano;
    const durationMs = durationNano / 1e6;
    const leftPct = ((node.startNano - traceStart) / traceDuration) * 100;
    const widthPct = Math.max(0.5, (durationNano / traceDuration) * 100);
    const statusClass = node.status.toLowerCase().includes('error') ? 'error' : (node.status === 'OK' ? 'ok' : 'unset');

    const row = el('div', { class: 'span-row', 'data-span': node.spanId });
    row.onclick = () => selectSpan(node);

    // Label
    const label = el('div', { class: 'span-label' });
    if (depth > 0) label.appendChild(el('span', { class: 'indent', style: { width: `${depth * 16}px` } }));
    label.appendChild(el('span', { class: 'svc', text: truncate(node.service, 14) }));
    label.appendChild(document.createTextNode(truncate(node.operation, 24)));
    row.appendChild(label);

    // Bar area
    const barArea = el('div', { class: 'span-bar-area' });
    barArea.appendChild(el('div', { class: `span-bar ${statusClass}`, style: { left: `${leftPct}%`, width: `${widthPct}%` } }));
    barArea.appendChild(el('span', { class: 'span-duration', text: formatDuration(durationMs) }));
    row.appendChild(barArea);

    container.appendChild(row);

    // Recurse children
    (node.children || []).forEach(child => renderSpanRow(container, child, traceStart, traceDuration, depth + 1));
  }

  function selectSpan(node) {
    selectedSpanId = node.spanId;

    // Highlight row
    $$('.span-row').forEach(r => r.classList.toggle('selected', r.dataset.span === node.spanId));

    const panel = $('#span-detail');
    panel.classList.remove('hidden');
    panel.innerHTML = '';

    panel.appendChild(el('div', { class: 'span-detail' }, [
      el('h3', { text: `${node.service} — ${node.operation}` }),
      buildAttrTable(node)
    ]));
  }

  function buildAttrTable(node) {
    const tbl = el('table', { class: 'kv-table' });
    const durationMs = (node.endNano - node.startNano) / 1e6;

    const baseRows = [
      ['Span ID', node.spanId],
      ['Parent Span ID', node.parentSpanId || '(root)'],
      ['Service', node.service],
      ['Operation', node.operation],
      ['Duration', formatDuration(durationMs)],
      ['Status', node.status]
    ];
    baseRows.forEach(([k, v]) => {
      tbl.appendChild(el('tr', {}, [
        el('td', { text: k }),
        el('td', { text: v })
      ]));
    });

    // Attributes
    const botnexusKeys = ['botnexus.correlation.id', 'botnexus.session.id', 'botnexus.agent.id'];
    const attrs = Array.isArray(node.attributes) ? node.attributes : Object.entries(node.attributes || {}).map(([k, v]) => ({ key: k, value: v }));

    attrs.forEach(attr => {
      const key = attr.key || '';
      const val = attr.value?.stringValue || attr.value?.intValue || attr.value || '';
      const isHighlight = botnexusKeys.some(bk => key.includes(bk));
      const tr = el('tr', { class: isHighlight ? 'highlight' : '' }, [
        el('td', { text: key }),
        el('td', {}, [
          isHighlight
            ? el('span', { class: 'id-link', text: String(val), onclick: () => goCorrelate(String(val)) })
            : document.createTextNode(String(val))
        ])
      ]);
      tbl.appendChild(tr);
    });

    return tbl;
  }
})();
