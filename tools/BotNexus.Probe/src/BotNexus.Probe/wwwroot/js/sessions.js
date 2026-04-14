/* Session Viewer — BotNexus Probe */

(function () {
  'use strict';

  const PAGE_SIZE = 50;
  let allSessions = [];
  let currentSessionId = null;
  let currentSkip = 0;

  document.addEventListener('DOMContentLoaded', () => {
    loadSessions();
    const search = $('#session-search');
    if (search) search.addEventListener('input', debounce(filterList, 250));
  });

  async function loadSessions() {
    const list = $('#session-list');
    setLoading(list);
    try {
      const data = await ProbeApi.getSessions();
      allSessions = Array.isArray(data) ? data : (data.sessions || []);
      renderList(allSessions);

      // Auto-select from URL param
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
      const id = s.sessionId || s.id || '';
      const item = el('div', {
        class: `list-item${id === currentSessionId ? ' active' : ''}`,
        onclick: () => selectSession(id)
      }, [
        el('div', { class: 'item-title', text: truncate(id, 28) }),
        el('div', { class: 'item-meta' }, [
          el('span', { text: `🤖 ${s.agentId || s.agent || '—'}` }),
          el('span', { text: `💬 ${s.messageCount || s.messages || '?'}` }),
          el('span', { text: formatTimestamp(s.createdAt || s.timestamp) })
        ])
      ]);
      list.appendChild(item);
    });
  }

  function filterList() {
    const q = ($('#session-search').value || '').toLowerCase();
    if (!q) { renderList(allSessions); return; }
    const filtered = allSessions.filter(s => {
      const id = (s.sessionId || s.id || '').toLowerCase();
      const agent = (s.agentId || s.agent || '').toLowerCase();
      return id.includes(q) || agent.includes(q);
    });
    renderList(filtered);
  }

  async function selectSession(id) {
    currentSessionId = id;
    currentSkip = 0;
    setParams({ id });

    // Update list selection
    $$('.panel-list .list-item').forEach(item => {
      const title = item.querySelector('.item-title')?.textContent || '';
      item.classList.toggle('active', id.startsWith(title.replace('…', '')) || title.startsWith(id.substring(0, 10)));
    });

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
    const messages = data.messages || session.messages || [];
    const total = data.totalMessages || data.total || messages.length;

    // Metadata header
    const meta = el('div', { class: 'metadata' });
    const fields = [
      { label: 'Session ID', value: session.sessionId || session.id || currentSessionId },
      { label: 'Agent', value: session.agentId || session.agent || '—' },
      { label: 'Channel', value: session.channelId || session.channel || '—' },
      { label: 'Type', value: session.type || session.conversationType || '—' },
      { label: 'Created', value: formatTimestamp(session.createdAt || session.timestamp) },
      { label: 'Messages', value: String(total) }
    ];
    fields.forEach(f => {
      meta.appendChild(el('div', { class: 'meta-item' }, [
        el('label', { text: f.label }),
        el('span', { text: f.value })
      ]));
    });
    detail.appendChild(meta);

    // Action buttons
    const actions = el('div', { class: 'flex gap-8 mb-16' });
    const copyBtn = el('button', { class: 'btn-secondary btn-small', text: '📋 Copy Session ID' });
    copyBtn.onclick = () => copyToClipboard(session.sessionId || session.id || currentSessionId);
    actions.appendChild(copyBtn);

    const corrBtn = el('button', { class: 'btn-secondary btn-small', text: '🔎 Correlate' });
    corrBtn.onclick = () => goCorrelate(session.sessionId || session.id || currentSessionId);
    actions.appendChild(corrBtn);
    detail.appendChild(actions);

    // Messages
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

    // Pagination
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
    const role = msg.role || msg.type || 'system';
    const item = el('div', { class: 'message-item' });

    // Header
    const header = el('div', { class: 'message-header' }, [
      el('span', { class: `badge ${roleClass(role)}`, text: role.toUpperCase() }),
      el('span', { class: 'timestamp', text: formatTimestamp(msg.timestamp), title: formatTimestampUTC(msg.timestamp) })
    ]);
    if (msg.agentId || msg.agent) {
      header.appendChild(el('span', { class: 'text-muted text-sm', text: `🤖 ${msg.agentId || msg.agent}` }));
    }
    item.appendChild(header);

    // Content
    const content = msg.content || msg.text || msg.body || '';
    if (typeof content === 'string') {
      const contentDiv = el('div', { class: 'message-content' });
      contentDiv.textContent = content;
      item.appendChild(contentDiv);
    } else if (typeof content === 'object') {
      const pre = el('pre', { text: JSON.stringify(content, null, 2) });
      item.appendChild(pre);
    }

    // Tool calls
    const toolCalls = msg.toolCalls || msg.tool_calls || [];
    if (toolCalls.length > 0) {
      toolCalls.forEach(tc => {
        const tcDiv = el('div', { class: 'tool-call' });
        const tcHeader = el('div', { class: 'tool-call-header', text: `🔧 ${tc.name || tc.function?.name || 'Tool Call'}` });
        tcHeader.onclick = () => tcDiv.classList.toggle('open');
        tcDiv.appendChild(tcHeader);

        const tcBody = el('div', { class: 'tool-call-body' });
        const args = tc.arguments || tc.function?.arguments || tc.input || {};
        tcBody.appendChild(el('pre', { text: typeof args === 'string' ? args : JSON.stringify(args, null, 2) }));
        if (tc.result || tc.output) {
          tcBody.appendChild(el('div', { class: 'text-sm text-muted mt-8', text: 'Result:' }));
          tcBody.appendChild(el('pre', { text: typeof (tc.result || tc.output) === 'string' ? (tc.result || tc.output) : JSON.stringify(tc.result || tc.output, null, 2) }));
        }
        tcDiv.appendChild(tcBody);
        item.appendChild(tcDiv);
      });
    }

    return item;
  }
})();
