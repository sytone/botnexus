// BotNexus WebUI — connects to the Gateway via WebSocket and REST APIs
(function () {
    'use strict';

    // --- Configuration ---
    const API_BASE = '/api';
    const WS_PATH = '/ws';
    const RECONNECT_BASE_MS = 1000;
    const RECONNECT_MAX_MS = 30000;
    const PING_INTERVAL_MS = 30000;

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
    /** @type {Object<string, {toolName:string, args:string, result:string}>} */
    let activeToolCalls = {};
    /** @type {Array} */
    let agentsCache = [];

    // --- DOM refs ---
    const $ = (sel) => document.querySelector(sel);
    const $$ = (sel) => document.querySelectorAll(sel);

    const elApp = $('#app');
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
    const elToolModal = $('#tool-modal');
    const elModalClose = $('.modal-close');
    const elModalOverlay = $('.modal-overlay');

    // =========================================================================
    // Markdown rendering
    // =========================================================================

    /**
     * Configure marked.js for safe markdown rendering.
     */
    function initMarkdown() {
        if (typeof marked !== 'undefined') {
            marked.setOptions({
                breaks: true,
                gfm: true,
                headerIds: false,
                mangle: false
            });
        }
    }

    /**
     * Render markdown string to sanitized HTML.
     * @param {string} text - Raw markdown text
     * @returns {string} Sanitized HTML
     */
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

    /**
     * Escape HTML special characters to prevent XSS.
     * @param {string} str
     * @returns {string}
     */
    function escapeHtml(str) {
        if (!str) return '';
        const div = document.createElement('div');
        div.textContent = str;
        return div.innerHTML;
    }

    /**
     * Format an ISO timestamp to a short time string.
     * @param {string} iso - ISO 8601 timestamp
     * @returns {string}
     */
    function formatTime(iso) {
        if (!iso) return '';
        try {
            const d = new Date(iso);
            return d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
        } catch {
            return '';
        }
    }

    /**
     * Format a timestamp as a relative time string (e.g. "2m ago").
     * @param {string} iso
     * @returns {string}
     */
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
        } catch {
            return '';
        }
    }

    /**
     * Scroll the chat messages area to the bottom.
     */
    function scrollToBottom() {
        requestAnimationFrame(() => {
            elChatMessages.scrollTop = elChatMessages.scrollHeight;
        });
    }

    /**
     * Auto-resize textarea based on content.
     * @param {HTMLTextAreaElement} el
     */
    function autoResize(el) {
        el.style.height = 'auto';
        el.style.height = Math.min(el.scrollHeight, 200) + 'px';
    }

    // =========================================================================
    // Connection status
    // =========================================================================

    /**
     * Update the connection status indicator.
     * @param {'connected'|'disconnected'|'connecting'} state
     */
    function setStatus(state) {
        elConnectionStatus.className = `status ${state}`;
        const labels = {
            connected: 'Connected',
            disconnected: 'Disconnected',
            connecting: 'Connecting...'
        };
        elStatusText.textContent = labels[state] || state;
    }

    // =========================================================================
    // WebSocket connection
    // =========================================================================

    /**
     * Open a WebSocket connection to the Gateway for the current agent/session.
     */
    function connectWebSocket() {
        if (ws && (ws.readyState === WebSocket.OPEN || ws.readyState === WebSocket.CONNECTING)) {
            return;
        }

        if (!currentAgentId) return;

        setStatus('connecting');
        const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
        let url = `${proto}//${location.host}${WS_PATH}?agent=${encodeURIComponent(currentAgentId)}`;
        if (currentSessionId) {
            url += `&session=${encodeURIComponent(currentSessionId)}`;
        }

        ws = new WebSocket(url);

        ws.onopen = () => {
            setStatus('connected');
            reconnectAttempts = 0;
            startPing();
        };

        ws.onclose = () => {
            setStatus('disconnected');
            connectionId = null;
            stopPing();
            scheduleReconnect();
        };

        ws.onerror = () => {
            setStatus('disconnected');
        };

        ws.onmessage = (event) => {
            try {
                const msg = JSON.parse(event.data);
                handleWsMessage(msg);
            } catch (e) {
                console.error('Failed to parse WS message:', e);
            }
        };
    }

    /**
     * Disconnect the current WebSocket connection.
     */
    function disconnectWebSocket() {
        stopPing();
        if (reconnectTimer) {
            clearTimeout(reconnectTimer);
            reconnectTimer = null;
        }
        if (ws) {
            ws.onclose = null;
            ws.close();
            ws = null;
        }
        setStatus('disconnected');
        connectionId = null;
    }

    /**
     * Schedule a reconnection attempt with exponential backoff.
     */
    function scheduleReconnect() {
        if (reconnectTimer) return;
        const delay = Math.min(RECONNECT_BASE_MS * Math.pow(2, reconnectAttempts), RECONNECT_MAX_MS);
        reconnectAttempts++;
        reconnectTimer = setTimeout(() => {
            reconnectTimer = null;
            connectWebSocket();
        }, delay);
    }

    /**
     * Start the ping keepalive interval.
     */
    function startPing() {
        stopPing();
        pingTimer = setInterval(() => {
            sendWs({ type: 'ping' });
        }, PING_INTERVAL_MS);
    }

    /**
     * Stop the ping keepalive interval.
     */
    function stopPing() {
        if (pingTimer) {
            clearInterval(pingTimer);
            pingTimer = null;
        }
    }

    /**
     * Send a JSON message over WebSocket.
     * @param {Object} obj
     */
    function sendWs(obj) {
        if (ws && ws.readyState === WebSocket.OPEN) {
            ws.send(JSON.stringify(obj));
        }
    }

    // =========================================================================
    // WebSocket message handler
    // =========================================================================

    /**
     * Route incoming WebSocket messages to appropriate handlers.
     * @param {Object} msg - Parsed message object
     */
    function handleWsMessage(msg) {
        switch (msg.type) {
            case 'connected':
                connectionId = msg.connectionId;
                if (msg.sessionId) {
                    currentSessionId = msg.sessionId;
                }
                break;

            case 'message_start':
                activeMessageId = msg.messageId;
                isStreaming = true;
                elBtnAbort.classList.remove('hidden');
                showThinkingIndicator();
                break;

            case 'content_delta':
                removeThinkingIndicator();
                appendDelta(msg.delta);
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

            case 'pong':
                // Keepalive acknowledged
                break;
        }
    }

    /**
     * Handle tool_start WebSocket message.
     * @param {Object} msg
     */
    function handleToolStart(msg) {
        const callId = msg.toolCallId || 'unknown';
        activeToolCalls[callId] = {
            toolName: msg.toolName || 'unknown',
            args: '',
            result: ''
        };
        appendToolProgress(msg.toolName, 'running');
    }

    /**
     * Handle tool_end WebSocket message.
     * @param {Object} msg
     */
    function handleToolEnd(msg) {
        const callId = msg.toolCallId || 'unknown';
        if (activeToolCalls[callId]) {
            activeToolCalls[callId].result = msg.toolResult || '';
        }
        updateToolProgress(callId, 'complete');
    }

    /**
     * Finalize a streamed message — clean up state, update usage.
     * @param {Object} msg
     */
    function finalizeMessage(msg) {
        isStreaming = false;
        activeMessageId = null;
        elBtnAbort.classList.add('hidden');
        removeThinkingIndicator();

        // Finalize any streaming element
        const streaming = elChatMessages.querySelector('.message.assistant.streaming');
        if (streaming) {
            streaming.classList.remove('streaming');
            const deltaEl = streaming.querySelector('.delta-content');
            if (deltaEl) {
                const rawText = deltaEl.textContent;
                deltaEl.innerHTML = renderMarkdown(rawText);
            }
            const timeEl = streaming.querySelector('.msg-time');
            if (timeEl) {
                timeEl.textContent = formatTime(new Date().toISOString());
            }
        }

        // Display usage info if available
        if (msg.usage) {
            const usageStr = formatUsage(msg.usage);
            if (usageStr) {
                const badge = document.createElement('div');
                badge.className = 'usage-badge';
                badge.textContent = usageStr;
                const target = streaming || elChatMessages.lastElementChild;
                if (target) target.appendChild(badge);
            }
        }

        // Clear active tool calls for this message
        activeToolCalls = {};

        // Refresh sessions list (new session may have been created)
        loadSessions();
        scrollToBottom();
    }

    /**
     * Format usage data into a short string.
     * @param {Object} usage
     * @returns {string}
     */
    function formatUsage(usage) {
        if (!usage) return '';
        const parts = [];
        if (usage.inputTokens) parts.push(`↑${usage.inputTokens}`);
        if (usage.outputTokens) parts.push(`↓${usage.outputTokens}`);
        if (usage.totalTokens) parts.push(`Σ${usage.totalTokens}`);
        return parts.join(' ');
    }

    /**
     * Handle an error message from WebSocket.
     * @param {Object} msg
     */
    function handleError(msg) {
        isStreaming = false;
        elBtnAbort.classList.add('hidden');
        removeThinkingIndicator();
        appendSystemMessage(`Error: ${msg.message || 'Unknown error'}${msg.code ? ` (${msg.code})` : ''}`);
    }

    // =========================================================================
    // Thinking indicator
    // =========================================================================

    /**
     * Show a thinking/processing indicator in chat.
     */
    function showThinkingIndicator() {
        removeThinkingIndicator();
        const div = document.createElement('div');
        div.className = 'message thinking';
        div.setAttribute('aria-label', 'Agent is thinking');
        div.innerHTML = '<span class="thinking-dots" aria-hidden="true">●●●</span> Agent is thinking...';
        elChatMessages.appendChild(div);
        scrollToBottom();
    }

    /**
     * Remove the thinking indicator from chat.
     */
    function removeThinkingIndicator() {
        elChatMessages.querySelectorAll('.message.thinking').forEach(el => el.remove());
    }

    // =========================================================================
    // Chat message rendering
    // =========================================================================

    /**
     * Append a chat message bubble for a given role.
     * @param {'user'|'assistant'|'system'} role
     * @param {string} content
     */
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

    /**
     * Append a system/status message to chat.
     * @param {string} text
     */
    function appendSystemMessage(text) {
        const div = document.createElement('div');
        div.className = 'message system-msg';
        div.textContent = text;
        elChatMessages.appendChild(div);
        scrollToBottom();
    }

    /**
     * Append a streaming delta to the current assistant message.
     * @param {string} content
     */
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
        const deltaEl = streaming.querySelector('.delta-content');
        deltaEl.textContent += content;
        scrollToBottom();
    }

    /**
     * Append a tool progress indicator to the chat.
     * @param {string} toolName
     * @param {'running'|'complete'} status
     */
    function appendToolProgress(toolName, status) {
        if (!showTools) return;
        const div = document.createElement('div');
        div.className = `message tool-call ${status === 'complete' ? 'tool-complete' : 'tool-running'}`;
        div.dataset.toolName = toolName;
        div.innerHTML = `
            <span class="tool-icon" aria-hidden="true">🔧</span>
            <span class="tool-name">${escapeHtml(toolName)}</span>
            <span class="tool-status">${status === 'running' ? '⏳' : '✓'}</span>
        `;
        elChatMessages.appendChild(div);
        scrollToBottom();
    }

    /**
     * Update an existing tool progress indicator.
     * @param {string} callId
     * @param {'complete'} status
     */
    function updateToolProgress(callId, status) {
        if (!showTools) return;
        const toolData = activeToolCalls[callId];
        if (!toolData) return;

        // Find the last running tool indicator for this tool name
        const indicators = elChatMessages.querySelectorAll('.message.tool-call.tool-running');
        for (const el of indicators) {
            if (el.dataset.toolName === toolData.toolName) {
                el.classList.remove('tool-running');
                el.classList.add('tool-complete');
                el.querySelector('.tool-status').textContent = '✓';
                el.style.cursor = 'pointer';
                el.addEventListener('click', () => openToolModal(toolData));
                break;
            }
        }
    }

    /**
     * Render a history entry from a loaded session.
     * @param {Object} entry - Message history entry
     */
    function renderHistoryEntry(entry) {
        if (!entry) return;
        if (entry.role === 'user' || entry.role === 'assistant') {
            if (entry.content && entry.content.trim()) {
                appendChatMessage(entry.role, entry.content);
            }
        }
        // Tool calls within assistant messages
        if (entry.role === 'assistant' && entry.toolCalls && entry.toolCalls.length > 0) {
            for (const tc of entry.toolCalls) {
                renderToolCallHistory(tc);
            }
        }
        // Standalone tool results
        if (entry.role === 'tool') {
            renderToolCallHistory(entry);
        }
    }

    /**
     * Render a historical tool call as a compact clickable summary.
     * @param {Object} tc
     */
    function renderToolCallHistory(tc) {
        const div = document.createElement('div');
        div.className = `message tool-call tool-complete ${showTools ? '' : 'hidden'}`;
        const toolName = tc.toolName || tc.name || 'unknown';
        const argsPreview = formatToolArgsPreview(tc);
        div.innerHTML = `
            <span class="tool-icon" aria-hidden="true">🔧</span>
            <span class="tool-name">${escapeHtml(toolName)}</span>
            <span class="tool-args-preview">${escapeHtml(argsPreview)}</span>
        `;
        div.style.cursor = 'pointer';
        div.addEventListener('click', () => openToolModal({
            toolName,
            args: JSON.stringify(tc.arguments || tc.args || {}, null, 2),
            result: tc.content || tc.output || tc.result || '(no result)'
        }));
        elChatMessages.appendChild(div);
    }

    /**
     * Format tool arguments into a short preview string.
     * @param {Object} entry
     * @returns {string}
     */
    function formatToolArgsPreview(entry) {
        const args = entry.arguments || entry.args || {};
        if (typeof args === 'string') {
            return args.length > 80 ? args.substring(0, 80) + '...' : args;
        }
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

    /**
     * Open the tool detail modal.
     * @param {Object} toolData - { toolName, args, result }
     */
    function openToolModal(toolData) {
        $('#modal-tool-name').textContent = toolData.toolName || 'unknown';
        $('#modal-tool-args').textContent = typeof toolData.args === 'string'
            ? toolData.args
            : JSON.stringify(toolData.args || {}, null, 2);
        $('#modal-tool-result').textContent = toolData.result || '(no result)';
        elToolModal.classList.remove('hidden');
    }

    /**
     * Close the tool detail modal.
     */
    function closeToolModal() {
        elToolModal.classList.add('hidden');
    }

    // =========================================================================
    // Chat actions
    // =========================================================================

    /**
     * Send the user's current input as a chat message.
     */
    function sendMessage() {
        const text = elChatInput.value.trim();
        if (!text) return;
        elChatInput.value = '';
        autoResize(elChatInput);

        appendChatMessage('user', text);
        showThinkingIndicator();

        // Send via WebSocket if connected
        if (ws && ws.readyState === WebSocket.OPEN) {
            sendWs({ type: 'message', content: text });
        } else {
            // Fallback to REST /api/chat
            sendViaRest(text);
        }
    }

    /**
     * Send a message via the REST /api/chat endpoint (non-streaming fallback).
     * @param {string} text
     */
    async function sendViaRest(text) {
        const body = {
            agentId: currentAgentId || (elAgentSelect.value || undefined),
            message: text
        };
        if (currentSessionId) body.sessionId = currentSessionId;

        try {
            const res = await fetch(`${API_BASE}/chat`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(body)
            });
            removeThinkingIndicator();
            if (res.ok) {
                const data = await res.json();
                if (data.sessionId) currentSessionId = data.sessionId;
                if (data.content) appendChatMessage('assistant', data.content);
                if (data.usage) {
                    const usageStr = formatUsage(data.usage);
                    if (usageStr) {
                        const badge = document.createElement('div');
                        badge.className = 'usage-badge';
                        badge.textContent = usageStr;
                        const last = elChatMessages.lastElementChild;
                        if (last) last.appendChild(badge);
                    }
                }
                loadSessions();
            } else {
                const errText = await res.text();
                appendSystemMessage(`Error: ${res.status} — ${errText}`);
            }
        } catch (e) {
            removeThinkingIndicator();
            appendSystemMessage(`Connection error: ${e.message}`);
        }
    }

    /**
     * Abort the currently streaming request.
     */
    function abortRequest() {
        sendWs({ type: 'abort' });
        isStreaming = false;
        elBtnAbort.classList.add('hidden');
        removeThinkingIndicator();
        appendSystemMessage('Request aborted.');
    }

    /**
     * Start a new chat session.
     */
    function startNewChat() {
        disconnectWebSocket();
        currentSessionId = null;
        activeMessageId = null;
        activeToolCalls = {};

        elWelcome.classList.add('hidden');
        elChatView.classList.remove('hidden');
        elChatTitle.textContent = 'New Chat';
        elChatMeta.textContent = `Agent: ${elAgentSelect.value || 'default'} · Session will be created on first message`;
        elChatMessages.innerHTML = '';
        elBtnSend.disabled = false;
        elAgentSelect.disabled = false;

        // Deselect sidebar sessions
        elSessionsList.querySelectorAll('.list-item').forEach(el => el.classList.remove('active'));

        currentAgentId = elAgentSelect.value || null;
        if (currentAgentId) {
            connectWebSocket();
        }

        elChatInput.focus();
    }

    // =========================================================================
    // REST API helpers
    // =========================================================================

    /**
     * Fetch JSON from the Gateway REST API.
     * @param {string} path
     * @returns {Promise<any|null>}
     */
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

    /**
     * Load sessions list from the Gateway.
     */
    async function loadSessions() {
        elSessionsList.innerHTML = '<div class="loading">Loading...</div>';
        const sessions = await fetchJson('/sessions');
        if (!sessions || sessions.length === 0) {
            elSessionsList.innerHTML = '<div class="empty-state">No sessions yet</div>';
            return;
        }
        elSessionsList.innerHTML = '';
        // Sort by most recently updated
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
                <span class="item-title">${escapeHtml(agentName)}</span>
                <span class="item-meta">${msgCount} msgs · ${timeStr}</span>
            `;
            el.addEventListener('click', () => openSession(s.sessionId, s.agentId || s.agentName));
            elSessionsList.appendChild(el);
        }
    }

    /**
     * Open an existing session by ID.
     * @param {string} sessionId
     * @param {string} [agentId]
     */
    async function openSession(sessionId, agentId) {
        disconnectWebSocket();
        currentSessionId = sessionId;
        currentAgentId = agentId || null;

        // Update sidebar active state
        elSessionsList.querySelectorAll('.list-item').forEach(el => {
            el.classList.toggle('active', el.dataset.sessionId === sessionId);
        });

        // Show chat view
        elWelcome.classList.add('hidden');
        elChatView.classList.remove('hidden');
        elChatTitle.textContent = agentId || 'Chat';
        elChatMessages.innerHTML = '';
        elBtnSend.disabled = false;

        // Set agent selector
        if (agentId) {
            elAgentSelect.value = agentId;
        }
        elAgentSelect.disabled = true;

        // Load session details
        const session = await fetchJson(`/sessions/${encodeURIComponent(sessionId)}`);
        if (session) {
            const msgCount = session.history ? session.history.length : (session.messageCount || 0);
            elChatMeta.textContent = `Agent: ${agentId || 'unknown'} · ${msgCount} messages`;

            // Render history
            if (session.history) {
                for (const entry of session.history) {
                    renderHistoryEntry(entry);
                }
            }
        }

        // Connect WebSocket for continued chat
        if (currentAgentId) {
            connectWebSocket();
        }

        scrollToBottom();
        elChatInput.focus();
    }

    // =========================================================================
    // Agents
    // =========================================================================

    /**
     * Load agents list from the Gateway.
     */
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
            el.innerHTML = `
                <span class="item-title">${escapeHtml(name)}</span>
                <span class="item-meta">${model ? 'Model: ' + escapeHtml(model) : ''}</span>
            `;
            el.addEventListener('click', () => {
                elAgentSelect.value = name;
                currentAgentId = name;
            });
            elAgentsList.appendChild(el);
        }

        // Populate agent selector dropdown
        populateAgentSelect(agents);
    }

    /**
     * Populate the agent selector dropdown.
     * @param {Array} agents
     */
    function populateAgentSelect(agents) {
        elAgentSelect.innerHTML = '';
        for (const a of agents) {
            const opt = document.createElement('option');
            const name = a.name || a.agentId || a.id || 'unknown';
            opt.value = name;
            opt.textContent = name;
            elAgentSelect.appendChild(opt);
        }
        // Set currentAgentId if not already set
        if (!currentAgentId && agents.length > 0) {
            currentAgentId = agents[0].name || agents[0].agentId || agents[0].id;
        }
    }

    // =========================================================================
    // Tool visibility toggle
    // =========================================================================

    /**
     * Toggle visibility of tool call messages in the chat.
     */
    function toggleToolVisibility() {
        showTools = elToggleTools.checked;
        elChatMessages.querySelectorAll('.message.tool-call').forEach(el => {
            if (showTools) {
                el.classList.remove('hidden');
            } else {
                el.classList.add('hidden');
            }
        });
    }

    // =========================================================================
    // Section toggle (sidebar collapsible sections)
    // =========================================================================

    /**
     * Wire up sidebar section toggle behavior.
     */
    function initSectionToggles() {
        $$('.section-header[data-toggle]').forEach(header => {
            header.addEventListener('click', (e) => {
                // Don't toggle if clicking a button inside the header
                if (e.target.closest('.btn-icon')) return;
                const targetId = header.dataset.toggle;
                const target = document.getElementById(targetId);
                if (target) {
                    target.classList.toggle('collapsed');
                }
            });
        });
    }

    // =========================================================================
    // Event listeners
    // =========================================================================

    /**
     * Wire up all event listeners.
     */
    function initEventListeners() {
        // New chat
        $('#btn-new-chat').addEventListener('click', startNewChat);

        // Send message
        elBtnSend.addEventListener('click', sendMessage);

        // Abort
        elBtnAbort.addEventListener('click', abortRequest);

        // Chat input — Enter to send, Shift+Enter for newline
        elChatInput.addEventListener('keydown', (e) => {
            if (e.key === 'Enter' && !e.shiftKey) {
                e.preventDefault();
                sendMessage();
            }
        });

        // Auto-resize textarea
        elChatInput.addEventListener('input', () => autoResize(elChatInput));

        // Agent selector change
        elAgentSelect.addEventListener('change', () => {
            currentAgentId = elAgentSelect.value;
            if (!currentSessionId && ws) {
                disconnectWebSocket();
                connectWebSocket();
            }
        });

        // Tool visibility toggle
        elToggleTools.addEventListener('change', toggleToolVisibility);

        // Refresh buttons
        $('#btn-refresh-sessions').addEventListener('click', (e) => {
            e.stopPropagation();
            loadSessions();
        });
        $('#btn-refresh-agents').addEventListener('click', (e) => {
            e.stopPropagation();
            loadAgents();
        });

        // Tool modal close
        elModalClose.addEventListener('click', closeToolModal);
        elModalOverlay.addEventListener('click', closeToolModal);
        document.addEventListener('keydown', (e) => {
            if (e.key === 'Escape' && !elToolModal.classList.contains('hidden')) {
                closeToolModal();
            }
        });
    }

    // =========================================================================
    // Initialization
    // =========================================================================

    /**
     * Bootstrap the application.
     */
    function init() {
        initMarkdown();
        initSectionToggles();
        initEventListeners();
        loadSessions();
        loadAgents();
    }

    // Start on DOM ready
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
