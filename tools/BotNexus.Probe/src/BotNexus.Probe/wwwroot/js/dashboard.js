/* Dashboard — BotNexus Probe */

(function () {
  'use strict';

  const REFRESH_INTERVAL = 30000;
  let refreshTimer = null;

  document.addEventListener('DOMContentLoaded', () => {
    loadDashboard();
    refreshTimer = setInterval(loadDashboard, REFRESH_INTERVAL);

    const input = $('#correlation-input');
    if (input) input.addEventListener('keydown', (e) => { if (e.key === 'Enter') doQuickCorrelate(); });
  });

  async function loadDashboard() {
    await Promise.allSettled([
      loadGatewayStatus(),
      loadLogStats(),
      loadSessionStats(),
      loadTraceStats(),
      loadRecentLogs(),
      loadActiveSessions()
    ]);
  }

  async function loadGatewayStatus() {
    const cardVal = $('#card-gateway');
    const cardSub = $('#card-gateway-sub');
    try {
      const data = await ProbeApi.getGatewayStatus();
      const connected = data.connected || data.isConnected;
      cardVal.innerHTML = `<span class="status-dot ${connected ? 'connected' : 'disconnected'}"></span> ${connected ? 'Connected' : 'Disconnected'}`;
      cardSub.textContent = data.url || data.gatewayUrl || '—';
    } catch {
      cardVal.innerHTML = '<span class="status-dot unknown"></span> Unknown';
      cardSub.textContent = 'Could not reach gateway API';
    }
  }

  async function loadLogStats() {
    try {
      const files = await ProbeApi.getLogFiles();
      const arr = Array.isArray(files) ? files : (files.files || []);
      $('#card-logs').textContent = arr.length;
      const totalSize = arr.reduce((s, f) => s + (f.size || f.sizeBytes || 0), 0);
      $('#card-logs-sub').textContent = totalSize > 0 ? formatFileSize(totalSize) : `${arr.length} file(s)`;
    } catch {
      $('#card-logs').textContent = '—';
      $('#card-logs-sub').textContent = 'Unable to load';
    }
  }

  async function loadSessionStats() {
    try {
      const sessions = await ProbeApi.getSessions();
      const arr = Array.isArray(sessions) ? sessions : (sessions.sessions || []);
      $('#card-sessions').textContent = arr.length;
      if (arr.length > 0) {
        const latest = arr[0];
        $('#card-sessions-sub').textContent = `Latest: ${truncate(latest.sessionId || latest.id || '', 16)}`;
      } else {
        $('#card-sessions-sub').textContent = 'No sessions found';
      }
    } catch {
      $('#card-sessions').textContent = '—';
      $('#card-sessions-sub').textContent = 'Unable to load';
    }
  }

  async function loadTraceStats() {
    try {
      const traces = await ProbeApi.getTraces();
      const arr = Array.isArray(traces) ? traces : (traces.traces || []);
      $('#card-traces').textContent = arr.length;
      $('#card-traces-sub').textContent = arr.length > 0 ? 'OTLP active' : 'No traces captured';
    } catch {
      $('#card-traces').textContent = '—';
      $('#card-traces-sub').textContent = 'OTLP may be disabled';
    }
  }

  async function loadRecentLogs() {
    const container = $('#recent-logs');
    try {
      const data = await ProbeApi.getLogs({ take: 10 });
      const entries = Array.isArray(data) ? data : (data.entries || data.logs || []);
      if (entries.length === 0) {
        setEmpty(container, '📄', 'No log entries found');
        return;
      }
      const table = el('div', { class: 'table-wrap' }, [
        el('table', {}, [
          el('thead', {}, [
            el('tr', {}, [
              el('th', { text: 'Time', style: { width: '150px' } }),
              el('th', { text: 'Level', style: { width: '60px' } }),
              el('th', { text: 'Message' })
            ])
          ]),
          el('tbody', {}, entries.map(entry =>
            el('tr', { class: 'clickable', onclick: () => goCorrelate(entry.correlationId || entry.sessionId) }, [
              el('td', { class: 'mono-sm', text: formatTimestampShort(entry.timestamp), title: formatTimestampUTC(entry.timestamp) }),
              el('td', {}, [el('span', { class: `badge ${levelClass(entry.level)}`, text: (entry.level || 'info').substring(0, 4).toUpperCase() })]),
              el('td', { text: truncate(entry.message || entry.renderedMessage || '', 100) })
            ])
          ))
        ])
      ]);
      container.innerHTML = '';
      container.appendChild(table);
    } catch (err) {
      setError(container, `Failed to load logs: ${err.message}`);
    }
  }

  async function loadActiveSessions() {
    const container = $('#active-sessions');
    try {
      const data = await ProbeApi.getSessions();
      const sessions = (Array.isArray(data) ? data : (data.sessions || [])).slice(0, 5);
      if (sessions.length === 0) {
        setEmpty(container, '💬', 'No sessions available');
        return;
      }
      const table = el('div', { class: 'table-wrap' }, [
        el('table', {}, [
          el('thead', {}, [
            el('tr', {}, [
              el('th', { text: 'Session ID' }),
              el('th', { text: 'Agent' }),
              el('th', { text: 'Channel' }),
              el('th', { text: 'Messages' }),
              el('th', { text: 'Created' })
            ])
          ]),
          el('tbody', {}, sessions.map(s =>
            el('tr', { class: 'clickable', onclick: () => goSession(s.sessionId || s.id) }, [
              el('td', { class: 'mono-sm' }, [
                el('span', { class: 'id-link', text: truncate(s.sessionId || s.id || '', 20) })
              ]),
              el('td', { text: s.agentId || s.agent || '—' }),
              el('td', { text: s.channelId || s.channel || '—' }),
              el('td', { class: 'mono-sm', text: String(s.messageCount || s.messages || '—') }),
              el('td', { class: 'mono-sm', text: formatTimestamp(s.createdAt || s.timestamp), title: formatTimestampUTC(s.createdAt || s.timestamp) })
            ])
          ))
        ])
      ]);
      container.innerHTML = '';
      container.appendChild(table);
    } catch (err) {
      setError(container, `Failed to load sessions: ${err.message}`);
    }
  }

  function formatFileSize(bytes) {
    if (bytes < 1024) return bytes + ' B';
    if (bytes < 1048576) return (bytes / 1024).toFixed(1) + ' KB';
    return (bytes / 1048576).toFixed(1) + ' MB';
  }

  // Expose for inline handler
  window.doQuickCorrelate = async function () {
    const input = $('#correlation-input');
    const id = (input.value || '').trim();
    if (!id) return;
    goCorrelate(id);
  };
})();
