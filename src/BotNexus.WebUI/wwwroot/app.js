// BotNexus WebUI — connects to the Gateway via WebSocket and REST APIs
(function () {
    'use strict';

    // --- Configuration ---
    const API_BASE = '/api';
    const WS_PATH = '/ws';
    const RECONNECT_BASE_MS = 1000;
    const RECONNECT_MAX_MS = 30000;
    const PING_INTERVAL_MS = 30000;
    const MAX_ACTIVITY_ITEMS = 100;

    // --- State ---
    /** @type {WebSocket|null} */
    let ws = null;
    let currentSessionId = null;
    let currentAgentId = null;
    let connectionId = null;
    let reconnectAttempts = 0;
    let reconnectTimer = null;
    let pingTimer = null;
    let isStreaming = false;
    let activeMessageId = null;
    let showTools = false;
    let showThinking = false;
    let isActivitySubscribed = false;
    /** @type {Object<string, {toolName:string, args:string, result:string, status:string}>} */
    let activeToolCalls = {};
    let activeToolCount = 0;
    /** @type {Array} */
    let agentsCache = [];
    /** @type {Array} */
    let providersCache = [];
    /** @type {Array} */
    let modelsCache = [];
    /** @type {string} */
    let thinkingBuffer = '';
    /** @type {function|null} */
    let confirmCallback = null;

    // --- DOM refs ---
    const $ = (sel) => document.querySelector(sel);
    const $$ = (sel) => document.querySelectorAll(sel);

    const elSessionsList = $('#sessions-list');
    const elAgentsList = $('#agents-list');
    const elConnectionStatus = $('#connection-status');
    const elStatusText = elConnectionStatus.querySelector('.status-text');
    const elWelcome = $('#welcome-screen');
    const elChatView = $('#chat-view');
    const elChatTitle = $('#chat-title');
    const elChatMeta = $('#chat-meta');
    const elChatMessages = $('#chat-messages');
    const elChatInput = $('#chat-input');
    const elBtnSend = $('#btn-send');
    const elBtnAbort = $('#btn-abort');
    const elAgentSelect = $('#agent-select');
    const elToggleTools = $('#toggle-tools');
    const elToggleThinking = $('#toggle-thinking');
    const elToggleActivity = $('#toggle-activity');
    const elActivityFeed = $('#activity-feed');
    const elToolModal = $('#tool-modal');
    const elModalClose = elToolModal.querySelector('.modal-close');
    const elModalOverlay = elToolModal.querySelector('.modal-overlay');
    const elAgentFormModal = $('#agent-form-modal');
    const elAgentForm = $('#agent-form');
    const elConfirmDialog = $('#confirm-dialog');

    // =========================================================================
    // Markdown rendering
    // =========================================================================

    function initMarkdown() {
        if (typeof marked !== 'undefined') {
            marked.setOptions({ breaks: true, gfm: true, headerIds: false, mangle: false });
        }
    }

    function renderMarkdown(text) {
        if (!text) return '';
        try {
            const html = typeof marked !== 'undefined'
                ? (typeof marked.parse === 'function' ? marked.parse(text) : marked(text))
                : escapeHtml(text);
            return typeof DOMPurify !== 'undefined' ? DOMPurify.sanitize(html) : html;
        } catch (e) {
            console.error('Markdown render error:', e);
            return escapeHtml(text);
        }
    }

    // =========================================================================
    // Utilities
    // =========================================================================

    function escapeHtml(str) {
        if (!str) return '';
        const div = document.createElement('div');
        div.textContent = str;
        return div.innerHTML;
    }

    function formatTime(iso) {
        if (!iso) return '';
        try {
            return new Date(iso).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
        } catch { return ''; }
    }

    function relativeTime(iso) {
        if (!iso) return '';
        try {
            const diff = Date.now() - new Date(iso).getTime();
            const mins = Math.floor(diff / 60000);
            if (mins < 1) return 'just now';
            if (mins < 60) return `${mins}m ago`;
            const hrs = Math.floor(mins / 60);
            if (hrs < 24) return `${hrs}h ago`;
            return `${Math.floor(hrs / 24)}d ago`;
        } catch { return ''; }
    }

    function scrollToBottom() {
        requestAnimationFrame(() => { elChatMessages.scrollTop = elChatMessages.scrollHeight; });
    }

    function autoResize(el) {
        el.style.height = 'auto';
        el.style.height = Math.min(el.scrollHeight, 200) + 'px';
    }

    // =========================================================================
    // Connection status
    // =========================================================================

    function setStatus(state) {
        elConnectionStatus.className = `status ${state}`;
        const labels = { connected: 'Connected', disconnected: 'Disconnected', connecting: 'Connecting...', reconnecting: 'Reconnecting...' };
        elStatusText.textContent = labels[state] || state;
    }

    // =========================================================================
    // Confirm dialog
    // =========================================================================

    function showConfirm(message, title, onConfirm) {
        $('#confirm-title').textContent = title || 'Confirm';
        $('#confirm-message').textContent = message;
        confirmCallback = onConfirm;
        elConfirmDialog.classList.remove('hidden');
    }

    function closeConfirm() {
        elConfirmDialog.classList.add('hidden');
        confirmCallback = null;
    }

    // =========================================================================
    // WebSocket connection
    // =========================================================================

    function connectWebSocket() {
        if (ws && (ws.readyState === WebSocket.OPEN || ws.readyState === WebSocket.CONNECTING)) return;
        if (!currentAgentId) return;

        setStatus(reconnectAttempts > 0 ? 'reconnecting' : 'connecting');
        const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
        let url = `${proto}//${location.host}${WS_PATH}?agent=${encodeURIComponent(currentAgentId)}`;
        if (currentSessionId) url += `&session=${encodeURIComponent(currentSessionId)}`;

        ws = new WebSocket(url);

        ws.onopen = () => {
            setStatus('connected');
            reconnectAttempts = 0;
            startPing();
            if (isActivitySubscribed) sendWs({ type: 'subscribe' });
        };

        ws.onclose = () => {
            setStatus('disconnected');
            connectionId = null;
            stopPing();
            scheduleReconnect();
        };

        ws.onerror = () => { setStatus('disconnected'); };

        ws.onmessage = (event) => {
            try { handleWsMessage(JSON.parse(event.data)); }
            catch (e) { console.error('Failed to parse WS message:', e); }
        };
    }

    function disconnectWebSocket() {
        stopPing();
        if (reconnectTimer) { clearTimeout(reconnectTimer); reconnectTimer = null; }
        reconnectAttempts = 0;
        if (ws) { ws.onclose = null; ws.close(); ws = null; }
        setStatus('disconnected');
        connectionId = null;
    }

    function scheduleReconnect() {
        if (reconnectTimer) return;
        const delay = Math.min(RECONNECT_BASE_MS * Math.pow(2, reconnectAttempts), RECONNECT_MAX_MS);
        reconnectAttempts++;
        setStatus('reconnecting');
        reconnectTimer = setTimeout(() => { reconnectTimer = null; connectWebSocket(); }, delay);
    }

    function startPing() {
        stopPing();
        pingTimer = setInterval(() => { sendWs({ type: 'ping' }); }, PING_INTERVAL_MS);
    }

    function stopPing() {
        if (pingTimer) { clearInterval(pingTimer); pingTimer = null; }
    }

    function sendWs(obj) {
        if (ws && ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify(obj));
    }

    // =========================================================================
    // WebSocket message handler
    // =========================================================================

    function handleWsMessage(msg) {
        switch (msg.type) {
            case 'connected':
                connectionId = msg.connectionId;
                if (msg.sessionId) currentSessionId = msg.sessionId;
                break;
            case 'message_start':
                activeMessageId = msg.messageId;
                isStreaming = true;
                activeToolCount = 0;
                thinkingBuffer = '';
                elBtnAbort.classList.remove('hidden');
                showStreamingIndicator();
                break;
            case 'content_delta':
                removeStreamingIndicator();
                appendDelta(msg.delta);
                break;
            case 'thinking_delta':
                handleThinkingDelta(msg);
                break;
            case 'tool_start':
                handleToolStart(msg);
                break;
            case 'tool_end':
                handleToolEnd(msg);
                break;
            case 'message_end':
                finalizeMessage(msg);
                break;
            case 'error':
                handleError(msg);
                break;
            case 'activity':
                handleActivityEvent(msg);
                break;
            case 'pong':
                break;
        }
    }

    // =========================================================================
    // Thinking display
    // =========================================================================

    function handleThinkingDelta(msg) {
        if (!msg.delta) return;
        thinkingBuffer += msg.delta;

        let thinkingEl = elChatMessages.querySelector('.thinking-block');
        if (!thinkingEl) {
            removeStreamingIndicator();
            thinkingEl = document.createElement('div');
            thinkingEl.className = `thinking-block${showThinking ? '' : ' collapsed'}`;
            thinkingEl.innerHTML = `
                <div class="thinking-toggle" role="button" tabindex="0" aria-expanded="${showThinking}" aria-label="Toggle thinking details">
                    <span class="thinking-icon" aria-hidden="true">💭</span>
                    <span class="thinking-label">Thinking...</span>
                    <span class="thinking-chevron" aria-hidden="true">${showThinking ? '▾' : '▸'}</span>
                </div>
                <div class="thinking-content"><pre class="thinking-pre"></pre></div>
            `;
            thinkingEl.querySelector('.thinking-toggle').addEventListener('click', () => {
                thinkingEl.classList.toggle('collapsed');
                const expanded = !thinkingEl.classList.contains('collapsed');
                thinkingEl.querySelector('.thinking-toggle').setAttribute('aria-expanded', expanded);
                thinkingEl.querySelector('.thinking-chevron').textContent = expanded ? '▾' : '▸';
            });
            elChatMessages.appendChild(thinkingEl);
        }

        thinkingEl.querySelector('.thinking-pre').textContent = thinkingBuffer;
        scrollToBottom();
    }

    function finalizeThinkingBlock() {
        const thinkingEl = elChatMessages.querySelector('.thinking-block');
        if (thinkingEl) {
            thinkingEl.querySelector('.thinking-label').textContent = 'Thought process';
            thinkingEl.classList.add('complete');
        }
    }

    // =========================================================================
    // Tool call handling
    // =========================================================================

    function handleToolStart(msg) {
        const callId = msg.toolCallId || `tc-${Date.now()}`;
        activeToolCount++;
        activeToolCalls[callId] = {
            toolName: msg.toolName || 'unknown',
            args: msg.toolArgs || '',
            result: '',
            status: 'running'
        };
        appendToolCall(callId, msg.toolName, 'running');
    }

    function handleToolEnd(msg) {
        const callId = msg.toolCallId || 'unknown';
        if (activeToolCalls[callId]) {
            activeToolCalls[callId].result = msg.toolResult || '';
            activeToolCalls[callId].status = 'complete';
        }
        updateToolCallStatus(callId, 'complete');
    }

    function appendToolCall(callId, toolName, status) {
        const div = document.createElement('div');
        div.className = `message tool-call tool-${status}${showTools ? '' : ' hidden'}`;
        div.dataset.callId = callId;
        div.dataset.toolName = toolName;
        div.setAttribute('role', 'status');
        div.innerHTML = `
            <span class="tool-icon" aria-hidden="true">🔧</span>
            <span class="tool-name">${escapeHtml(toolName)}</span>
            <span class="tool-status-badge ${status}">${status === 'running' ? '⏳ Running' : '✓ Done'}</span>
        `;
        elChatMessages.appendChild(div);
        scrollToBottom();
    }

    function updateToolCallStatus(callId, status) {
        const el = elChatMessages.querySelector(`.tool-call[data-call-id="${callId}"]`);
        if (!el) return;
        el.classList.remove('tool-running');
        el.classList.add(`tool-${status}`);
        const badge = el.querySelector('.tool-status-badge');
        if (badge) {
            badge.className = `tool-status-badge ${status}`;
            badge.textContent = status === 'error' ? '✗ Error' : '✓ Done';
        }
        el.style.cursor = 'pointer';
        el.addEventListener('click', () => {
            const data = activeToolCalls[callId];
            if (data) openToolModal(data);
        });
    }

    // =========================================================================
    // Streaming / message indicators
    // =========================================================================

    function showStreamingIndicator() {
        removeStreamingIndicator();
        const div = document.createElement('div');
        div.className = 'message thinking streaming-indicator';
        div.setAttribute('aria-label', 'Agent is processing');
        div.innerHTML = '<span class="thinking-dots" aria-hidden="true">●●●</span> Agent is processing...';
        elChatMessages.appendChild(div);
        scrollToBottom();
    }

    function removeStreamingIndicator() {
        elChatMessages.querySelectorAll('.streaming-indicator').forEach(el => el.remove());
    }

    // =========================================================================
    // Message finalization
    // =========================================================================

    function finalizeMessage(msg) {
        isStreaming = false;
        activeMessageId = null;
        elBtnAbort.classList.add('hidden');
        removeStreamingIndicator();
        finalizeThinkingBlock();

        const streaming = elChatMessages.querySelector('.message.assistant.streaming');
        if (streaming) {
            streaming.classList.remove('streaming');
            const deltaEl = streaming.querySelector('.delta-content');
            if (deltaEl) {
                deltaEl.innerHTML = renderMarkdown(deltaEl.textContent);
            }
            const timeEl = streaming.querySelector('.msg-time');
            if (timeEl) timeEl.textContent = formatTime(new Date().toISOString());

            // Message footer with tool count + usage
            const footer = document.createElement('div');
            footer.className = 'msg-footer';
            const parts = [];
            if (activeToolCount > 0) parts.push(`🔧 ${activeToolCount} tool call${activeToolCount > 1 ? 's' : ''}`);
            if (msg.usage) {
                const u = formatUsage(msg.usage);
                if (u) parts.push(u);
            }
            if (parts.length > 0) {
                footer.textContent = parts.join(' · ');
                streaming.appendChild(footer);
            }
        }

        activeToolCalls = {};
        activeToolCount = 0;
        thinkingBuffer = '';
        loadSessions();
        scrollToBottom();
    }

    function formatUsage(usage) {
        if (!usage) return '';
        const parts = [];
        if (usage.inputTokens) parts.push(`↑${usage.inputTokens}`);
        if (usage.outputTokens) parts.push(`↓${usage.outputTokens}`);
        if (usage.totalTokens) parts.push(`Σ${usage.totalTokens}`);
        return parts.join(' ');
    }

    function handleError(msg) {
        isStreaming = false;
        elBtnAbort.classList.add('hidden');
        removeStreamingIndicator();
        appendSystemMessage(`Error: ${msg.message || 'Unknown error'}${msg.code ? ` (${msg.code})` : ''}`, 'error');
    }

    // =========================================================================
    // Chat message rendering
    // =========================================================================

    function appendChatMessage(role, content) {
        if (!content || !content.trim()) return;
        const div = document.createElement('div');
        div.className = `message ${role}`;
        const now = formatTime(new Date().toISOString());
        const contentHtml = role === 'assistant' ? renderMarkdown(content) : escapeHtml(content);
        div.innerHTML = `
            <div class="msg-header">
                <span class="msg-role">${escapeHtml(role.toUpperCase())}</span>
                <span class="msg-time">${now}</span>
            </div>
            <div class="msg-content">${contentHtml}</div>
        `;
        elChatMessages.appendChild(div);
        scrollToBottom();
    }

    function appendSystemMessage(text, level) {
        const div = document.createElement('div');
        div.className = `message system-msg${level ? ' ' + level : ''}`;
        div.textContent = text;
        elChatMessages.appendChild(div);
        scrollToBottom();
    }

    function appendDelta(content) {
        if (!content) return;
        let streaming = elChatMessages.querySelector('.message.assistant.streaming');
        if (!streaming) {
            streaming = document.createElement('div');
            streaming.className = 'message assistant streaming';
            streaming.innerHTML = `
                <div class="msg-header">
                    <span class="msg-role">ASSISTANT</span>
                    <span class="msg-time">streaming...</span>
                </div>
                <div class="msg-content"><span class="delta-content"></span></div>
            `;
            elChatMessages.appendChild(streaming);
        }
        streaming.querySelector('.delta-content').textContent += content;
        scrollToBottom();
    }

    // =========================================================================
    // History rendering
    // =========================================================================

    function renderHistoryEntry(entry) {
        if (!entry) return;
        if ((entry.role === 'user' || entry.role === 'assistant') && entry.content && entry.content.trim()) {
            appendChatMessage(entry.role, entry.content);
        }
        if (entry.role === 'assistant' && entry.toolCalls && entry.toolCalls.length > 0) {
            for (const tc of entry.toolCalls) renderToolCallHistory(tc);
        }
        if (entry.role === 'tool') renderToolCallHistory(entry);
    }

    function renderToolCallHistory(tc) {
        const div = document.createElement('div');
        div.className = `message tool-call tool-complete${showTools ? '' : ' hidden'}`;
        const toolName = tc.toolName || tc.name || 'unknown';
        const argsPreview = formatToolArgsPreview(tc);
        div.innerHTML = `
            <span class="tool-icon" aria-hidden="true">🔧</span>
            <span class="tool-name">${escapeHtml(toolName)}</span>
            <span class="tool-args-preview">${escapeHtml(argsPreview)}</span>
            <span class="tool-status-badge complete">✓ Done</span>
        `;
        div.style.cursor = 'pointer';
        div.addEventListener('click', () => openToolModal({
            toolName,
            args: JSON.stringify(tc.arguments || tc.args || {}, null, 2),
            result: tc.content || tc.output || tc.result || '(no result)'
        }));
        elChatMessages.appendChild(div);
    }

    function formatToolArgsPreview(entry) {
        const args = entry.arguments || entry.args || {};
        if (typeof args === 'string') return args.length > 80 ? args.substring(0, 80) + '...' : args;
        const pairs = [];
        for (const [key, value] of Object.entries(args)) {
            let valStr = typeof value === 'string'
                ? (value.length > 30 ? value.substring(0, 30) + '...' : value)
                : JSON.stringify(value);
            if (valStr && valStr.length > 30) valStr = valStr.substring(0, 30) + '...';
            pairs.push(`${key}: "${valStr}"`);
        }
        const preview = pairs.join(', ');
        return preview.length > 80 ? preview.substring(0, 80) + '...' : preview;
    }

    // =========================================================================
    // Tool modal
    // =========================================================================

    function openToolModal(toolData) {
        $('#modal-tool-name').textContent = toolData.toolName || 'unknown';
        $('#modal-tool-args').textContent = typeof toolData.args === 'string'
            ? toolData.args : JSON.stringify(toolData.args || {}, null, 2);
        $('#modal-tool-result').textContent = toolData.result || '(no result)';
        elToolModal.classList.remove('hidden');
    }

    function closeToolModal() { elToolModal.classList.add('hidden'); }

    // =========================================================================
    // Chat actions
    // =========================================================================

    function sendMessage() {
        const text = elChatInput.value.trim();
        if (!text) return;
        elChatInput.value = '';
        autoResize(elChatInput);
        appendChatMessage('user', text);

        if (ws && ws.readyState === WebSocket.OPEN) {
            sendWs({ type: 'message', content: text });
        } else {
            sendViaRest(text);
        }
    }

    async function sendViaRest(text) {
        showStreamingIndicator();
        const body = { agentId: currentAgentId || (elAgentSelect.value || undefined), message: text };
        if (currentSessionId) body.sessionId = currentSessionId;

        try {
            const res = await fetch(`${API_BASE}/chat`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(body)
            });
            removeStreamingIndicator();
            if (res.ok) {
                const data = await res.json();
                if (data.sessionId) currentSessionId = data.sessionId;
                if (data.content) appendChatMessage('assistant', data.content);
                if (data.usage) {
                    const usageStr = formatUsage(data.usage);
                    if (usageStr) {
                        const badge = document.createElement('div');
                        badge.className = 'msg-footer';
                        badge.textContent = usageStr;
                        const last = elChatMessages.lastElementChild;
                        if (last) last.appendChild(badge);
                    }
                }
                loadSessions();
            } else {
                appendSystemMessage(`Error: ${res.status} — ${await res.text()}`, 'error');
            }
        } catch (e) {
            removeStreamingIndicator();
            appendSystemMessage(`Connection error: ${e.message}`, 'error');
        }
    }

    function abortRequest() {
        sendWs({ type: 'abort' });
        isStreaming = false;
        elBtnAbort.classList.add('hidden');
        removeStreamingIndicator();
        appendSystemMessage('Request aborted.');
    }

    function startNewChat() {
        disconnectWebSocket();
        currentSessionId = null;
        activeMessageId = null;
        activeToolCalls = {};
        activeToolCount = 0;
        thinkingBuffer = '';

        elWelcome.classList.add('hidden');
        elChatView.classList.remove('hidden');
        elChatTitle.textContent = 'New Chat';
        elChatMeta.textContent = `Agent: ${elAgentSelect.value || 'default'} · Session will be created on first message`;
        elChatMessages.innerHTML = '';
        elBtnSend.disabled = false;
        elAgentSelect.disabled = false;
        elSessionsList.querySelectorAll('.list-item').forEach(el => el.classList.remove('active'));

        currentAgentId = elAgentSelect.value || null;
        if (currentAgentId) connectWebSocket();
        elChatInput.focus();
    }

    // =========================================================================
    // REST API helpers
    // =========================================================================

    async function fetchJson(path) {
        try {
            const res = await fetch(`${API_BASE}${path}`);
            if (!res.ok) return null;
            return await res.json();
        } catch (e) {
            console.error(`API error (${path}):`, e);
            return null;
        }
    }

    // =========================================================================
    // Sessions
    // =========================================================================

    async function loadSessions() {
        elSessionsList.innerHTML = '<div class="loading">Loading...</div>';
        const sessions = await fetchJson('/sessions');
        if (!sessions || sessions.length === 0) {
            elSessionsList.innerHTML = '<div class="empty-state">No sessions yet</div>';
            return;
        }
        elSessionsList.innerHTML = '';
        sessions.sort((a, b) => new Date(b.updatedAt || b.createdAt || 0) - new Date(a.updatedAt || a.createdAt || 0));

        for (const s of sessions) {
            const el = document.createElement('div');
            el.className = 'list-item' + (s.sessionId === currentSessionId ? ' active' : '');
            el.dataset.sessionId = s.sessionId;
            el.setAttribute('role', 'listitem');
            const timeStr = relativeTime(s.updatedAt || s.createdAt);
            const agentName = s.agentId || s.agentName || 'Chat';
            const msgCount = s.messageCount || 0;
            el.innerHTML = `
                <div class="list-item-row">
                    <span class="item-title">${escapeHtml(agentName)}</span>
                    <button class="btn-delete-session" title="Delete session" aria-label="Delete session">✕</button>
                </div>
                <span class="item-meta">${msgCount} msgs · ${timeStr}</span>
            `;
            el.querySelector('.btn-delete-session').addEventListener('click', (e) => {
                e.stopPropagation();
                deleteSession(s.sessionId);
            });
            el.addEventListener('click', (e) => {
                if (e.target.closest('.btn-delete-session')) return;
                openSession(s.sessionId, s.agentId || s.agentName);
            });
            elSessionsList.appendChild(el);
        }
    }

    async function deleteSession(sessionId) {
        showConfirm(
            `Delete session ${sessionId.substring(0, 8)}...? This cannot be undone.`,
            'Delete Session',
            async () => {
                try {
                    const res = await fetch(`${API_BASE}/sessions/${encodeURIComponent(sessionId)}`, { method: 'DELETE' });
                    if (res.ok || res.status === 204) {
                        if (currentSessionId === sessionId) {
                            disconnectWebSocket();
                            currentSessionId = null;
                            elChatView.classList.add('hidden');
                            elWelcome.classList.remove('hidden');
                        }
                        loadSessions();
                    } else {
                        appendSystemMessage(`Failed to delete session: ${res.status}`, 'error');
                    }
                } catch (e) {
                    appendSystemMessage(`Failed to delete session: ${e.message}`, 'error');
                }
            }
        );
    }

    async function openSession(sessionId, agentId) {
        disconnectWebSocket();
        currentSessionId = sessionId;
        currentAgentId = agentId || null;

        elSessionsList.querySelectorAll('.list-item').forEach(el => {
            el.classList.toggle('active', el.dataset.sessionId === sessionId);
        });

        elWelcome.classList.add('hidden');
        elChatView.classList.remove('hidden');
        elChatTitle.textContent = agentId || 'Chat';
        elChatMessages.innerHTML = '<div class="loading">Loading messages...</div>';
        elBtnSend.disabled = false;

        if (agentId) elAgentSelect.value = agentId;
        elAgentSelect.disabled = true;

        const session = await fetchJson(`/sessions/${encodeURIComponent(sessionId)}`);
        elChatMessages.innerHTML = '';

        if (session) {
            const msgCount = session.history ? session.history.length : (session.messageCount || 0);
            elChatMeta.textContent = `Agent: ${agentId || 'unknown'} · ${msgCount} messages`;
            if (session.history) {
                for (const entry of session.history) renderHistoryEntry(entry);
            }
        }

        if (currentAgentId) connectWebSocket();
        scrollToBottom();
        elChatInput.focus();
    }

    // =========================================================================
    // Agents
    // =========================================================================

    async function loadAgents() {
        elAgentsList.innerHTML = '<div class="loading">Loading...</div>';
        const agents = await fetchJson('/agents');
        if (!agents || agents.length === 0) {
            elAgentsList.innerHTML = '<div class="empty-state">No agents configured</div>';
            return;
        }
        agentsCache = agents;
        elAgentsList.innerHTML = '';
        for (const a of agents) {
            const el = document.createElement('div');
            el.className = 'list-item';
            el.setAttribute('role', 'listitem');
            const name = a.name || a.agentId || a.id || 'unknown';
            const model = a.model || a.defaultModel || '';
            const statusClass = a.status === 'error' ? 'error' : (a.status === 'busy' ? 'warning' : 'success');
            el.innerHTML = `
                <div class="list-item-row">
                    <span class="item-title">
                        <span class="agent-status-dot ${statusClass}" aria-hidden="true"></span>
                        ${escapeHtml(name)}
                    </span>
                </div>
                <span class="item-meta">${model ? 'Model: ' + escapeHtml(model) : ''}</span>
            `;
            el.addEventListener('click', () => { elAgentSelect.value = name; currentAgentId = name; });
            elAgentsList.appendChild(el);
        }
        populateAgentSelect(agents);
    }

    function populateAgentSelect(agents) {
        elAgentSelect.innerHTML = '';
        for (const a of agents) {
            const opt = document.createElement('option');
            const name = a.name || a.agentId || a.id || 'unknown';
            opt.value = name;
            opt.textContent = name;
            elAgentSelect.appendChild(opt);
        }
        if (!currentAgentId && agents.length > 0) {
            currentAgentId = agents[0].name || agents[0].agentId || agents[0].id;
        }
    }

    // =========================================================================
    // Agent form modal
    // =========================================================================

    async function openAddAgentForm() {
        $('#agent-form-title').textContent = 'Add Agent';
        elAgentForm.reset();
        $('#form-agent-name').disabled = false;
        $('#form-agent-temperature').disabled = true;
        $('#form-agent-max-tokens').disabled = true;
        $('#form-feedback').className = 'form-feedback hidden';

        const providerSelect = $('#form-agent-provider');
        providerSelect.innerHTML = '<option value="">Select provider...</option>';
        const providers = await fetchJson('/providers');
        if (providers && providers.length > 0) {
            providersCache = providers;
            for (const p of providers) {
                const opt = document.createElement('option');
                opt.value = p.name || p.providerId || p.id || 'unknown';
                opt.textContent = opt.value;
                providerSelect.appendChild(opt);
            }
        }

        elAgentFormModal.classList.remove('hidden');
    }

    function closeAgentForm() {
        elAgentFormModal.classList.add('hidden');
        elAgentForm.reset();
        $('#form-feedback').className = 'form-feedback hidden';
    }

    async function loadModelsForProvider(providerName) {
        const modelSelect = $('#form-agent-model');
        modelSelect.innerHTML = '<option value="">Loading models...</option>';
        const models = await fetchJson('/models');
        modelSelect.innerHTML = '<option value="">Select model...</option>';
        if (models && models.length > 0) {
            modelsCache = models;
            const filtered = providerName
                ? models.filter(m => (m.provider || '').toLowerCase() === providerName.toLowerCase())
                : models;
            for (const m of filtered) {
                const opt = document.createElement('option');
                opt.value = m.name || m.modelId || m.id || 'unknown';
                opt.textContent = opt.value;
                modelSelect.appendChild(opt);
            }
        }
    }

    async function saveAgent() {
        const name = $('#form-agent-name').value.trim();
        const provider = $('#form-agent-provider').value;
        const model = $('#form-agent-model').value;
        const systemPrompt = $('#form-agent-system-prompt').value.trim();
        const feedback = $('#form-feedback');

        if (!name || !provider || !model) {
            feedback.textContent = 'Name, provider, and model are required.';
            feedback.className = 'form-feedback form-error';
            return;
        }

        const body = { name, provider, model };
        if (systemPrompt) body.systemPrompt = systemPrompt;
        if ($('#form-agent-temperature-enabled').checked) body.temperature = parseFloat($('#form-agent-temperature').value);
        if ($('#form-agent-max-tokens-enabled').checked) body.maxTokens = parseInt($('#form-agent-max-tokens').value, 10);

        try {
            const res = await fetch(`${API_BASE}/agents`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(body)
            });
            if (res.ok || res.status === 201) {
                feedback.textContent = `Agent "${name}" created!`;
                feedback.className = 'form-feedback form-success';
                setTimeout(() => { closeAgentForm(); loadAgents(); }, 1000);
            } else {
                feedback.textContent = `Error: ${res.status} — ${await res.text()}`;
                feedback.className = 'form-feedback form-error';
            }
        } catch (e) {
            feedback.textContent = `Error: ${e.message}`;
            feedback.className = 'form-feedback form-error';
        }
    }

    // =========================================================================
    // Activity monitor
    // =========================================================================

    function handleActivityEvent(evt) {
        if (!isActivitySubscribed) return;
        const el = document.createElement('div');
        let cssClass = 'activity-item';
        const eventType = evt.eventType || evt.event || 'unknown';
        if (eventType.includes('Error') || eventType === 'error') cssClass += ' error';
        else if (eventType.includes('Response') || eventType.includes('Sent')) cssClass += ' response-sent';
        else if (eventType.includes('Message') || eventType.includes('Received')) cssClass += ' msg-received';
        el.className = cssClass;

        const time = formatTime(evt.timestamp || new Date().toISOString());
        const channel = evt.channel || evt.source || '';
        const preview = (evt.content || evt.message || '').substring(0, 80);
        el.innerHTML = `
            <span class="activity-time">${time}</span>
            ${channel ? `<span class="activity-channel">[${escapeHtml(channel)}]</span>` : ''}
            <strong>${escapeHtml(eventType)}</strong>${preview ? ': ' + escapeHtml(preview) : ''}${(evt.content || evt.message || '').length > 80 ? '...' : ''}
        `;
        elActivityFeed.insertBefore(el, elActivityFeed.firstChild);
        while (elActivityFeed.children.length > MAX_ACTIVITY_ITEMS) {
            elActivityFeed.removeChild(elActivityFeed.lastChild);
        }
    }

    // =========================================================================
    // Toggle visibility
    // =========================================================================

    function toggleToolVisibility() {
        showTools = elToggleTools.checked;
        elChatMessages.querySelectorAll('.message.tool-call').forEach(el => {
            el.classList.toggle('hidden', !showTools);
        });
    }

    function toggleThinkingVisibility() {
        showThinking = elToggleThinking.checked;
        elChatMessages.querySelectorAll('.thinking-block').forEach(el => {
            el.classList.toggle('collapsed', !showThinking);
            const toggle = el.querySelector('.thinking-toggle');
            if (toggle) {
                toggle.setAttribute('aria-expanded', showThinking);
                el.querySelector('.thinking-chevron').textContent = showThinking ? '▾' : '▸';
            }
        });
    }

    function toggleActivity() {
        isActivitySubscribed = elToggleActivity.checked;
        if (isActivitySubscribed) {
            sendWs({ type: 'subscribe' });
            elActivityFeed.classList.remove('collapsed');
        } else {
            elActivityFeed.classList.add('collapsed');
        }
    }

    // =========================================================================
    // Section toggle (sidebar collapsible sections)
    // =========================================================================

    function initSectionToggles() {
        $$('.section-header[data-toggle]').forEach(header => {
            header.addEventListener('click', (e) => {
                if (e.target.closest('.btn-icon') || e.target.closest('.toggle-switch')) return;
                const target = document.getElementById(header.dataset.toggle);
                if (target) target.classList.toggle('collapsed');
            });
        });
    }

    // =========================================================================
    // Event listeners
    // =========================================================================

    function initEventListeners() {
        $('#btn-new-chat').addEventListener('click', startNewChat);
        elBtnSend.addEventListener('click', sendMessage);
        elBtnAbort.addEventListener('click', abortRequest);

        elChatInput.addEventListener('keydown', (e) => {
            if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); sendMessage(); }
        });
        elChatInput.addEventListener('input', () => autoResize(elChatInput));

        elAgentSelect.addEventListener('change', () => {
            currentAgentId = elAgentSelect.value;
            if (!currentSessionId && ws) { disconnectWebSocket(); connectWebSocket(); }
        });

        elToggleTools.addEventListener('change', toggleToolVisibility);
        elToggleThinking.addEventListener('change', toggleThinkingVisibility);
        elToggleActivity.addEventListener('change', toggleActivity);

        $('#btn-refresh-sessions').addEventListener('click', (e) => { e.stopPropagation(); loadSessions(); });
        $('#btn-refresh-agents').addEventListener('click', (e) => { e.stopPropagation(); loadAgents(); });

        // Tool modal
        elModalClose.addEventListener('click', closeToolModal);
        elModalOverlay.addEventListener('click', closeToolModal);

        // Agent form modal
        $('#btn-add-agent').addEventListener('click', (e) => { e.stopPropagation(); openAddAgentForm(); });
        elAgentFormModal.querySelector('.agent-form-close').addEventListener('click', closeAgentForm);
        elAgentFormModal.querySelector('.agent-form-overlay').addEventListener('click', closeAgentForm);
        $('#btn-cancel-agent').addEventListener('click', closeAgentForm);
        $('#btn-save-agent').addEventListener('click', saveAgent);
        $('#form-agent-provider').addEventListener('change', () => { loadModelsForProvider($('#form-agent-provider').value); });
        $('#form-agent-temperature-enabled').addEventListener('change', (e) => { $('#form-agent-temperature').disabled = !e.target.checked; });
        $('#form-agent-max-tokens-enabled').addEventListener('change', (e) => { $('#form-agent-max-tokens').disabled = !e.target.checked; });

        // Confirm dialog
        $('#btn-confirm-ok').addEventListener('click', () => { if (confirmCallback) confirmCallback(); closeConfirm(); });
        $('#btn-confirm-cancel').addEventListener('click', closeConfirm);
        elConfirmDialog.querySelector('.confirm-overlay').addEventListener('click', closeConfirm);

        // Global escape
        document.addEventListener('keydown', (e) => {
            if (e.key === 'Escape') {
                if (!elToolModal.classList.contains('hidden')) closeToolModal();
                else if (!elAgentFormModal.classList.contains('hidden')) closeAgentForm();
                else if (!elConfirmDialog.classList.contains('hidden')) closeConfirm();
            }
        });
    }

    // =========================================================================
    // Initialization
    // =========================================================================

    function init() {
        initMarkdown();
        initSectionToggles();
        initEventListeners();
        loadSessions();
        loadAgents();
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
