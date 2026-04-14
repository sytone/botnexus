/* Live Activity Stream — BotNexus Probe */

(function () {
  'use strict';

  let eventSource = null;
  let connected = false;
  let events = [];
  let totalEvents = 0;
  let eventRateWindow = [];
  let rateTimer = null;
  let activeTypes = new Set();
  let knownTypes = new Set();
  let paused = false;

  document.addEventListener('DOMContentLoaded', () => {
    rateTimer = setInterval(updateRate, 1000);
  });

  window.toggleConnection = function () {
    if (connected) {
      disconnect();
    } else {
      connect();
    }
  };

  function connect() {
    const url = $('#gateway-url').value || '/api/gateway/activity';
    try {
      eventSource = new EventSource(url);
      eventSource.onopen = () => {
        connected = true;
        updateConnectionUI(true);
        // Clear placeholder
        const stream = $('#activity-stream');
        if (stream.querySelector('.empty-state')) stream.innerHTML = '';
      };
      eventSource.onmessage = (e) => {
        let data;
        try { data = JSON.parse(e.data); } catch { data = { message: e.data }; }
        onEvent(data);
      };
      eventSource.onerror = () => {
        if (connected) {
          disconnect();
          showToast('Gateway connection lost');
        }
      };
    } catch (err) {
      showToast(`Connection failed: ${err.message}`);
    }
  }

  function disconnect() {
    if (eventSource) { eventSource.close(); eventSource = null; }
    connected = false;
    updateConnectionUI(false);
  }

  function updateConnectionUI(isConnected) {
    const banner = $('#connection-banner');
    const dot = banner.querySelector('.status-dot');
    const text = $('#connection-text');
    const btn = $('#btn-connect');

    if (isConnected) {
      banner.className = 'connection-banner connected';
      dot.className = 'status-dot connected';
      text.textContent = 'Connected to gateway';
      btn.textContent = 'Disconnect';
      btn.style.background = 'var(--error)';
    } else {
      banner.className = 'connection-banner disconnected';
      dot.className = 'status-dot disconnected';
      text.textContent = 'Not connected to gateway';
      btn.textContent = 'Connect';
      btn.style.background = '';
    }
  }

  function onEvent(data) {
    totalEvents++;
    eventRateWindow.push(Date.now());

    const type = data.type || data.activityType || data.eventType || 'Unknown';
    if (!knownTypes.has(type)) {
      knownTypes.add(type);
      activeTypes.add(type);
      renderTypeFilters();
    }

    events.push(data);
    // Cap at 500 events in memory
    if (events.length > 500) events.shift();

    $('#stat-total').textContent = totalEvents;

    if (!activeTypes.has(type)) return;

    renderEvent(data);
  }

  function renderEvent(data) {
    const stream = $('#activity-stream');
    const type = data.type || data.activityType || data.eventType || 'Unknown';
    const ts = data.timestamp || new Date().toISOString();
    const agent = data.agentId || data.agent || '—';
    const session = data.sessionId || data.session || '';
    const message = data.message || data.text || truncate(JSON.stringify(data), 120);

    const badgeClass = getBadgeClass(type);

    const entry = el('div', { class: 'activity-entry' }, [
      el('span', { class: 'ts', text: formatTimestampShort(ts), title: formatTimestampUTC(ts) }),
      el('span', {}, [el('span', { class: `badge ${badgeClass}`, text: truncate(type, 18) })]),
      el('div', {}, [
        el('span', { text: message }),
        session ? el('span', { class: 'id-link text-sm', text: ` [${truncate(session, 12)}]`, style: { marginLeft: '8px' }, onclick: (e) => { e.stopPropagation(); goCorrelate(session); } }) : null
      ]),
      el('div', { class: 'payload' }, [
        el('pre', { text: JSON.stringify(data, null, 2) })
      ])
    ]);
    entry.onclick = () => entry.classList.toggle('open');

    stream.appendChild(entry);

    // Auto-scroll
    if ($('#auto-scroll').checked && !paused) {
      stream.scrollTop = stream.scrollHeight;
    }
  }

  function getBadgeClass(type) {
    const t = (type || '').toLowerCase();
    if (t.includes('received') || t.includes('request')) return 'badge-received';
    if (t.includes('sent') || t.includes('response')) return 'badge-sent';
    if (t.includes('error')) return 'badge-error';
    return 'badge-activity';
  }

  function renderTypeFilters() {
    const container = $('#type-filters');
    container.innerHTML = '';
    [...knownTypes].sort().forEach(type => {
      const label = el('label', { class: 'text-sm', style: { cursor: 'pointer', display: 'flex', alignItems: 'center', gap: '4px' } }, [
        (() => {
          const cb = document.createElement('input');
          cb.type = 'checkbox';
          cb.checked = activeTypes.has(type);
          cb.onchange = () => {
            if (cb.checked) activeTypes.add(type);
            else activeTypes.delete(type);
          };
          return cb;
        })(),
        document.createTextNode(type)
      ]);
      container.appendChild(label);
    });
  }

  window.toggleAllTypes = function () {
    const all = $('#show-all-types').checked;
    if (all) {
      knownTypes.forEach(t => activeTypes.add(t));
    } else {
      activeTypes.clear();
    }
    renderTypeFilters();
  };

  window.clearStream = function () {
    events = [];
    totalEvents = 0;
    const stream = $('#activity-stream');
    stream.innerHTML = '';
    $('#stat-total').textContent = '0';
    $('#stat-rate').textContent = '0';
  };

  function updateRate() {
    const now = Date.now();
    eventRateWindow = eventRateWindow.filter(t => now - t < 5000);
    const rate = (eventRateWindow.length / 5).toFixed(1);
    $('#stat-rate').textContent = rate;
  }
})();
