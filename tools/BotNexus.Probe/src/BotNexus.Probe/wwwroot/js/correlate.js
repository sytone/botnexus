/* Correlation Pivot — BotNexus Probe */

(function () {
  'use strict';

  document.addEventListener('DOMContentLoaded', () => {
    const input = $('#correlate-input');
    input.addEventListener('keydown', (e) => { if (e.key === 'Enter') doCorrelate(); });

    // Auto-search from URL param
    const id = getParam('id');
    if (id) {
      input.value = id;
      doCorrelate();
    }
  });

  window.doCorrelate = async function () {
    const input = $('#correlate-input');
    const id = (input.value || '').trim();
    if (!id) return;

    setParams({ id });
    const results = $('#correlate-results');
    const timeline = $('#correlate-timeline');
    setLoading(results);
    timeline.classList.add('hidden');

    try {
      const data = await ProbeApi.correlate(id);
      renderResults(data, id);
    } catch (err) {
      setError(results, `Correlation failed: ${err.message}`);
    }
  };

  function renderResults(data, searchId) {
    const container = $('#correlate-results');
    container.innerHTML = '';

    const logs = data.logs || data.logEntries || [];
    const sessions = data.sessions || data.sessionMessages || [];
    const traces = data.traces || data.traceSpans || [];

    const totalItems = logs.length + sessions.length + traces.length;
    if (totalItems === 0) {
      setEmpty(container, '🔎', `No results found for "${searchId}"`);
      return;
    }

    // Summary bar
    const summary = el('div', { class: 'flex gap-16 mb-16', style: { fontSize: '14px' } });
    if (logs.length) summary.appendChild(el('span', { text: `📋 ${logs.length} log entries` }));
    if (sessions.length) summary.appendChild(el('span', { text: `💬 ${sessions.length} session messages` }));
    if (traces.length) summary.appendChild(el('span', { text: `🔗 ${traces.length} trace spans` }));
    container.appendChild(summary);

    // Log section
    if (logs.length > 0) {
      container.appendChild(renderSection('📋 Log Entries', logs.length, () => renderLogsTable(logs)));
    }

    // Session section
    if (sessions.length > 0) {
      container.appendChild(renderSection('💬 Sessions', sessions.length, () => renderSessionsList(sessions)));
    }

    // Trace section
    if (traces.length > 0) {
      container.appendChild(renderSection('🔗 Traces', traces.length, () => renderTracesTable(traces)));
    }

    // Build unified timeline
    buildTimeline(logs, sessions, traces);
  }

  function renderSection(title, count, contentFn) {
    const section = el('div', { class: 'section' });

    const header = el('div', { class: 'collapsible-header open' });
    header.appendChild(el('span', { text: `${title} (${count})`, style: { fontWeight: '600' } }));
    header.onclick = () => {
      header.classList.toggle('open');
      body.classList.toggle('open');
    };
    section.appendChild(header);

    const body = el('div', { class: 'collapsible-body open' });
    body.appendChild(contentFn());
    section.appendChild(body);

    return section;
  }

  function renderLogsTable(logs) {
    const wrap = el('div', { class: 'table-wrap' });
    const table = el('table');
    table.appendChild(el('thead', {}, [
      el('tr', {}, [
        el('th', { text: 'Time' }),
        el('th', { text: 'Level' }),
        el('th', { text: 'Message' }),
        el('th', { text: 'Source' })
      ])
    ]));
    const tbody = el('tbody');
    logs.forEach(entry => {
      const level = entry.level || 'info';
      tbody.appendChild(el('tr', { class: 'clickable', onclick: () => navigateTo(`/logs.html?correlation=${encodeURIComponent(entry.correlationId || '')}`) }, [
        el('td', { class: 'mono-sm', text: formatTimestamp(entry.timestamp), title: formatTimestampUTC(entry.timestamp) }),
        el('td', {}, [el('span', { class: `badge ${levelClass(level)}`, text: level.substring(0, 4).toUpperCase() })]),
        el('td', { text: truncate(entry.message || entry.renderedMessage || '', 80) }),
        el('td', { class: 'mono-sm text-muted', text: truncate(entry.sourceContext || '', 30) })
      ]));
    });
    table.appendChild(tbody);
    wrap.appendChild(table);
    return wrap;
  }

  function renderSessionsList(sessions) {
    const list = el('div', { class: 'message-list' });
    sessions.forEach(msg => {
      const role = msg.role || msg.type || 'system';
      const item = el('div', { class: 'message-item clickable', onclick: () => goSession(msg.sessionId) }, [
        el('div', { class: 'message-header' }, [
          el('span', { class: `badge ${roleClass(role)}`, text: role.toUpperCase() }),
          el('span', { class: 'timestamp', text: formatTimestamp(msg.timestamp) }),
          msg.sessionId ? el('span', { class: 'id-link text-sm', text: `Session: ${truncate(msg.sessionId, 16)}` }) : null
        ]),
        el('div', { class: 'message-content', text: truncate(msg.content || msg.text || msg.body || '', 200) })
      ]);
      list.appendChild(item);
    });
    return list;
  }

  function renderTracesTable(traces) {
    const wrap = el('div', { class: 'table-wrap' });
    const table = el('table');
    table.appendChild(el('thead', {}, [
      el('tr', {}, [
        el('th', { text: 'Trace ID' }),
        el('th', { text: 'Service' }),
        el('th', { text: 'Operation' }),
        el('th', { text: 'Duration' }),
        el('th', { text: 'Status' })
      ])
    ]));
    const tbody = el('tbody');
    traces.forEach(span => {
      const traceId = span.traceId || span.id || '';
      const status = span.status || 'OK';
      const statusClass = status.toLowerCase().includes('error') ? 'text-error' : 'text-success';
      tbody.appendChild(el('tr', { class: 'clickable', onclick: () => navigateTo(`/traces.html?traceId=${encodeURIComponent(traceId)}`) }, [
        el('td', { class: 'mono-sm' }, [el('span', { class: 'id-link', text: truncate(traceId, 16) })]),
        el('td', { text: span.serviceName || span.service || '—' }),
        el('td', { text: truncate(span.operationName || span.name || '—', 40) }),
        el('td', { class: 'mono-sm', text: formatDuration(span.duration || span.durationMs) }),
        el('td', {}, [el('span', { class: statusClass, text: status })])
      ]));
    });
    table.appendChild(tbody);
    wrap.appendChild(table);
    return wrap;
  }

  function buildTimeline(logs, sessions, traces) {
    const timelineSection = $('#correlate-timeline');
    const container = $('#timeline-container');

    // Gather all events with timestamps
    const items = [];
    logs.forEach(e => {
      items.push({
        type: 'log',
        time: e.timestamp,
        level: e.level,
        text: e.message || e.renderedMessage || '',
        icon: '📋',
        id: e.correlationId || e.sessionId
      });
    });
    sessions.forEach(e => {
      items.push({
        type: 'session',
        time: e.timestamp,
        level: e.role || 'system',
        text: truncate(e.content || e.text || e.body || '', 120),
        icon: '💬',
        id: e.sessionId
      });
    });
    traces.forEach(e => {
      items.push({
        type: 'trace',
        time: e.timestamp || e.startTime,
        level: e.status || 'OK',
        text: `${e.serviceName || e.service || '?'} — ${e.operationName || e.name || '?'} (${formatDuration(e.duration || e.durationMs)})`,
        icon: '🔗',
        id: e.traceId
      });
    });

    if (items.length === 0) return;

    // Sort by timestamp
    items.sort((a, b) => new Date(a.time || 0) - new Date(b.time || 0));

    container.innerHTML = '';
    items.forEach(item => {
      const dot = el('div', { class: `timeline-dot ${item.type}` });
      const timeLabel = el('div', { class: 'timeline-time', text: `${item.icon} ${formatTimestamp(item.time)}` });
      const body = el('div', { class: 'timeline-body', text: item.text });

      const entry = el('div', { class: 'timeline-item' }, [dot, timeLabel, body]);
      container.appendChild(entry);
    });

    timelineSection.classList.remove('hidden');
  }
})();
