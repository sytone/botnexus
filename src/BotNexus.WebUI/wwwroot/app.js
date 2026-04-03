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

    // --- Commands ---
    const COMMANDS = [
        { name: '/help', description: 'Show available commands and their descriptions' },
        { name: '/reset', description: 'Reset the current conversation session' },
        { name: '/status', description: 'Show system status and last heartbeat time' }
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

    // --- WS message handler ---
    function handleWsMessage(msg) {
        switch (msg.type) {
            case 'connected':
                connectionId = msg.connection_id;
                break;
            case 'response':
                appendChatMessage('assistant', msg.content);
                break;
            case 'delta':
                appendDelta(msg.content);
                break;
            case 'activity':
                if (msg.event) addActivityItem(msg.event);
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
            el.innerHTML = `<span class="item-title">${channelIcon} ${escapeHtml(s.agentName || 'Chat')}</span><span class="item-meta">${escapeHtml(s.channel)} · ${s.messageCount} msgs · ${timeStr}</span>`;
            el.addEventListener('click', () => openSession(s.key));
            elSessionsList.appendChild(el);
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

        // Set model selector to match session's model (if available)
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
        const div = document.createElement('div');
        div.className = `message ${entry.role}`;
        const time = entry.timestamp ? formatTime(entry.timestamp) : '';
        let roleLabel = entry.role;
        if (entry.role === 'tool' && entry.toolName) roleLabel = `tool: ${entry.toolName}`;
        div.innerHTML = `<div class="msg-header"><span class="msg-role">${escapeHtml(roleLabel)}</span><span>${time}</span></div>${escapeHtml(entry.content)}`;
        elChatMessages.appendChild(div);
    }

    // --- Chat ---
    function sendMessage() {
        const text = elChatInput.value.trim();
        if (!text) return;
        elChatInput.value = '';
        autoResize(elChatInput);

        appendChatMessage('user', text);

        // If we have a current session, send with session_id override
        const payload = { type: 'message', content: text };
        if (currentSessionKey) payload.session_id = currentSessionKey;
        if (!currentSessionKey && elAgentSelect.value) payload.agent = elAgentSelect.value;
        if (elModelSelect.value) payload.model = elModelSelect.value;
        sendWs(payload);
    }

    function appendChatMessage(role, content) {
        const div = document.createElement('div');
        div.className = `message ${role}`;
        const now = formatTime(new Date().toISOString());
        div.innerHTML = `<div class="msg-header"><span class="msg-role">${escapeHtml(role)}</span><span>${now}</span></div>${escapeHtml(content)}`;
        // Remove any pending delta element
        const pending = elChatMessages.querySelector('.message.assistant.streaming');
        if (pending && role === 'assistant') {
            pending.remove();
        }
        elChatMessages.appendChild(div);
        scrollToBottom();
    }

    function appendDelta(content) {
        let streaming = elChatMessages.querySelector('.message.assistant.streaming');
        if (!streaming) {
            streaming = document.createElement('div');
            streaming.className = 'message assistant streaming';
            streaming.innerHTML = `<div class="msg-header"><span class="msg-role">ASSISTANT</span><span>streaming...</span></div><span class="delta-content"></span>`;
            elChatMessages.appendChild(streaming);
        }
        const deltaEl = streaming.querySelector('.delta-content');
        deltaEl.textContent += content;
        scrollToBottom();
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

        // Extract models for the model selector
        availableModels = providers
            .map(p => p.defaultModel || p.model)
            .filter(m => m && m !== 'N/A');
        populateModelSelector();
    }

    function populateModelSelector() {
        elModelSelect.innerHTML = '';
        
        // Add a default option
        const defaultOpt = document.createElement('option');
        defaultOpt.value = '';
        defaultOpt.textContent = '(default)';
        elModelSelect.appendChild(defaultOpt);

        // Add available models
        for (const model of availableModels) {
            const opt = document.createElement('option');
            opt.value = model;
            opt.textContent = model;
            elModelSelect.appendChild(opt);
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
