/* Session Viewer — BotNexus Probe */

(function () {
  'use strict';

  const PAGE_SIZE = 50;
  let allSessions = [];
  let currentSessionId = null;
  let currentSkip = 0;
  let currentSource = 'jsonl';

  document.addEventListener('DOMContentLoaded', () => {
    loadSessions();
    const search = $('#session-search');
    if (search) search.addEventListener('input', debounce(filterList, 250));
    ['agent-filter', 'channel-filter', 'type-filter', 'status-filter'].forEach(id => {
      const element = document.getElementById(id);
      if (element) element.addEventListener('change', () => loadSessions());
    });
  });

  async function loadSessions() {
    const list = $('#session-list');
    setLoading(list);
    try {
      const data = await ProbeApi.getSessions({
        agent: valueOf('#agent-filter'),
        channel: valueOf('#channel-filter'),
        type: valueOf('#type-filter'),
        status: valueOf('#status-filter')
      });

      currentSource = data?.source || 'jsonl';
      allSessions = Array.isArray(data) ? data : (data.items || data.sessions || []);
      populateFilters(allSessions);
      renderList(allSessions);

      const id = getParam('id');
      if (id) selectSession(id);
    } catch (err) {
      setError(list, `Failed to load sessions: ${err.message}`);
    }
  }

  function renderList(sessions) {
    const list = $('#session-list');
    if (sessions.length === 0) {
      setEmpty(list, '💬', 'No sessions found');
      return;
    }

    list.innerHTML = '';
    sessions.forEach(s => {
      const id = s.id || s.sessionId || '';
      const item = el('div', {
        class: `list-item${id === currentSessionId ? ' active' : ''}`,
        onclick: () => selectSession(id)
      }, [
        el('div', { class: 'item-title', text: truncate(id, 32) }),
        el('div', { class: 'item-meta' }, [
          el('span', { text: `🤖 ${s.agentId || '—'}` }),
          el('span', { text: `📡 ${s.channelType || '—'}` }),
          el('span', { text: `🧭 ${s.sessionType || '—'}` }),
          el('span', { text: `🟢 ${s.status || '—'}` }),
          el('span', { text: `💬 ${s.messageCount ?? '?'}` })
        ])
      ]);
      list.appendChild(item);
    });
  }

  function filterList() {
    const q = ($('#session-search').value || '').toLowerCase();
    if (!q) { renderList(allSessions); return; }
    const filtered = allSessions.filter(s => {
      const id = (s.id || s.sessionId || '').toLowerCase();
      const agent = (s.agentId || '').toLowerCase();
      const channel = (s.channelType || '').toLowerCase();
      const type = (s.sessionType || '').toLowerCase();
      const status = (s.status || '').toLowerCase();
      return id.includes(q) || agent.includes(q) || channel.includes(q) || type.includes(q) || status.includes(q);
    });
    renderList(filtered);
  }

  async function selectSession(id) {
    currentSessionId = id;
    currentSkip = 0;
    setParams({ id });
    await loadSessionDetail();
  }

  async function loadSessionDetail() {
    const detail = $('#session-detail');
    setLoading(detail);
    try {
      const data = await ProbeApi.getSession(currentSessionId, currentSkip, PAGE_SIZE);
      renderSessionDetail(data);
    } catch (err) {
      setError(detail, `Failed to load session: ${err.message}`);
    }
  }

  function renderSessionDetail(data) {
    const detail = $('#session-detail');
    detail.innerHTML = '';

    const session = data.session || data;
    const messageBlock = data.messages || {};
    const messages = messageBlock.items || data.items || [];
    const total = messageBlock.count || data.count || messages.length;
    const source = data.source || currentSource;

    const meta = el('div', { class: 'metadata' });
    const fields = [
      { label: 'Source', value: source },
      { label: 'Session ID', value: session.sessionId || session.id || currentSessionId },
      { label: 'Agent', value: session.agentId || '—' },
      { label: 'Channel', value: session.channelType || '—' },
      { label: 'Type', value: session.sessionType || '—' },
      { label: 'Status', value: session.status || session.statusText || '—' },
      { label: 'Caller', value: session.callerId || '—' },
      { label: 'Created', value: formatTimestamp(session.createdAt || session.timestamp) },
      { label: 'Updated', value: formatTimestamp(session.updatedAt) },
      { label: 'Messages', value: String(total) }
    ];

    fields.forEach(f => {
      meta.appendChild(el('div', { class: 'meta-item' }, [
        el('label', { text: f.label }),
        el('span', { text: f.value })
      ]));
    });
    detail.appendChild(meta);

    if (session.participants) {
      detail.appendChild(el('pre', { text: JSON.stringify(session.participants, null, 2) }));
    }

    const actions = el('div', { class: 'flex gap-8 mb-16' });
    const copyBtn = el('button', { class: 'btn-secondary btn-small', text: '📋 Copy Session ID' });
    copyBtn.onclick = () => copyToClipboard(session.sessionId || session.id || currentSessionId);
    actions.appendChild(copyBtn);
    detail.appendChild(actions);

    if (messages.length === 0) {
      detail.appendChild(el('div', { class: 'empty-state' }, [
        el('div', { class: 'icon', text: '📭' }),
        el('p', { text: 'No messages in this session' })
      ]));
      return;
    }

    const msgList = el('div', { class: 'message-list' });
    messages.forEach(msg => {
      msgList.appendChild(renderMessage(msg));
    });
    detail.appendChild(msgList);

    if (total > PAGE_SIZE) {
      const pag = el('div', { class: 'pagination mt-16' });
      const page = Math.floor(currentSkip / PAGE_SIZE) + 1;
      const maxPage = Math.ceil(total / PAGE_SIZE);
      pag.innerHTML = `<span class="text-muted">Page ${page} of ${maxPage}</span>`;

      const controls = el('div', { class: 'page-controls' });
      const prevBtn = el('button', { class: 'btn-secondary btn-small', text: '← Prev' });
      prevBtn.disabled = currentSkip === 0;
      prevBtn.onclick = () => { currentSkip = Math.max(0, currentSkip - PAGE_SIZE); loadSessionDetail(); };
      controls.appendChild(prevBtn);

      const nextBtn = el('button', { class: 'btn-secondary btn-small', text: 'Next →' });
      nextBtn.disabled = currentSkip + PAGE_SIZE >= total;
      nextBtn.onclick = () => { currentSkip += PAGE_SIZE; loadSessionDetail(); };
      controls.appendChild(nextBtn);

      pag.appendChild(controls);
      detail.appendChild(pag);
    }
  }

  function renderMessage(msg) {
    const role = msg.role || 'system';
    const item = el('div', { class: 'message-item' });
    const header = el('div', { class: 'message-header' }, [
      el('span', { class: `badge ${roleClass(role)}`, text: role.toUpperCase() }),
      el('span', { class: 'timestamp', text: formatTimestamp(msg.timestamp), title: formatTimestampUTC(msg.timestamp) })
    ]);

    if (msg.toolName) {
      header.appendChild(el('span', { class: 'badge badge-tool', text: `TOOL:${msg.toolName}` }));
    }
    if (msg.isCompactionSummary) {
      header.appendChild(el('span', { class: 'badge badge-system', text: 'COMPACTION' }));
    }
    item.appendChild(header);

    const content = msg.content || '';
    const contentDiv = el('div', { class: 'message-content' });
    contentDiv.textContent = content;
    item.appendChild(contentDiv);
    return item;
  }

  function populateFilters(sessions) {
    fillSelect('#agent-filter', sessions.map(x => x.agentId).filter(Boolean), 'All agents');
    fillSelect('#channel-filter', sessions.map(x => x.channelType).filter(Boolean), 'All channels');
    fillSelect('#type-filter', sessions.map(x => x.sessionType).filter(Boolean), 'All types');
    fillSelect('#status-filter', sessions.map(x => x.status).filter(Boolean), 'All statuses');
  }

  function fillSelect(selector, values, label) {
    const select = document.querySelector(selector);
    if (!select) return;
    const current = select.value;
    const distinct = [...new Set(values)].sort();
    select.innerHTML = `<option value="">${label}</option>`;
    distinct.forEach(v => select.appendChild(el('option', { value: v, text: v })));
    if (current && distinct.includes(current)) select.value = current;
  }

  function valueOf(selector) {
    return document.querySelector(selector)?.value || '';
  }
})();
