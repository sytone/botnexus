// BotNexus Web UI — connects to the gateway via WebSocket and REST APIs
(function () {
    'use strict';

    // --- Configuration ---
    const WS_PATH = '/ws';
    const API_BASE = '/api';
    const MAX_ACTIVITY_ITEMS = 100;
    const RECONNECT_DELAY_MS = 3000;

    // --- State ---
    let ws = null;
    let connectionId = null;
    let currentSessionKey = null;
    let isSubscribed = false;
    let reconnectTimer = null;
    let commandPaletteIndex = -1;
    let availableModels = [];
    let modelsData = null;
    let showTools = false;
    let agentFormMode = 'add'; // 'add' or 'edit'
    let editingAgentName = null;

    // --- DOM refs ---
    const $ = (sel) => document.querySelector(sel);
    const $$ = (sel) => document.querySelectorAll(sel);

    const elSessionsList = $('#sessions-list');
    const elChannelsList = $('#channels-list');
    const elAgentsList = $('#agents-list');
    const elExtensionsSummary = $('#extensions-summary');
    const elProvidersList = $('#providers-list');
    const elToolsList = $('#tools-list');
    const elActivityFeed = $('#activity-feed');
    const elConnectionStatus = $('#connection-status');
    const elStatusText = elConnectionStatus.querySelector('.status-text');
    const elWelcome = $('#welcome-screen');
    const elChatView = $('#chat-view');
    const elChatTitle = $('#chat-title');
    const elChatMeta = $('#chat-meta');
    const elChatMessages = $('#chat-messages');
    const elChatInput = $('#chat-input');
    const elBtnSend = $('#btn-send');
    const elToggleActivity = $('#toggle-activity');
    const elAgentSelect = $('#agent-select');
    const elModelSelect = $('#model-select');
    const elCommandPalette = $('#command-palette');
    const elToggleTools = $('#toggle-tools');
    const elToolModal = $('#tool-modal');
    const elToolModalClose = $('.tool-modal-close');
    const elToolModalOverlay = $('.tool-modal-overlay');
    const elAgentFormModal = $('#agent-form-modal');
    const elAgentFormTitle = $('#agent-form-title');
    const elAgentForm = $('#agent-form');
    const elAgentFormClose = $('.agent-form-close');
    const elAgentFormOverlay = $('.agent-form-overlay');
    const elFormAgentName = $('#form-agent-name');
    const elFormAgentProvider = $('#form-agent-provider');
    const elFormAgentModel = $('#form-agent-model');
    const elFormAgentSystemPrompt = $('#form-agent-system-prompt');
    const elFormAgentTemperature = $('#form-agent-temperature');
    const elFormAgentTemperatureEnabled = $('#form-agent-temperature-enabled');
    const elFormAgentMaxTokens = $('#form-agent-max-tokens');
    const elFormAgentMaxTokensEnabled = $('#form-agent-max-tokens-enabled');
    const elFormFeedback = $('#form-feedback');
    const elBtnSaveAgent = $('#btn-save-agent');
    const elBtnCancelAgent = $('#btn-cancel-agent');
    const elBtnAddAgent = $('#btn-add-agent');
    const elSystemMessageBanner = $('#system-message-banner');
    const elSystemMessageClose = elSystemMessageBanner ? elSystemMessageBanner.querySelector('.system-message-close') : null;

    // --- Commands ---
    const COMMANDS = [
        { name: '/help', description: 'Show available commands and their descriptions' },
        { name: '/reset', description: 'Reset the current conversation session' },
        { name: '/status', description: 'Show system status and last heartbeat time' },
        { name: '/models', description: 'List all available models by provider' },
        { name: '/model', description: 'List all available models by provider (alias for /models)' }
    ];

    function showCommandPalette(filter) {
        const query = filter.toLowerCase();
        const matches = COMMANDS.filter(c => c.name.startsWith(query));
        if (matches.length === 0) {
            hideCommandPalette();
            return;
        }
        commandPaletteIndex = 0;
        elCommandPalette.innerHTML = '';
        for (let i = 0; i < matches.length; i++) {
            const el = document.createElement('div');
            el.className = 'command-item' + (i === 0 ? ' active' : '');
            el.dataset.index = i;
            el.innerHTML = `<span class="command-name">${escapeHtml(matches[i].name)}</span><span class="command-desc">${escapeHtml(matches[i].description)}</span>`;
            el.addEventListener('click', () => acceptCommand(matches[i].name));
            elCommandPalette.appendChild(el);
        }
        const hint = document.createElement('div');
        hint.className = 'command-palette-hint';
        hint.textContent = '↑↓ navigate · Tab or Enter to select · Esc to dismiss';
        elCommandPalette.appendChild(hint);
        elCommandPalette.classList.remove('hidden');
    }

    function hideCommandPalette() {
        elCommandPalette.classList.add('hidden');
        elCommandPalette.innerHTML = '';
        commandPaletteIndex = -1;
    }

    function acceptCommand(name) {
        elChatInput.value = name + ' ';
        hideCommandPalette();
        elChatInput.focus();
        autoResize(elChatInput);
    }

    function navigateCommandPalette(direction) {
        const items = elCommandPalette.querySelectorAll('.command-item');
        if (items.length === 0) return;
        items[commandPaletteIndex]?.classList.remove('active');
        commandPaletteIndex = (commandPaletteIndex + direction + items.length) % items.length;
        items[commandPaletteIndex].classList.add('active');
        items[commandPaletteIndex].scrollIntoView({ block: 'nearest' });
    }

    function isCommandPaletteVisible() {
        return !elCommandPalette.classList.contains('hidden');
    }

    // --- WebSocket ---
    function connect() {
        if (ws && (ws.readyState === WebSocket.OPEN || ws.readyState === WebSocket.CONNECTING)) return;

        setStatus('connecting');
        const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
        ws = new WebSocket(`${proto}//${location.host}${WS_PATH}`);

        ws.onopen = () => {
            setStatus('connected');
            if (isSubscribed) sendWs({ type: 'subscribe' });
        };

        ws.onclose = () => {
            setStatus('disconnected');
            connectionId = null;
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

    function scheduleReconnect() {
        if (reconnectTimer) return;
        reconnectTimer = setTimeout(() => {
            reconnectTimer = null;
            connect();
        }, RECONNECT_DELAY_MS);
    }

    function sendWs(obj) {
        if (ws && ws.readyState === WebSocket.OPEN) {
            ws.send(JSON.stringify(obj));
        }
    }

    function setStatus(state) {
        elConnectionStatus.className = `status ${state}`;
        const labels = { connected: 'Connected', disconnected: 'Disconnected', connecting: 'Connecting...' };
        elStatusText.textContent = labels[state] || state;
    }

    function cleanupStreamingElements() {
        // Remove all streaming indicators when a final response arrives
        const streamingElements = elChatMessages.querySelectorAll('.message.assistant.streaming');
        streamingElements.forEach(el => el.remove());
        
        // Remove thinking indicator when final response arrives
        const thinkingElements = elChatMessages.querySelectorAll('.message.thinking');
        thinkingElements.forEach(el => el.remove());
        
        // Remove standalone tool progress indicators
        const toolProgressElements = elChatMessages.querySelectorAll('.tool-progress-indicator');
        toolProgressElements.forEach(el => el.remove());
    }

    function showThinkingIndicator() {
        // Remove any existing thinking indicator first
        const existing = elChatMessages.querySelectorAll('.message.thinking');
        existing.forEach(el => el.remove());
        
        // Create and insert thinking indicator
        const div = document.createElement('div');
        div.className = 'message thinking';
        div.innerHTML = '<span class="thinking-dots">●●●</span> Agent is thinking...';
        elChatMessages.appendChild(div);
        scrollToBottom();
    }

    // --- WS message handler ---
    function handleWsMessage(msg) {
        switch (msg.type) {
            case 'connected':
                connectionId = msg.connection_id;
                break;
            case 'response':
                // Clean up any streaming indicators first
                cleanupStreamingElements();
                
                // Check if response includes tool calls
                if (msg.toolCalls && msg.toolCalls.length > 0) {
                    renderAssistantWithToolsLive(msg);
                } else if (msg.content && msg.content.trim()) {
                    // Only render if content is non-empty
                    appendChatMessage('assistant', msg.content);
                }
                break;
            case 'delta':
                appendDelta(msg.content);
                break;
            case 'tool_progress':
                appendToolProgress(msg);
                break;
            case 'activity':
                if (msg.event) {
                    addActivityItem(msg.event);
                    // Handle system messages
                    if (msg.event.eventType === 'SystemMessage') {
                        handleSystemMessage(msg.event);
                    }
                }
                break;
        }
    }

    // --- REST API ---
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

    async function postJson(path, body) {
        try {
            const res = await fetch(`${API_BASE}${path}`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(body)
            });
            return { ok: res.ok, status: res.status, data: res.ok ? await res.json() : await res.text() };
        } catch (e) {
            console.error(`API error (${path}):`, e);
            return { ok: false, status: 0, data: e.message };
        }
    }

    async function putJson(path, body) {
        try {
            const res = await fetch(`${API_BASE}${path}`, {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(body)
            });
            return { ok: res.ok, status: res.status, data: res.ok ? await res.json() : await res.text() };
        } catch (e) {
            console.error(`API error (${path}):`, e);
            return { ok: false, status: 0, data: e.message };
        }
    }

    async function patchJson(path, body) {
        try {
            const res = await fetch(`${API_BASE}${path}`, {
                method: 'PATCH',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(body)
            });
            return { ok: res.ok, status: res.status, data: res.ok ? await res.json() : await res.text() };
        } catch (e) {
            console.error(`API error (${path}):`, e);
            return { ok: false, status: 0, data: e.message };
        }
    }

    // --- Sessions ---
    async function loadSessions() {
        elSessionsList.innerHTML = '<div class="loading">Loading...</div>';
        const sessions = await fetchJson('/sessions');
        if (!sessions || sessions.length === 0) {
            elSessionsList.innerHTML = '<div class="empty-state">No sessions yet</div>';
            return;
        }
        elSessionsList.innerHTML = '';
        sessions.sort((a, b) => new Date(b.updatedAt) - new Date(a.updatedAt));
        for (const s of sessions) {
            const el = document.createElement('div');
            el.className = 'list-item' + (s.key === currentSessionKey ? ' active' : '');
            el.dataset.key = s.key;
            const channelIcon = channelEmoji(s.channel);
            const timeStr = relativeTime(s.updatedAt);
            el.innerHTML = `
                <span class="item-title">${channelIcon} ${escapeHtml(s.agentName || 'Chat')}</span>
                <span class="item-meta">${escapeHtml(s.channel)} · ${s.messageCount} msgs · ${timeStr}</span>
                <button class="btn-hide-session" title="Hide session">✕</button>
            `;
            
            // Prevent click from bubbling to parent when clicking main area
            el.addEventListener('click', (e) => {
                if (!e.target.classList.contains('btn-hide-session')) {
                    openSession(s.key);
                }
            });
            
            // Hide button click handler
            const hideBtn = el.querySelector('.btn-hide-session');
            hideBtn.addEventListener('click', async (e) => {
                e.stopPropagation();
                await hideSession(s.key);
            });
            
            elSessionsList.appendChild(el);
        }
    }

    async function hideSession(key) {
        const result = await patchJson(`/sessions/${encodeURIComponent(key)}`, { hidden: true });
        if (result.ok) {
            // If we're hiding the currently active session, go back to welcome
            if (key === currentSessionKey) {
                currentSessionKey = null;
                elChatView.classList.add('hidden');
                elWelcome.classList.remove('hidden');
            }
            // Refresh the sessions list
            loadSessions();
        } else {
            console.error('Failed to hide session:', result.data);
        }
    }

    async function openSession(key) {
        currentSessionKey = key;
        const session = await fetchJson(`/sessions/${encodeURIComponent(key)}`);
        if (!session) return;

        // Update sidebar active state
        elSessionsList.querySelectorAll('.list-item').forEach(el => {
            el.classList.toggle('active', el.dataset.key === key);
        });

        // Show chat view
        elWelcome.classList.add('hidden');
        elChatView.classList.remove('hidden');
        const channel = session.channel || (key.includes(':') ? key.split(':')[0] : 'unknown');
        elChatTitle.textContent = `${session.agentName || 'Chat'}`;
        elChatMeta.textContent = `Channel: ${channel} · Agent: ${session.agentName} · ${session.history ? session.history.length : 0} messages`;
        elChatMessages.innerHTML = '';
        elBtnSend.disabled = false;

        // Set agent selector to match session's agent
        if (session.agentName) {
            elAgentSelect.value = session.agentName;
        }
        elAgentSelect.disabled = true;

        // Populate model selector if not already done, then set session's model
        await populateModelSelector();
        if (session.model) {
            elModelSelect.value = session.model;
        }
        elModelSelect.disabled = false; // Allow changing model even for existing sessions

        // Render history
        if (session.history) {
            for (const entry of session.history) {
                renderHistoryEntry(entry);
            }
        }
        scrollToBottom();
    }

    function renderHistoryEntry(entry) {
        if (entry.role === 'tool') {
            renderToolCall(entry);
        } else if (entry.role === 'assistant' && entry.toolCalls) {
            renderAssistantWithTools(entry);
        } else if (entry.content && entry.content.trim()) {
            // Only render non-empty messages
            const div = document.createElement('div');
            div.className = `message ${entry.role}`;
            const time = entry.timestamp ? formatTime(entry.timestamp) : '';
            const contentHtml = entry.role === 'assistant' ? renderMarkdown(entry.content) : escapeHtml(entry.content);
            div.innerHTML = `<div class="msg-header"><span class="msg-role">${escapeHtml(entry.role)}</span><span>${time}</span></div><div class="msg-content">${contentHtml}</div>`;
            elChatMessages.appendChild(div);
        }
    }

    function renderToolCall(entry) {
        const div = document.createElement('div');
        div.className = `message tool ${showTools ? '' : 'hidden'}`;
        
        const toolName = entry.toolName || 'unknown';
        const argsPreview = formatToolArgsPreview(entry);
        
        const summary = document.createElement('div');
        summary.className = 'tool-call-summary';
        summary.innerHTML = `
            <span class="tool-icon">🔧</span>
            <span class="tool-name">${escapeHtml(toolName)}</span>
            <span class="tool-args-preview">${escapeHtml(argsPreview)}</span>
        `;
        summary.addEventListener('click', () => openToolModal(entry));
        
        div.appendChild(summary);
        elChatMessages.appendChild(div);
    }

    function renderAssistantWithTools(entry) {
        const hasContent = entry.content && entry.content.trim();
        const hasToolCalls = entry.toolCalls && entry.toolCalls.length > 0;
        
        // If no content and no tool calls, don't render anything
        if (!hasContent && !hasToolCalls) return;
        
        // If only tool calls and they're hidden, don't render a message bubble
        if (!hasContent && hasToolCalls && !showTools) return;
        
        const div = document.createElement('div');
        div.className = 'message assistant';
        const time = entry.timestamp ? formatTime(entry.timestamp) : '';
        
        let contentHtml = hasContent ? `<div class="msg-content">${renderMarkdown(entry.content)}</div>` : '';
        let toolCallsHtml = '';
        
        if (hasToolCalls) {
            const toolSummaries = entry.toolCalls.map(tc => {
                const argsPreview = formatToolArgsPreview(tc);
                return `<span class="tool-call-summary" data-tool-index="${entry.toolCalls.indexOf(tc)}">
                    <span class="tool-icon">🔧</span>
                    <span class="tool-name">${escapeHtml(tc.toolName || tc.name || 'unknown')}</span>
                    <span class="tool-args-preview">${escapeHtml(argsPreview)}</span>
                </span>`;
            }).join(' ');
            toolCallsHtml = `<div class="${showTools ? '' : 'hidden'}">${toolSummaries}</div>`;
        }
        
        div.innerHTML = `
            <div class="msg-header"><span class="msg-role">${escapeHtml(entry.role)}</span><span>${time}</span></div>
            ${contentHtml}
            ${toolCallsHtml}
        `;
        
        // Attach click handlers after adding to DOM
        if (hasToolCalls) {
            setTimeout(() => {
                div.querySelectorAll('.tool-call-summary').forEach((el, idx) => {
                    el.addEventListener('click', () => openToolModal(entry.toolCalls[idx]));
                });
            }, 0);
        }
        
        elChatMessages.appendChild(div);
    }

    function renderAssistantWithToolsLive(msg) {
        const hasContent = msg.content && msg.content.trim();
        const hasToolCalls = msg.toolCalls && msg.toolCalls.length > 0;
        
        // If no content and no tool calls, don't render anything
        if (!hasContent && !hasToolCalls) return;
        
        // If only tool calls and they're hidden, don't render a message bubble
        if (!hasContent && hasToolCalls && !showTools) return;

        const div = document.createElement('div');
        div.className = 'message assistant';
        const now = formatTime(new Date().toISOString());
        
        let contentHtml = hasContent ? `<div class="msg-content">${renderMarkdown(msg.content)}</div>` : '';
        let toolCallsHtml = '';
        
        if (hasToolCalls) {
            const toolSummaries = msg.toolCalls.map(tc => {
                const argsPreview = formatToolArgsPreview(tc);
                return `<span class="tool-call-summary" data-tool-index="${msg.toolCalls.indexOf(tc)}">
                    <span class="tool-icon">🔧</span>
                    <span class="tool-name">${escapeHtml(tc.toolName || tc.name || 'unknown')}</span>
                    <span class="tool-args-preview">${escapeHtml(argsPreview)}</span>
                </span>`;
            }).join(' ');
            toolCallsHtml = `<div class="${showTools ? '' : 'hidden'}">${toolSummaries}</div>`;
        }
        
        div.innerHTML = `
            <div class="msg-header"><span class="msg-role">ASSISTANT</span><span>${now}</span></div>
            ${contentHtml}
            ${toolCallsHtml}
        `;
        
        // Attach click handlers after adding to DOM
        if (hasToolCalls) {
            setTimeout(() => {
                div.querySelectorAll('.tool-call-summary').forEach((el, idx) => {
                    el.addEventListener('click', () => openToolModal(msg.toolCalls[idx]));
                });
            }, 0);
        }
        
        elChatMessages.appendChild(div);
        scrollToBottom();
    }

    function formatToolArgsPreview(entry) {
        const args = entry.arguments || entry.args || {};
        const pairs = [];
        
        for (const [key, value] of Object.entries(args)) {
            let valStr = '';
            if (typeof value === 'string') {
                valStr = value.length > 40 ? value.substring(0, 40) + '...' : value;
            } else {
                valStr = JSON.stringify(value);
                if (valStr.length > 40) valStr = valStr.substring(0, 40) + '...';
            }
            pairs.push(`${key}: "${valStr}"`);
        }
        
        const preview = pairs.join(', ');
        return preview.length > 80 ? preview.substring(0, 80) + '...' : preview;
    }

    function openToolModal(toolData) {
        const toolName = toolData.toolName || toolData.name || 'unknown';
        const args = toolData.arguments || toolData.args || {};
        const output = toolData.content || toolData.output || toolData.result || '(no output)';
        
        $('#modal-tool-name').textContent = toolName;
        $('#modal-tool-args').textContent = JSON.stringify(args, null, 2);
        $('#modal-tool-output').textContent = output;
        
        elToolModal.classList.remove('hidden');
    }

    function closeToolModal() {
        elToolModal.classList.add('hidden');
    }

    // --- Chat ---
    function sendMessage() {
        const text = elChatInput.value.trim();
        if (!text) return;
        elChatInput.value = '';
        autoResize(elChatInput);

        appendChatMessage('user', text);
        
        // Show thinking indicator immediately after user message
        showThinkingIndicator();

        // If we have a current session, send with session_id override
        const payload = { type: 'message', content: text };
        if (currentSessionKey) payload.session_id = currentSessionKey;
        if (!currentSessionKey && elAgentSelect.value) payload.agent = elAgentSelect.value;
        if (elModelSelect.value) payload.model = elModelSelect.value;
        sendWs(payload);
    }

    function appendChatMessage(role, content) {
        // Skip rendering if content is empty/whitespace
        if (!content || !content.trim()) return;
        
        const div = document.createElement('div');
        div.className = `message ${role}`;
        const now = formatTime(new Date().toISOString());
        const contentHtml = role === 'assistant' ? renderMarkdown(content) : escapeHtml(content);
        div.innerHTML = `<div class="msg-header"><span class="msg-role">${escapeHtml(role)}</span><span>${now}</span></div><div class="msg-content">${contentHtml}</div>`;
        elChatMessages.appendChild(div);
        scrollToBottom();
    }

    function appendDelta(content) {
        let streaming = elChatMessages.querySelector('.message.assistant.streaming');
        if (!streaming) {
            // Remove thinking indicator when streaming starts
            const thinkingElements = elChatMessages.querySelectorAll('.message.thinking');
            thinkingElements.forEach(el => el.remove());
            
            streaming = document.createElement('div');
            streaming.className = 'message assistant streaming';
            streaming.innerHTML = `<div class="msg-header"><span class="msg-role">ASSISTANT</span><span>streaming...</span></div><span class="delta-content"></span>`;
            elChatMessages.appendChild(streaming);
        }
        const deltaEl = streaming.querySelector('.delta-content');
        deltaEl.textContent += content;
        scrollToBottom();
    }

    function appendToolProgress(msg) {
        // Only show if tools toggle is on
        if (!showTools) return;
        
        const tool = msg.tool || 'unknown';
        const action = msg.action || '';
        const args = msg.args || {};
        
        // Build a descriptive message based on tool/action/args
        let description = '';
        if (tool === 'filesystem' && action === 'read' && args.path) {
            description = `Reading ${args.path}`;
        } else if (tool === 'filesystem' && action === 'write' && args.path) {
            description = `Writing ${args.path}`;
        } else if (action && args.path) {
            description = `${action} ${args.path}`;
        } else if (action) {
            description = `${tool}: ${action}`;
        } else {
            description = tool;
        }
        
        // Check if we have an active streaming message
        let streaming = elChatMessages.querySelector('.message.assistant.streaming');
        if (!streaming) {
            // If no streaming message, show tool progress as a separate indicator
            const existing = elChatMessages.querySelector('.tool-progress-indicator');
            if (existing) {
                // Update existing indicator
                existing.textContent = `🔧 ${description}...`;
            } else {
                // Create new indicator
                const indicator = document.createElement('div');
                indicator.className = 'tool-progress-indicator';
                indicator.textContent = `🔧 ${description}...`;
                elChatMessages.appendChild(indicator);
            }
        } else {
            // Tool progress within streaming response
            // Check if there's already a tool progress element in the streaming bubble
            let toolProgress = streaming.querySelector('.tool-progress-inline');
            if (!toolProgress) {
                toolProgress = document.createElement('div');
                toolProgress.className = 'tool-progress-inline';
                streaming.appendChild(toolProgress);
            }
            toolProgress.textContent = `🔧 ${description}...`;
        }
        
        scrollToBottom();
    }

    // --- System Messages ---
    function handleSystemMessage(event) {
        const metadata = event.metadata || {};
        const messageType = metadata.type;

        if (messageType === 'device_auth') {
            showDeviceAuthBanner(event, metadata);
        } else if (messageType === 'auth_success') {
            hideSystemMessageBanner();
            showSuccessNotification('✓ Authentication successful');
        } else if (messageType === 'provider_status') {
            showProviderStatusBanner(event, metadata);
        } else {
            // Generic system message
            showGenericSystemMessage(event);
        }
    }

    function showDeviceAuthBanner(event, metadata) {
        if (!elSystemMessageBanner) return;
        
        const verificationUri = metadata.verification_uri || metadata.verificationUri;
        const userCode = metadata.user_code || metadata.userCode;
        const provider = metadata.provider || 'Service';
        
        elSystemMessageBanner.className = 'system-message-banner device-auth';
        const icon = elSystemMessageBanner.querySelector('.system-message-icon');
        const title = elSystemMessageBanner.querySelector('.system-message-title');
        const body = elSystemMessageBanner.querySelector('.system-message-body');
        
        icon.textContent = '🔐';
        title.textContent = 'Authentication Required';
        
        body.innerHTML = `
            <p>Please authenticate with ${escapeHtml(provider)} to continue.</p>
            ${verificationUri ? `<p><a href="${escapeHtml(verificationUri)}" target="_blank" rel="noopener noreferrer" class="auth-link">${escapeHtml(verificationUri)}</a></p>` : ''}
            ${userCode ? `<div class="auth-code-container">
                <div class="auth-code-label">Enter this code:</div>
                <div class="auth-code" onclick="navigator.clipboard.writeText('${escapeHtml(userCode)}'); this.nextElementSibling.style.display='inline'; setTimeout(() => this.nextElementSibling.style.display='none', 2000);" title="Click to copy">${escapeHtml(userCode)}</div>
                <span class="auth-code-copied" style="display:none;">✓ Copied!</span>
            </div>` : ''}
            <p class="auth-waiting">⏳ Waiting for authentication...</p>
        `;
    }

    function showProviderStatusBanner(event, metadata) {
        if (!elSystemMessageBanner) return;
        
        elSystemMessageBanner.className = 'system-message-banner provider-status';
        const icon = elSystemMessageBanner.querySelector('.system-message-icon');
        const title = elSystemMessageBanner.querySelector('.system-message-title');
        const body = elSystemMessageBanner.querySelector('.system-message-body');
        
        icon.textContent = '📡';
        title.textContent = metadata.title || 'Provider Status';
        body.innerHTML = `<p>${escapeHtml(event.content || '')}</p>`;
    }

    function showGenericSystemMessage(event) {
        if (!elSystemMessageBanner) return;
        
        const metadata = event.metadata || {};
        elSystemMessageBanner.className = 'system-message-banner generic';
        const icon = elSystemMessageBanner.querySelector('.system-message-icon');
        const title = elSystemMessageBanner.querySelector('.system-message-title');
        const body = elSystemMessageBanner.querySelector('.system-message-body');
        
        icon.textContent = 'ℹ️';
        title.textContent = metadata.title || 'System Message';
        body.innerHTML = `<p>${escapeHtml(event.content || '')}</p>`;
    }

    function hideSystemMessageBanner() {
        if (!elSystemMessageBanner) return;
        elSystemMessageBanner.classList.add('hidden');
    }

    function showSuccessNotification(message) {
        // Create a temporary success notification
        const notif = document.createElement('div');
        notif.className = 'success-notification';
        notif.textContent = message;
        document.body.appendChild(notif);
        
        setTimeout(() => notif.classList.add('show'), 10);
        setTimeout(() => {
            notif.classList.remove('show');
            setTimeout(() => notif.remove(), 300);
        }, 3000);
    }

    function startNewChat() {
        currentSessionKey = null;
        elWelcome.classList.add('hidden');
        elChatView.classList.remove('hidden');
        elChatTitle.textContent = 'New Chat';
        const modelText = elModelSelect.value ? ` · Model: ${elModelSelect.value}` : '';
        elChatMeta.textContent = `Agent: ${elAgentSelect.value || 'default'}${modelText} · Session will be created on first message`;
        elChatMessages.innerHTML = '';
        elBtnSend.disabled = false;
        elAgentSelect.disabled = false;
        elModelSelect.disabled = false;

        // Remove active from all sessions
        elSessionsList.querySelectorAll('.list-item').forEach(el => el.classList.remove('active'));

        elChatInput.focus();
    }

    // --- Channels ---
    async function loadChannels() {
        elChannelsList.innerHTML = '<div class="loading">Loading...</div>';
        const channels = await fetchJson('/channels');
        if (!channels || channels.length === 0) {
            elChannelsList.innerHTML = '<div class="empty-state">No channels</div>';
            return;
        }
        elChannelsList.innerHTML = '';
        for (const ch of channels) {
            const el = document.createElement('div');
            el.className = 'list-item';
            const badge = ch.isRunning ? '<span class="channel-badge running">RUNNING</span>' : '<span class="channel-badge stopped">STOPPED</span>';
            el.innerHTML = `
                <span class="item-title">${channelEmoji(ch.name)} ${escapeHtml(ch.displayName)} ${badge}</span>
                <span class="item-meta">Streaming: ${ch.supportsStreaming ? 'Yes' : 'No'}</span>
            `;
            elChannelsList.appendChild(el);
        }
    }

    // --- Agents ---
    async function loadAgents() {
        elAgentsList.innerHTML = '<div class="loading">Loading...</div>';
        const agents = await fetchJson('/agents');
        if (!agents || agents.length === 0) {
            elAgentsList.innerHTML = '<div class="empty-state">No agents configured</div>';
            return;
        }
        elAgentsList.innerHTML = '';
        for (const a of agents) {
            const el = document.createElement('div');
            el.className = 'list-item';
            el.innerHTML = `
                <span class="item-title">${escapeHtml(a.name)}</span>
                <span class="item-meta">Model: ${escapeHtml(a.model)} · Temp: ${a.temperature} · Max tokens: ${a.maxTokens}</span>
            `;
            el.addEventListener('click', () => openEditAgentForm(a.name));
            elAgentsList.appendChild(el);
        }

        // Populate agent selector dropdown
        elAgentSelect.innerHTML = '';
        for (const a of agents) {
            const opt = document.createElement('option');
            opt.value = a.name;
            opt.textContent = a.name;
            elAgentSelect.appendChild(opt);
        }
    }

    function openAddAgentForm() {
        agentFormMode = 'add';
        editingAgentName = null;
        elAgentFormTitle.textContent = 'Add Agent';
        elFormAgentName.disabled = false;
        elAgentForm.reset();
        elFormFeedback.classList.add('hidden');
        
        // Reset checkboxes and disable inputs
        elFormAgentTemperatureEnabled.checked = false;
        elFormAgentTemperature.disabled = true;
        elFormAgentMaxTokensEnabled.checked = false;
        elFormAgentMaxTokens.disabled = true;
        
        elAgentFormModal.classList.remove('hidden');
    }

    async function openEditAgentForm(name) {
        agentFormMode = 'edit';
        editingAgentName = name;
        elAgentFormTitle.textContent = 'Edit Agent';
        elFormAgentName.disabled = true;
        elFormFeedback.classList.add('hidden');
        
        // Load agent config
        const agent = await fetchJson(`/agents/${encodeURIComponent(name)}`);
        if (!agent) {
            showFormFeedback('Failed to load agent config', 'error');
            return;
        }
        
        // Populate form
        elFormAgentName.value = agent.name || '';
        elFormAgentProvider.value = agent.provider || '';
        elFormAgentModel.value = agent.model || '';
        elFormAgentSystemPrompt.value = agent.systemPrompt || '';
        
        // Temperature: if value exists, check and enable; otherwise uncheck and disable
        if (agent.temperature !== null && agent.temperature !== undefined) {
            elFormAgentTemperatureEnabled.checked = true;
            elFormAgentTemperature.disabled = false;
            elFormAgentTemperature.value = agent.temperature;
        } else {
            elFormAgentTemperatureEnabled.checked = false;
            elFormAgentTemperature.disabled = true;
            elFormAgentTemperature.value = '';
        }
        
        // Max Tokens: if value exists, check and enable; otherwise uncheck and disable
        if (agent.maxTokens !== null && agent.maxTokens !== undefined) {
            elFormAgentMaxTokensEnabled.checked = true;
            elFormAgentMaxTokens.disabled = false;
            elFormAgentMaxTokens.value = agent.maxTokens;
        } else {
            elFormAgentMaxTokensEnabled.checked = false;
            elFormAgentMaxTokens.disabled = true;
            elFormAgentMaxTokens.value = '';
        }
        
        elAgentFormModal.classList.remove('hidden');
    }

    function closeAgentForm() {
        elAgentFormModal.classList.add('hidden');
        elAgentForm.reset();
        elFormFeedback.classList.add('hidden');
    }

    async function saveAgent() {
        elFormFeedback.classList.add('hidden');
        
        // Validate required fields
        if (!elFormAgentName.value.trim()) {
            showFormFeedback('Name is required', 'error');
            return;
        }
        if (!elFormAgentProvider.value) {
            showFormFeedback('Provider is required', 'error');
            return;
        }
        if (!elFormAgentModel.value.trim()) {
            showFormFeedback('Model is required', 'error');
            return;
        }
        
        // Build payload
        const payload = {
            name: elFormAgentName.value.trim(),
            provider: elFormAgentProvider.value,
            model: elFormAgentModel.value.trim()
        };
        
        if (elFormAgentSystemPrompt.value.trim()) {
            payload.systemPrompt = elFormAgentSystemPrompt.value.trim();
        }
        
        // Temperature: only include if checkbox is checked, otherwise send null
        if (elFormAgentTemperatureEnabled.checked && elFormAgentTemperature.value) {
            payload.temperature = parseFloat(elFormAgentTemperature.value);
        } else {
            payload.temperature = null;
        }
        
        // Max Tokens: only include if checkbox is checked, otherwise send null
        if (elFormAgentMaxTokensEnabled.checked && elFormAgentMaxTokens.value) {
            payload.maxTokens = parseInt(elFormAgentMaxTokens.value, 10);
        } else {
            payload.maxTokens = null;
        }
        
        // Call API
        let result;
        if (agentFormMode === 'add') {
            result = await postJson('/agents', payload);
        } else {
            result = await putJson(`/agents/${encodeURIComponent(editingAgentName)}`, payload);
        }
        
        if (result.ok) {
            showFormFeedback('Agent saved successfully!', 'success');
            loadAgents(); // Refresh agents list
            setTimeout(() => closeAgentForm(), 1500);
        } else {
            showFormFeedback(`Failed to save agent: ${result.data}`, 'error');
        }
    }

    function showFormFeedback(message, type) {
        elFormFeedback.className = type === 'error' ? 'form-error' : 'form-success';
        elFormFeedback.textContent = message;
        elFormFeedback.classList.remove('hidden');
    }

    // --- Extensions ---
    async function loadExtensions() {
        elExtensionsSummary.innerHTML = '<div class="loading">Loading...</div>';
        const ext = await fetchJson('/extensions');
        if (!ext) {
            elExtensionsSummary.innerHTML = '<div class="empty-state">Unavailable</div>';
            return;
        }
        const healthClass = ext.healthy ? 'healthy' : 'unhealthy';
        const healthLabel = ext.healthy ? 'Healthy' : 'Issues';
        elExtensionsSummary.innerHTML = `
            <div class="ext-stat ${healthClass}">
                <span class="ext-count">${ext.loaded}</span> loaded
            </div>
            <div class="ext-stat ${ext.failed > 0 ? 'unhealthy' : 'healthy'}">
                <span class="ext-count">${ext.failed}</span> failed
            </div>
            <div class="ext-stat">
                <span class="ext-count">${ext.channels}</span> channels
            </div>
            <div class="ext-stat">
                <span class="ext-count">${ext.providers}</span> providers
            </div>
            <div class="ext-stat">
                <span class="ext-count">${ext.tools}</span> tools
            </div>
        `;
    }

    async function loadProviders() {
        elProvidersList.innerHTML = '<div class="loading">Loading...</div>';
        const providers = await fetchJson('/providers');
        if (!providers || providers.length === 0) {
            elProvidersList.innerHTML = '<div class="empty-state">No providers</div>';
            return;
        }
        elProvidersList.innerHTML = '';
        for (const p of providers) {
            const el = document.createElement('div');
            el.className = 'ext-item';
            el.innerHTML = `
                <span class="ext-name">${escapeHtml(p.name)}</span>
                <span class="ext-detail">Model: ${escapeHtml(p.defaultModel || p.model || 'N/A')}</span>
            `;
            elProvidersList.appendChild(el);
        }

        // Populate model selector in chat header
        await populateModelSelector();
        
        // Populate provider dropdown in agent form
        elFormAgentProvider.innerHTML = '<option value="">Select provider...</option>';
        for (const p of providers) {
            const opt = document.createElement('option');
            opt.value = p.name;
            opt.textContent = p.name;
            elFormAgentProvider.appendChild(opt);
        }
        
        // Populate model dropdown in agent form with ALL models
        await populateAgentFormModels();
    }

    async function loadModels() {
        if (modelsData) {
            return modelsData;
        }
        
        try {
            const models = await fetchJson('/models');
            if (!models || models.length === 0) {
                console.warn('No models returned from /api/models');
                return null;
            }
            modelsData = models;
            return models;
        } catch (err) {
            console.error('Failed to load models:', err);
            return null;
        }
    }

    async function populateModelSelector() {
        const models = await loadModels();
        if (!models) {
            return;
        }

        elModelSelect.innerHTML = '';
        
        // Add a default option
        const defaultOpt = document.createElement('option');
        defaultOpt.value = '';
        defaultOpt.textContent = '(default)';
        elModelSelect.appendChild(defaultOpt);

        // Add all models from all providers
        for (const providerData of models) {
            const provider = providerData.provider;
            const providerModels = providerData.models || [];
            
            for (const model of providerModels) {
                const opt = document.createElement('option');
                opt.value = model;
                opt.textContent = model;
                elModelSelect.appendChild(opt);
            }
        }
    }

    async function populateAgentFormModels(selectedProvider = null) {
        const models = await loadModels();
        if (!models) {
            elFormAgentModel.innerHTML = '<option value="">No models available</option>';
            return;
        }

        elFormAgentModel.innerHTML = '<option value="">Select model...</option>';
        
        for (const providerData of models) {
            const provider = providerData.provider;
            const providerModels = providerData.models || [];
            
            // If a provider is selected, only show models for that provider
            if (selectedProvider && provider !== selectedProvider) {
                continue;
            }
            
            for (const model of providerModels) {
                const opt = document.createElement('option');
                opt.value = model;
                opt.textContent = model;
                elFormAgentModel.appendChild(opt);
            }
        }
    }

    async function loadTools() {
        elToolsList.innerHTML = '<div class="loading">Loading...</div>';
        const tools = await fetchJson('/tools');
        if (!tools || tools.length === 0) {
            elToolsList.innerHTML = '<div class="empty-state">No tools</div>';
            return;
        }
        elToolsList.innerHTML = '';
        for (const t of tools) {
            const el = document.createElement('div');
            el.className = 'ext-item';
            const desc = (t.description || '').substring(0, 60);
            el.innerHTML = `
                <span class="ext-name">${escapeHtml(t.name)}</span>
                <span class="ext-detail">${escapeHtml(desc)}${t.description && t.description.length > 60 ? '...' : ''}</span>
            `;
            elToolsList.appendChild(el);
        }
    }

    // --- Activity Feed ---
    function addActivityItem(evt) {
        const el = document.createElement('div');
        let cssClass = 'activity-item';
        if (evt.eventType === 'MessageReceived') cssClass += ' msg-received';
        else if (evt.eventType === 'ResponseSent') cssClass += ' response-sent';
        else if (evt.eventType === 'Error') cssClass += ' error';
        el.className = cssClass;

        const time = formatTime(evt.timestamp);
        const preview = (evt.content || '').substring(0, 80);
        el.innerHTML = `
            <span class="activity-time">${time}</span>
            <span class="activity-channel">[${escapeHtml(evt.channel)}]</span>
            <strong>${escapeHtml(evt.eventType)}</strong>:
            ${escapeHtml(preview)}${evt.content && evt.content.length > 80 ? '...' : ''}
        `;

        elActivityFeed.insertBefore(el, elActivityFeed.firstChild);

        // Trim old items
        while (elActivityFeed.children.length > MAX_ACTIVITY_ITEMS) {
            elActivityFeed.removeChild(elActivityFeed.lastChild);
        }
    }

    // --- Helpers ---
    function escapeHtml(str) {
        if (!str) return '';
        const div = document.createElement('div');
        div.textContent = str;
        return div.innerHTML;
    }

    function renderMarkdown(str) {
        if (!str) return '';
        if (typeof marked === 'undefined' || typeof DOMPurify === 'undefined') {
            return escapeHtml(str);
        }
        marked.setOptions({
            breaks: false,
            gfm: true,
            headerIds: false,
            mangle: false
        });
        const rawHtml = marked.parse(str);
        const sanitized = DOMPurify.sanitize(rawHtml);
        return `<div class="markdown-content">${sanitized}</div>`;
    }

    function formatTime(isoStr) {
        try {
            const d = new Date(isoStr);
            return d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit', second: '2-digit' });
        } catch {
            return '';
        }
    }

    function relativeTime(isoStr) {
        try {
            const d = new Date(isoStr);
            const now = new Date();
            const diff = Math.floor((now - d) / 1000);
            if (diff < 60) return 'just now';
            if (diff < 3600) return `${Math.floor(diff / 60)}m ago`;
            if (diff < 86400) return `${Math.floor(diff / 3600)}h ago`;
            return `${Math.floor(diff / 86400)}d ago`;
        } catch {
            return '';
        }
    }

    function channelEmoji(name) {
        const map = {
            websocket: '🌐',
            telegram: '✈️',
            discord: '🎮',
            slack: '💼',
            unknown: '❓'
        };
        return map[(name || '').toLowerCase()] || '📡';
    }

    function scrollToBottom() {
        requestAnimationFrame(() => {
            elChatMessages.scrollTop = elChatMessages.scrollHeight;
        });
    }

    function autoResize(textarea) {
        textarea.style.height = 'auto';
        textarea.style.height = Math.min(textarea.scrollHeight, 120) + 'px';
    }

    // --- Event handlers ---
    elBtnSend.addEventListener('click', sendMessage);

    elChatInput.addEventListener('keydown', (e) => {
        if (isCommandPaletteVisible()) {
            if (e.key === 'ArrowDown') {
                e.preventDefault();
                navigateCommandPalette(1);
                return;
            }
            if (e.key === 'ArrowUp') {
                e.preventDefault();
                navigateCommandPalette(-1);
                return;
            }
            if (e.key === 'Tab' || (e.key === 'Enter' && !e.shiftKey)) {
                e.preventDefault();
                const active = elCommandPalette.querySelector('.command-item.active .command-name');
                if (active) acceptCommand(active.textContent);
                return;
            }
            if (e.key === 'Escape') {
                e.preventDefault();
                hideCommandPalette();
                return;
            }
        }

        if (e.key === 'Enter' && !e.shiftKey) {
            e.preventDefault();
            sendMessage();
        }
    });

    elChatInput.addEventListener('input', () => {
        autoResize(elChatInput);
        const text = elChatInput.value;
        if (text.startsWith('/') && !text.includes(' ')) {
            showCommandPalette(text);
        } else {
            hideCommandPalette();
        }
    });

    $('#btn-new-chat').addEventListener('click', startNewChat);

    $('#btn-refresh-sessions').addEventListener('click', (e) => {
        e.stopPropagation();
        loadSessions();
    });
    $('#btn-refresh-channels').addEventListener('click', (e) => {
        e.stopPropagation();
        loadChannels();
    });
    $('#btn-refresh-agents').addEventListener('click', (e) => {
        e.stopPropagation();
        loadAgents();
    });
    $('#btn-refresh-extensions').addEventListener('click', (e) => {
        e.stopPropagation();
        loadExtensions();
        loadProviders();
        loadTools();
    });

    elAgentSelect.addEventListener('change', () => {
        if (!currentSessionKey) {
            const modelText = elModelSelect.value ? ` · Model: ${elModelSelect.value}` : '';
            elChatMeta.textContent = `Agent: ${elAgentSelect.value || 'default'}${modelText} · Session will be created on first message`;
        }
    });

    elModelSelect.addEventListener('change', () => {
        if (!currentSessionKey) {
            const modelText = elModelSelect.value ? ` · Model: ${elModelSelect.value}` : '';
            elChatMeta.textContent = `Agent: ${elAgentSelect.value || 'default'}${modelText} · Session will be created on first message`;
        }
    });

    elToggleActivity.addEventListener('change', () => {
        isSubscribed = elToggleActivity.checked;
        if (isSubscribed) {
            sendWs({ type: 'subscribe' });
        }
        // Note: unsubscribe isn't supported yet; the subscription lives for the connection
    });

    elToggleTools.addEventListener('change', () => {
        showTools = elToggleTools.checked;
        // Toggle visibility of all tool messages
        elChatMessages.querySelectorAll('.message.tool').forEach(el => {
            el.classList.toggle('hidden', !showTools);
        });
        // Toggle visibility of tool call summaries in assistant messages
        elChatMessages.querySelectorAll('.message.assistant > div:not(.msg-header)').forEach(el => {
            if (el.querySelector('.tool-call-summary')) {
                el.classList.toggle('hidden', !showTools);
            }
        });
    });

    elToolModalClose.addEventListener('click', closeToolModal);
    elToolModalOverlay.addEventListener('click', closeToolModal);
    
    elAgentFormClose.addEventListener('click', closeAgentForm);
    elAgentFormOverlay.addEventListener('click', closeAgentForm);
    elBtnCancelAgent.addEventListener('click', closeAgentForm);
    elBtnSaveAgent.addEventListener('click', saveAgent);
    elBtnAddAgent.addEventListener('click', openAddAgentForm);
    
    // Provider selection in agent form — filter models by provider
    elFormAgentProvider.addEventListener('change', () => {
        const selectedProvider = elFormAgentProvider.value;
        populateAgentFormModels(selectedProvider || null);
    });
    
    // Checkbox toggles for nullable fields
    elFormAgentTemperatureEnabled.addEventListener('change', () => {
        elFormAgentTemperature.disabled = !elFormAgentTemperatureEnabled.checked;
        if (!elFormAgentTemperatureEnabled.checked) {
            elFormAgentTemperature.value = '';
        }
    });
    elFormAgentMaxTokensEnabled.addEventListener('change', () => {
        elFormAgentMaxTokens.disabled = !elFormAgentMaxTokensEnabled.checked;
        if (!elFormAgentMaxTokensEnabled.checked) {
            elFormAgentMaxTokens.value = '';
        }
    });

    // System message banner close button
    if (elSystemMessageClose) {
        elSystemMessageClose.addEventListener('click', hideSystemMessageBanner);
    }

    // Close modals on Escape key
    document.addEventListener('keydown', (e) => {
        if (e.key === 'Escape') {
            if (!elToolModal.classList.contains('hidden')) {
                closeToolModal();
            } else if (!elAgentFormModal.classList.contains('hidden')) {
                closeAgentForm();
            }
        }
    });

    // Section toggles
    $$('.section-header[data-toggle]').forEach(header => {
        header.addEventListener('click', () => {
            const target = document.getElementById(header.dataset.toggle);
            if (target) target.classList.toggle('hidden');
        });
    });

    // --- Init ---
    function init() {
        connect();
        loadSessions();
        loadChannels();
        loadAgents();
        loadExtensions();
        loadProviders();
        loadTools();
    }

    init();
})();
