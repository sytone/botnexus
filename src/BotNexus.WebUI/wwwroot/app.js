// BotNexus WebUI — connects to the Gateway via SignalR and REST APIs
(function () {
    'use strict';

    // --- Configuration ---
    const API_BASE = '/api';
    const RESPONSE_TIMEOUT_MS = 30000;
    const MAX_ACTIVITY_ITEMS = 100;

    // --- State ---
    /** @type {signalR.HubConnection|null} */
    let connection = null;
    let currentSessionId = null;
    let currentAgentId = null;
    let currentChannelType = null;
    let connectionId = null;
    let responseTimeoutTimer = null;
    let steerIndicatorTimer = null;
    let isStreaming = false;
    let hasReceivedResponse = false;
    let isRestRequestInFlight = false;
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
    /** @type {number} */
    let messageQueueCount = 0;
    /** @type {Array<string>} */
    let pendingQueuedMessages = [];
    /** @type {number} */
    let toolCallDepth = 0;
    /** @type {boolean} */
    let sendModeFollowUp = false;
    /** @type {WebSocket|null} */
    let activityWs = null;
    let activityReconnectTimer = null;
    let userScrolledUp = false;
    let commandPaletteIndex = -1;

    // --- DOM refs ---
    const $ = (sel) => document.querySelector(sel);
    const $$ = (sel) => document.querySelectorAll(sel);

    const elSessionsList = $('#sessions-list');
    const elAgentsList = $('#agents-list');
    const elConnectionStatus = $('#connection-status');
    const elStatusText = elConnectionStatus.querySelector('.status-text');
    const elConnectionBanner = $('#connection-banner');
    const elWelcome = $('#welcome-screen');
    const elChatView = $('#chat-view');
    const elChatTitle = $('#chat-title');
    const elChatMeta = $('#chat-meta');
    const elChatMessages = $('#chat-messages');
    const elChatInput = $('#chat-input');
    const elBtnSend = $('#btn-send');
    const elBtnAbort = $('#btn-abort');
    const elAgentSelect = $('#agent-select');
    const elModelSelect = $('#model-select');
    const elToggleTools = $('#toggle-tools');
    const elToggleThinking = $('#toggle-thinking');
    const elToggleActivity = $('#toggle-activity');
    const elActivityFeed = $('#activity-feed');
    const elSteerIndicator = $('#steer-indicator');
    const elToolModal = $('#tool-modal');
    const elModalClose = elToolModal.querySelector('.modal-close');
    const elModalOverlay = elToolModal.querySelector('.modal-overlay');
    const elAgentFormModal = $('#agent-form-modal');
    const elAgentForm = $('#agent-form');
    const elDebugModal = $('#debug-modal');
    const elAgentConfigView = $('#agent-config-view');
    const elCronView = $('#cron-view');
    const elConfirmDialog = $('#confirm-dialog');
    const elBtnReconnect = $('#btn-reconnect');
    const elConnectionBannerText = $('#connection-banner-text');
    const elSessionIdDisplay = $('#session-id-display');
    const elSessionIdText = $('#session-id-text');
    const elQueueStatus = $('#queue-status');
    const elQueueCount = $('#queue-count');
    const elActivityItems = $('#activity-items');
    const elActivityFilterAgent = $('#activity-filter-agent');
    const elActivityFilterType = $('#activity-filter-type');
    const elScrollBottom = $('#btn-scroll-bottom');
    const elSidebarToggle = $('#btn-sidebar-toggle');
    const elSidebarOverlay = $('#sidebar-overlay');
    const elSidebar = $('#sidebar');
    const elChannelsList = $('#channels-list');
    const elExtensionsList = $('#extensions-list');
    const elBtnSendMode = $('#btn-send-mode');
    const elFollowUpIndicator = $('#followup-indicator');
    const elProcessingStatus = $('#processing-status');
    const elCommandPalette = $('#command-palette');

    // =========================================================================
    // Markdown rendering
    // =========================================================================

    function initMarkdown() {
        if (typeof marked !== 'undefined') {
            marked.setOptions({ breaks: false, gfm: true, headerIds: false, mangle: false });
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

    function scrollToBottom(force) {
        if (force || !userScrolledUp) {
            requestAnimationFrame(() => { elChatMessages.scrollTop = elChatMessages.scrollHeight; });
        }
        updateScrollButton();
    }

    function updateScrollButton() {
        if (!elScrollBottom) return;
        const threshold = 80;
        const atBottom = elChatMessages.scrollHeight - elChatMessages.scrollTop - elChatMessages.clientHeight < threshold;
        userScrolledUp = !atBottom;
        elScrollBottom.classList.toggle('hidden', atBottom);
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
        const labels = { 
            connected: 'Connected', 
            disconnected: 'Disconnected', 
            connecting: 'Connecting...', 
            reconnecting: 'Reconnecting...',
            online: 'Gateway Online'
        };
        elStatusText.textContent = labels[state] || state;
    }

    function showConnectionBanner(text, level = 'warning', showReconnectBtn = false) {
        elConnectionBanner.className = `connection-banner ${level}`;
        elConnectionBannerText.textContent = text;
        elBtnReconnect.classList.toggle('hidden', !showReconnectBtn);
    }

    function hideConnectionBanner() {
        elConnectionBanner.className = 'connection-banner hidden';
        elConnectionBannerText.textContent = '';
        elBtnReconnect.classList.add('hidden');
    }

    function showSteerIndicator() {
        if (steerIndicatorTimer) clearTimeout(steerIndicatorTimer);
        elSteerIndicator.classList.remove('hidden');
        steerIndicatorTimer = setTimeout(() => {
            elSteerIndicator.classList.add('hidden');
            steerIndicatorTimer = null;
        }, 1500);
    }

    function showFollowUpIndicator() {
        elFollowUpIndicator.classList.remove('hidden');
        setTimeout(() => { elFollowUpIndicator.classList.add('hidden'); }, 1500);
    }

    function updateSendButtonState() {
        const hasText = !!elChatInput.value.trim();
        if (isRestRequestInFlight) {
            elBtnSend.disabled = true;
            return;
        }
        if (isStreaming && connection?.state === signalR.HubConnectionState.Connected) {
            elBtnSend.disabled = !hasText;
            if (sendModeFollowUp) {
                elBtnSend.textContent = '📨 Follow-up';
                elBtnSend.classList.remove('btn-steer');
                elBtnSend.classList.add('btn-followup');
                elChatInput.placeholder = 'Queue a follow-up message...';
            } else {
                elBtnSend.textContent = '🧭 Steer';
                elBtnSend.classList.add('btn-steer');
                elBtnSend.classList.remove('btn-followup');
                elChatInput.placeholder = 'Steer the agent... (Enter to send)';
            }
            elBtnSendMode.classList.remove('hidden');
            elBtnSendMode.classList.toggle('followup-mode', sendModeFollowUp);
            return;
        }
        elBtnSend.classList.remove('btn-steer', 'btn-followup');
        elBtnSend.textContent = 'Send';
        elChatInput.placeholder = 'Type a message... (Enter to send, Shift+Enter for newline)';
        elBtnSend.disabled = !hasText || !elChatView || elChatView.classList.contains('hidden');
        elBtnSendMode.classList.add('hidden');
        sendModeFollowUp = false;
    }

    function setSendingState(isSending) {
        isRestRequestInFlight = isSending;
        elBtnSend.classList.toggle('btn-sending', isSending);
        elBtnSend.textContent = isSending ? 'Sending' : 'Send';
        updateSendButtonState();
    }

    function startResponseTimeout() {
        clearResponseTimeout();
        hasReceivedResponse = false;
        responseTimeoutTimer = setTimeout(() => {
            if (!hasReceivedResponse && isStreaming) {
                appendSystemMessage('⏳ Agent is taking longer than expected...', 'warning');
            }
        }, RESPONSE_TIMEOUT_MS);
    }

    function markResponseReceived() {
        hasReceivedResponse = true;
        clearResponseTimeout();
    }

    function clearResponseTimeout() {
        if (responseTimeoutTimer) {
            clearTimeout(responseTimeoutTimer);
            responseTimeoutTimer = null;
        }
    }

    // =========================================================================
    // Processing status bar
    // =========================================================================

    function showProcessingStatus(stage, icon) {
        elProcessingStatus.classList.remove('hidden');
        const label = $('#processing-label');
        if (label) {
            label.textContent = `${icon || '⏳'} ${stage}`;
            label.classList.remove('hidden');
        }
    }

    function updateProcessingToolCount() {
        const label = $('#processing-label');
        if (!label || label.classList.contains('hidden')) return;

        const runningCount = Object.values(activeToolCalls).filter(t => t.status === 'running').length;
        if (runningCount > 0) {
            const currentText = label.textContent;
            const baseText = currentText.replace(/\s*·\s*🔧.*$/, '');
            label.textContent = `${baseText} · 🔧 ${runningCount} tool${runningCount > 1 ? 's' : ''} active`;
        }
    }

    function hideProcessingStatus() {
        elProcessingStatus.classList.add('hidden');
        const label = $('#processing-label');
        if (label) {
            label.textContent = '';
            label.classList.add('hidden');
        }
    }

    // =========================================================================
    // Confirm dialog
    // =========================================================================

    function showConfirm(message, title, onConfirm, confirmLabel) {
        $('#confirm-title').textContent = title || 'Confirm';
        $('#confirm-message').textContent = message;
        $('#btn-confirm-ok').textContent = confirmLabel || 'OK';
        confirmCallback = onConfirm;
        elConfirmDialog.classList.remove('hidden');
    }

    function closeConfirm() {
        elConfirmDialog.classList.add('hidden');
        confirmCallback = null;
    }

    // =========================================================================
    // Gateway health check
    // =========================================================================

    let gatewayHealthy = false;
    let healthCheckInterval = null;

    async function checkGatewayHealth() {
        try {
            const response = await fetch('/health');
            const wasHealthy = gatewayHealthy;
            gatewayHealthy = response.ok;
            
            if (!connection || connection.state !== signalR.HubConnectionState.Connected) {
                setStatus(gatewayHealthy ? 'online' : 'disconnected');
            }
            
            if (wasHealthy !== gatewayHealthy) {
                if (!gatewayHealthy) {
                    showConnectionBanner('⚠️ Gateway offline', 'warning');
                } else if (!currentAgentId) {
                    hideConnectionBanner();
                }
            }
        } catch (e) {
            gatewayHealthy = false;
            if (!connection || connection.state !== signalR.HubConnectionState.Connected) {
                setStatus('disconnected');
            }
        }
    }

    function startHealthCheck() {
        if (healthCheckInterval) return;
        checkGatewayHealth(); // Initial check
        healthCheckInterval = setInterval(checkGatewayHealth, 15000);
    }

    // =========================================================================
    // SignalR Connection
    // =========================================================================

    // Client-side debug log — visible in browser console (F12)
    const DEBUG = true;
    function debugLog(category, ...args) {
        if (DEBUG) console.log(`[BotNexus:${category}]`, ...args);
    }

    // Safe invoke — guards against null/disconnected connection
    async function hubInvoke(method, ...args) {
        if (!connection || connection.state !== signalR.HubConnectionState.Connected) {
            debugLog('hub', `SKIP ${method} — not connected (state: ${connection?.state || 'null'})`);
            return null;
        }
        debugLog('hub', `→ ${method}`, ...args);
        try {
            const result = await connection.invoke(method, ...args);
            debugLog('hub', `← ${method} OK`, result);
            return result;
        } catch (err) {
            debugLog('hub', `← ${method} ERROR`, err.message);
            throw err;
        }
    }

    // Client version — fetched from server build timestamp
    let CLIENT_VERSION = 'loading';
    fetch('/api/version').then(r => r.json()).then(d => {
        CLIENT_VERSION = d.version || 'unknown';
        debugLog('init', `Client version: ${CLIENT_VERSION}`);
        const meta = document.querySelector('meta[name="botnexus-version"]');
        if (meta) meta.content = CLIENT_VERSION;
        scheduleVersionCheck();
    }).catch(() => { CLIENT_VERSION = 'unknown'; });

    function scheduleVersionCheck() {
        setInterval(async () => {
            try {
                const res = await fetch('/api/version');
                if (!res.ok) return;
                const data = await res.json();
                const serverVersion = data.version || 'unknown';
                if (CLIENT_VERSION !== 'loading' && CLIENT_VERSION !== 'unknown' &&
                    serverVersion !== CLIENT_VERSION) {
                    console.log(`Version changed: ${CLIENT_VERSION} → ${serverVersion}. Reloading...`);
                    location.reload();
                }
            } catch { /* server may be restarting */ }
        }, 10000);
    }

    // Post client logs to server for unified debugging
    function serverLog(level, message, data) {
        debugLog(level, message, data);
        fetch('/api/log', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ level, message, data, version: CLIENT_VERSION, timestamp: new Date().toISOString() })
        }).catch(() => {}); // fire-and-forget
    }

    function initSignalR() {
        connection = new signalR.HubConnectionBuilder()
            .withUrl(`/hub/gateway?clientVersion=${CLIENT_VERSION}`)
            .withAutomaticReconnect([0, 1000, 2000, 5000, 10000, 30000])
            .configureLogging(signalR.LogLevel.Warning)
            .build();

        debugLog('init', `SignalR hub builder created (client v${CLIENT_VERSION})`);

        // Server → Client methods
        connection.on('Connected', (data) => {
            connectionId = data.connectionId;
            agentsCache = data.agents || [];
            setStatus('connected');
            hideConnectionBanner();
            debugLog('lifecycle', 'Connected! connectionId:', connectionId);
            // Defer session join to next tick to avoid re-entrant hub calls
            if (currentSessionId && currentAgentId) {
                setTimeout(() => joinSession(currentAgentId, currentSessionId), 0);
            }
        });

        // SessionJoined is no longer sent as a separate callback —
        // JoinSession returns the data directly via invoke result.

        connection.on('SessionReset', (data) => {
            currentSessionId = null;
            updateSessionIdDisplay();
            elChatMessages.innerHTML = '';
            appendSystemMessage('Session reset. System prompt regenerated.');
            loadSessions();
        });

        connection.on('MessageStart', (evt) => {
            // Finalize any previous streaming message before starting new one
            const prevStreaming = elChatMessages.querySelector('.message.assistant.streaming');
            if (prevStreaming) {
                prevStreaming.classList.remove('streaming', 'message-streaming');
                const timeEl = prevStreaming.querySelector('.msg-time');
                if (timeEl) timeEl.textContent = new Date().toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
            }
            
            activeMessageId = evt.messageId;
            isStreaming = true;
            setSendingState(false);
            activeToolCount = 0;
            thinkingBuffer = '';
            toolCallDepth = 0;
            toolStartTimes = {};
            elBtnAbort.classList.remove('hidden');
            showStreamingIndicator();
            showProcessingStatus('Agent is processing...', '⏳');
            startResponseTimeout();
            updateSendButtonState();
            decrementQueue();
        });

        connection.on('ContentDelta', (evt) => {
            removeStreamingIndicator();
            markResponseReceived();
            autoCollapseThinking();
            showProcessingStatus('Writing response...', '✍️');
            // evt is AgentStreamEvent — contentDelta property (camelCase from JSON)
            const text = typeof evt === 'string' ? evt : (evt?.contentDelta || evt?.delta || '');
            if (text) appendDelta(text);
        });

        connection.on('ThinkingDelta', (evt) => {
            showProcessingStatus('Thinking...', '💭');
            const text = evt?.thinkingContent || evt?.delta || '';
            if (text) handleThinkingDelta({ delta: text });
        });

        connection.on('ToolStart', (evt) => {
            showProcessingStatus(`Using tool: ${evt.toolName || 'tool'}`, '🔧');
            handleToolStart(evt);
            trackActivity('tool', currentAgentId, `🔧 ${evt.toolName || 'tool'} started`);
        });

        connection.on('ToolEnd', (evt) => {
            handleToolEnd(evt);
            const remainingTools = Object.values(activeToolCalls).filter(t => t.status === 'running');
            if (remainingTools.length > 0) {
                showProcessingStatus(`Using tool: ${remainingTools[0].toolName}`, '🔧');
            } else {
                showProcessingStatus('Processing...', '⏳');
            }
        });

        connection.on('MessageEnd', (evt) => {
            markResponseReceived();
            trackActivity('response', currentAgentId, 'Response complete');
            hideProcessingStatus();
            finalizeMessage(evt || {});
        });

        connection.on('Error', (evt) => {
            markResponseReceived();
            trackActivity('error', currentAgentId, evt?.message || 'Error');
            hideProcessingStatus();
            handleError({ message: evt?.message || 'Unknown error', code: evt?.code });
        });

        // Connection lifecycle
        connection.onreconnecting(() => {
            setStatus('reconnecting');
            showConnectionBanner('⚠️ Connection lost. Reconnecting...', 'warning');
        });

        connection.onreconnected(() => {
            setStatus('connected');
            hideConnectionBanner();
            debugLog('lifecycle', 'Reconnected');
            if (currentSessionId && currentAgentId) {
                setTimeout(() => joinSession(currentAgentId, currentSessionId), 0);
            }
        });

        connection.onclose(() => {
            debugLog('lifecycle', 'Connection closed');
            setStatus('disconnected');
            connectionId = null;
            showConnectionBanner('❌ Connection closed. Click Reconnect to retry.', 'error', true);
        });

        startConnection();
    }

    async function startConnection() {
        try {
            serverLog('info', 'SignalR connecting', CLIENT_VERSION);
            setStatus('connecting');
            showConnectionBanner('Connecting...', 'warning');
            await connection.start();
            debugLog('lifecycle', 'Connected! State:', connection.state);
        } catch (err) {
            debugLog('lifecycle', 'Connection FAILED:', err.message);
            console.error('SignalR connection error:', err);
            setStatus('disconnected');
            showConnectionBanner('❌ Cannot connect to Gateway. Check that the server is running.', 'error', true);
            setTimeout(startConnection, 5000);
        }
    }

    let joinSessionVersion = 0;

    async function joinSession(agentId, sessionId) {
        if (!connection || connection.state !== signalR.HubConnectionState.Connected) {
            debugLog('session', 'Cannot join — not connected');
            return;
        }

        // Increment version — any in-flight join with older version will be ignored
        const myVersion = ++joinSessionVersion;
        debugLog('session', `Join v${myVersion}: ${agentId} ${sessionId || '(new)'}`);

        // Update agent immediately so SendMessage uses the right agent
        currentAgentId = agentId;

        // Leave previous session group
        if (currentSessionId && currentSessionId !== sessionId) {
            try { await hubInvoke('LeaveSession', currentSessionId); } catch {}
        }

        // If a newer join was requested while we were leaving, abort
        if (myVersion !== joinSessionVersion) {
            debugLog('session', `Join v${myVersion} superseded by v${joinSessionVersion}`);
            return;
        }

        try {
            const result = await hubInvoke('JoinSession', agentId, sessionId || null);

            // If a newer join was requested while we were joining, discard result
            if (myVersion !== joinSessionVersion) {
                debugLog('session', `Join v${myVersion} result discarded (v${joinSessionVersion} active)`);
                return;
            }

            if (result?.sessionId) {
                currentSessionId = result.sessionId;
                currentAgentId = result.agentId || agentId;
                debugLog('session', `Joined v${myVersion}: session=${currentSessionId} agent=${currentAgentId}`);
                updateSessionIdDisplay();
            }
        } catch (err) {
            if (myVersion === joinSessionVersion) {
                debugLog('session', 'Join failed:', err.message);
                appendSystemMessage(`Failed to join session: ${err.message}`, 'error');
            }
        }
    }

    async function reloadCurrentSessionHistory() {
        if (!currentSessionId) return 0;
        const session = await fetchJson(`/sessions/${encodeURIComponent(currentSessionId)}`);
        if (!session || !session.history) return 0;
        elChatMessages.innerHTML = '';
        for (const entry of session.history) renderHistoryEntry(entry);
        const msgCount = session.history.length || session.messageCount || 0;
        elChatMeta.textContent = `Agent: ${currentAgentId || 'unknown'} · ${msgCount} messages`;
        scrollToBottom();
        return msgCount;
    }

    async function checkAgentRunningStatus(agentId, sessionId) {
        try {
            const status = await fetchJson(`/agents/${encodeURIComponent(agentId)}/sessions/${encodeURIComponent(sessionId)}/status`);
            if (status && (status.status === 'Running' || status.status === 'Idle')) {
                isStreaming = true;
                showStreamingIndicator();
                showProcessingStatus('Agent is processing...', '⏳');
                setSendingState(false);
                updateSendButtonState();
            }
        } catch (e) {
            // Status endpoint may 404 if no instance — that's fine
        }
    }

    // =========================================================================
    // Thinking display
    // =========================================================================

    function formatCharCount(len) {
        if (len < 1000) return `${len} chars`;
        return `${(len / 1000).toFixed(1)}k chars`;
    }

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
                    <span class="thinking-stats"></span>
                    <span class="thinking-chevron" aria-hidden="true">${showThinking ? '▾' : '▸'}</span>
                </div>
                <div class="thinking-content"><pre class="thinking-pre"></pre></div>
            `;
            elChatMessages.appendChild(thinkingEl);
        }

        thinkingEl.querySelector('.thinking-pre').textContent = thinkingBuffer;
        thinkingEl.querySelector('.thinking-stats').textContent = formatCharCount(thinkingBuffer.length);
        scrollToBottom();
    }

    function finalizeThinkingBlock() {
        const thinkingEl = elChatMessages.querySelector('.thinking-block');
        if (thinkingEl) {
            const charCount = thinkingBuffer.length > 0 ? ` (${formatCharCount(thinkingBuffer.length)})` : '';
            thinkingEl.querySelector('.thinking-label').textContent = `Thought process${charCount}`;
            thinkingEl.querySelector('.thinking-stats').textContent = '';
            thinkingEl.classList.add('complete');
            thinkingEl.classList.add('collapsed');
            const toggle = thinkingEl.querySelector('.thinking-toggle');
            if (toggle) {
                toggle.setAttribute('aria-expanded', 'false');
                thinkingEl.querySelector('.thinking-chevron').textContent = '▸';
            }
        }
    }

    function autoCollapseThinking() {
        const thinkingEl = elChatMessages.querySelector('.thinking-block:not(.complete)');
        if (thinkingEl && !thinkingEl.classList.contains('collapsed')) {
            thinkingEl.classList.add('collapsed');
            const toggle = thinkingEl.querySelector('.thinking-toggle');
            if (toggle) {
                toggle.setAttribute('aria-expanded', 'false');
                thinkingEl.querySelector('.thinking-chevron').textContent = '▸';
            }
        }
    }

    // =========================================================================
    // Session ID display
    // =========================================================================

    function updateSessionIdDisplay() {
        if (currentSessionId) {
            const truncated = currentSessionId.length > 12
                ? currentSessionId.substring(0, 12) + '...'
                : currentSessionId;
            elSessionIdText.textContent = `Session: ${truncated}`;
            elSessionIdText.title = currentSessionId;
            elSessionIdDisplay.classList.remove('hidden');
        } else {
            elSessionIdDisplay.classList.add('hidden');
        }
    }

    function copySessionId() {
        if (!currentSessionId) return;
        navigator.clipboard.writeText(currentSessionId).then(() => {
            const btn = $('#btn-copy-session-id');
            btn.textContent = '✅';
            btn.classList.add('copy-flash');
            setTimeout(() => { btn.textContent = '📋'; btn.classList.remove('copy-flash'); }, 1200);
        }).catch(() => {
            // Fallback for older browsers
            const ta = document.createElement('textarea');
            ta.value = currentSessionId;
            ta.style.position = 'fixed';
            ta.style.opacity = '0';
            document.body.appendChild(ta);
            ta.select();
            document.execCommand('copy');
            document.body.removeChild(ta);
        });
    }

    // =========================================================================
    // Queue management
    // =========================================================================

    function incrementQueue() {
        messageQueueCount++;
        updateQueueDisplay();
    }

    function decrementQueue() {
        if (messageQueueCount > 0) messageQueueCount--;
        if (pendingQueuedMessages.length > 0) {
            const queuedText = pendingQueuedMessages.shift();
            appendChatMessage('user', queuedText);
        }
        updateQueueDisplay();
    }

    function resetQueue() {
        messageQueueCount = 0;
        pendingQueuedMessages = [];
        updateQueueDisplay();
    }

    function updateQueueDisplay() {
        if (messageQueueCount > 0) {
            elQueueStatus.classList.remove('hidden');
            elQueueCount.textContent = `${messageQueueCount} message${messageQueueCount > 1 ? 's' : ''} queued`;
        } else {
            elQueueStatus.classList.add('hidden');
        }
    }

    // =========================================================================
    // Reconnect action
    // =========================================================================

    function manualReconnect() {
        hideConnectionBanner();
        if (connection) {
            connection.stop().then(() => startConnection()).catch(() => startConnection());
        } else {
            initSignalR();
        }
    }

    // =========================================================================
    // Tool call handling
    // =========================================================================

    /** @type {Object<string, number>} */
    let toolStartTimes = {};
    let toolElapsedTimer = null;

    function handleToolStart(msg) {
        const callId = msg.toolCallId || `tc-${Date.now()}`;
        activeToolCount++;
        toolStartTimes[callId] = Date.now();
        const depth = msg.depth || toolCallDepth;
        activeToolCalls[callId] = {
            toolName: msg.toolName || 'unknown',
            args: msg.toolArgs || '',
            result: '',
            status: 'running',
            depth: depth
        };
        appendToolCall(callId, msg.toolName, 'running', msg.toolArgs, depth);
        startToolElapsedTimer();
    }

    function handleToolEnd(msg) {
        const callId = msg.toolCallId || 'unknown';
        const isError = msg.toolIsError === true;
        const status = isError ? 'error' : 'complete';
        if (activeToolCalls[callId]) {
            activeToolCalls[callId].result = msg.toolResult || '';
            activeToolCalls[callId].status = status;
        }
        const elapsed = toolStartTimes[callId] ? Math.round((Date.now() - toolStartTimes[callId]) / 1000) : 0;
        delete toolStartTimes[callId];
        updateToolCallStatus(callId, status, elapsed, msg.toolResult);
        if (isError) {
            trackActivity('error', currentAgentId, `🔧 ${msg.toolName || activeToolCalls[callId]?.toolName || 'tool'} failed`);
        }

        // Show a notification when a skill is loaded
        const toolName = msg.toolName || activeToolCalls[callId]?.toolName || '';
        if (toolName === 'skills' && !isError) {
            const result = msg.toolResult || '';
            const skillMatch = result.match(/^## Skill:\s*(.+)$/m);
            if (skillMatch) {
                appendSystemMessage(`📚 Skill loaded: ${skillMatch[1].trim()}`);
            }
        }

        if (Object.keys(toolStartTimes).length === 0) stopToolElapsedTimer();
    }

    function appendToolCall(callId, toolName, status, toolArgs, depth) {
        const depthClass = depth > 0 ? ` tool-call-depth-${Math.min(depth, 3)}` : '';
        const div = document.createElement('div');
        div.className = `message tool-call tool-${status}${showTools ? '' : ' hidden'}${depthClass}`;
        div.dataset.callId = callId;
        div.dataset.toolName = toolName;
        div.setAttribute('role', 'status');

        const argsStr = typeof toolArgs === 'string' ? toolArgs : JSON.stringify(toolArgs || {}, null, 2);
        const argsPreview = formatToolArgsPreview({ args: toolArgs });

        div.innerHTML = `
            <span class="tool-icon" aria-hidden="true">🔧</span>
            <span class="tool-name">${escapeHtml(toolName)}</span>
            <span class="tool-args-preview">${escapeHtml(argsPreview)}</span>
            <span class="tool-elapsed" aria-live="polite"></span>
            <span class="tool-status-badge ${status}">${status === 'running' ? '⏳ Running' : '✓ Done'}</span>
            <div class="tool-call-inspector">
                <div class="tool-inspector-section">
                    <div class="tool-inspector-label">Arguments</div>
                    <pre class="tool-inspector-code">${escapeHtml(argsStr !== '{}' ? argsStr : '(none)')}</pre>
                </div>
                <div class="tool-inspector-section tool-result-section">
                    <div class="tool-inspector-label">Result</div>
                    <pre class="tool-inspector-code tool-result-code">⏳ Running...</pre>
                </div>
            </div>
        `;
        elChatMessages.appendChild(div);
        scrollToBottom();
    }

    function updateToolCallStatus(callId, status, elapsed, result) {
        const el = elChatMessages.querySelector(`.tool-call[data-call-id="${callId}"]`);
        if (!el) return;
        el.classList.remove('tool-running');
        el.classList.add(`tool-${status}`);
        const badge = el.querySelector('.tool-status-badge');
        if (badge) {
            const elapsedStr = elapsed > 0 ? ` ${elapsed}s` : '';
            badge.className = `tool-status-badge ${status}`;
            badge.textContent = status === 'error' ? `❌ Error${elapsedStr}` : `✅ Done${elapsedStr}`;
        }
        const elapsedEl = el.querySelector('.tool-elapsed');
        if (elapsedEl) elapsedEl.textContent = '';

        // Update inline inspector result
        const resultCode = el.querySelector('.tool-result-code');
        if (resultCode && result !== undefined) {
            const resultStr = typeof result === 'string' ? result : JSON.stringify(result, null, 2);
            resultCode.textContent = resultStr || '(no result)';
        } else if (resultCode) {
            resultCode.textContent = status === 'error' ? '(error)' : '(no result)';
        }
        el.style.cursor = 'pointer';
    }

    function startToolElapsedTimer() {
        if (toolElapsedTimer) return;
        toolElapsedTimer = setInterval(() => {
            for (const [callId, startTime] of Object.entries(toolStartTimes)) {
                const el = elChatMessages.querySelector(`.tool-call[data-call-id="${callId}"] .tool-elapsed`);
                if (el) el.textContent = `${Math.round((Date.now() - startTime) / 1000)}s`;
            }
        }, 1000);
    }

    function stopToolElapsedTimer() {
        if (toolElapsedTimer) { clearInterval(toolElapsedTimer); toolElapsedTimer = null; }
    }

    // =========================================================================
    // Streaming / message indicators
    // =========================================================================

    function showStreamingIndicator() {
        removeStreamingIndicator();
        const div = document.createElement('div');
        div.className = 'message thinking typing-indicator streaming-indicator';
        div.setAttribute('aria-label', 'Agent is thinking');
        div.innerHTML = 'Agent is thinking<span class="dots" aria-hidden="true">...</span>';
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
        clearResponseTimeout();
        elBtnAbort.classList.add('hidden');
        removeStreamingIndicator();
        hideProcessingStatus();
        finalizeThinkingBlock();

        const streaming = elChatMessages.querySelector('.message.assistant.streaming');
        if (streaming) {
            streaming.classList.remove('streaming');
            streaming.classList.remove('message-streaming');
            const deltaEl = streaming.querySelector('.delta-content');
            if (deltaEl) {
                const rawText = deltaEl.textContent;
                streaming.dataset.rawContent = rawText;
                deltaEl.innerHTML = renderMarkdown(rawText);
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
        toolCallDepth = 0;
        toolStartTimes = {};
        stopToolElapsedTimer();
        thinkingBuffer = '';
        resetQueue();
        setSendingState(false);
        updateSendButtonState();
        updateSessionIdDisplay();
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
        clearResponseTimeout();
        stopToolElapsedTimer();
        elBtnAbort.classList.add('hidden');
        removeStreamingIndicator();
        hideProcessingStatus();
        appendErrorMessage(`❌ ${msg.message || 'Unknown error'}${msg.code ? ` (${msg.code})` : ''}`);
        setSendingState(false);
        updateSendButtonState();
    }

    // =========================================================================
    // Chat message rendering
    // =========================================================================

    function stripControlTags(text) {
        if (!text) return text;
        return text.replace(/\[\[\s*reply_to_current\s*\]\]/gi, '')
                   .replace(/\[\[\s*reply_to:\s*\w+\s*\]\]/gi, '');
    }

    function appendChatMessage(role, content) {
        appendChatMessageTo(role, content, elChatMessages);
    }

    function appendChatMessageTo(role, content, container) {
        content = stripControlTags(content);
        if (!content || !content.trim()) return;
        const div = document.createElement('div');
        div.className = `message ${role}`;
        const now = formatTime(new Date().toISOString());
        const contentHtml = role === 'assistant' ? renderMarkdown(content) : escapeHtml(content);
        div.innerHTML = `
            <div class="msg-header">
                <span class="msg-role">${escapeHtml(role.toUpperCase())}</span>
                <span class="msg-time">${now}</span>
                <button class="btn-copy-msg" title="Copy message" aria-label="Copy message">📋</button>
            </div>
            <div class="msg-content">${contentHtml}</div>
        `;
        div.dataset.rawContent = content;
        container.appendChild(div);
        if (container === elChatMessages) scrollToBottom();
    }

    function appendSystemMessage(text, level) {
        const div = document.createElement('div');
        div.className = `message system-msg${level ? ' ' + level : ''}`;
        div.textContent = text;
        elChatMessages.appendChild(div);
        scrollToBottom();
    }

    function appendErrorMessage(text) {
        const div = document.createElement('div');
        div.className = 'message assistant message-error';
        const now = formatTime(new Date().toISOString());
        div.innerHTML = `
            <div class="msg-header">
                <span class="msg-role">AGENT ERROR</span>
                <span class="msg-time">${now}</span>
            </div>
            <div class="msg-content">${escapeHtml(text)}</div>
        `;
        elChatMessages.appendChild(div);
        scrollToBottom();
    }

    function appendDelta(content) {
        if (!content) return;
        content = stripControlTags(content);
        if (!content) return;
        
        let streaming = elChatMessages.querySelector('.message.assistant.streaming');
        if (!streaming) {
            streaming = document.createElement('div');
            streaming.className = 'message assistant streaming message-streaming';
            streaming.innerHTML = `
                <div class="msg-header">
                    <span class="msg-role">ASSISTANT</span>
                    <span class="msg-time">streaming...</span>
                    <button class="btn-copy-msg" title="Copy message" aria-label="Copy message">📋</button>
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
        renderHistoryEntryTo(entry, elChatMessages);
    }

    function renderHistoryEntryTo(entry, container) {
        if (!entry) return;
        if ((entry.role === 'user' || entry.role === 'assistant') && entry.content && entry.content.trim()) {
            appendChatMessageTo(entry.role, entry.content, container);
        }
        if (entry.role === 'assistant' && entry.toolCalls && entry.toolCalls.length > 0) {
            for (const tc of entry.toolCalls) renderToolCallHistoryTo(tc, container);
        }
        if (entry.role === 'tool') renderToolCallHistoryTo(entry, container);
    }

    function renderToolCallHistory(tc) {
        renderToolCallHistoryTo(tc, elChatMessages);
    }

    function renderToolCallHistoryTo(tc, container) {
        const callId = `hist-${Date.now()}-${Math.random().toString(36).slice(2, 7)}`;
        const div = document.createElement('div');
        div.className = `message tool-call tool-complete${showTools ? '' : ' hidden'}`;
        div.dataset.callId = callId;
        const toolName = tc.toolName || tc.name || 'unknown';
        const argsPreview = formatToolArgsPreview(tc);
        const argsStr = JSON.stringify(tc.arguments || tc.args || {}, null, 2);
        const resultStr = tc.content || tc.output || tc.result || '(no result)';
        div.innerHTML = `
            <span class="tool-icon" aria-hidden="true">🔧</span>
            <span class="tool-name">${escapeHtml(toolName)}</span>
            <span class="tool-args-preview">${escapeHtml(argsPreview)}</span>
            <span class="tool-status-badge complete">✅ Done</span>
            <div class="tool-call-inspector">
                <div class="tool-inspector-section">
                    <div class="tool-inspector-label">Arguments</div>
                    <pre class="tool-inspector-code">${escapeHtml(argsStr !== '{}' ? argsStr : '(none)')}</pre>
                </div>
                <div class="tool-inspector-section">
                    <div class="tool-inspector-label">Result</div>
                    <pre class="tool-inspector-code">${escapeHtml(resultStr)}</pre>
                </div>
            </div>
        `;
        div.style.cursor = 'pointer';
        activeToolCalls[callId] = {
            toolName,
            args: argsStr,
            result: resultStr,
            status: 'complete'
        };
        container.appendChild(div);
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
    // Command Palette
    // =========================================================================

    const COMMANDS = [
        { name: '/help', description: 'Show available commands' },
        { name: '/new', description: 'Start a new chat session' },
        { name: '/reset', description: 'Clear chat and reset current session' },
        { name: '/status', description: 'Show gateway health status' },
        { name: '/agents', description: 'List available agents' },
    ];

    function showCommandPalette(filter) {
        const query = filter.toLowerCase();
        const matches = COMMANDS.filter(c => c.name.startsWith(query));
        if (matches.length === 0) { hideCommandPalette(); return; }
        commandPaletteIndex = 0;
        elCommandPalette.innerHTML = '';
        for (let i = 0; i < matches.length; i++) {
            const el = document.createElement('div');
            el.className = 'command-item' + (i === 0 ? ' active' : '');
            el.setAttribute('role', 'option');
            el.dataset.index = i;
            el.innerHTML = `<span class="command-name">${escapeHtml(matches[i].name)}</span><span class="command-desc">${escapeHtml(matches[i].description)}</span>`;
            el.addEventListener('click', () => executeCommand(matches[i].name));
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

    function isCommandPaletteVisible() {
        return !elCommandPalette.classList.contains('hidden');
    }

    function navigateCommandPalette(direction) {
        const items = elCommandPalette.querySelectorAll('.command-item');
        if (items.length === 0) return;
        items[commandPaletteIndex]?.classList.remove('active');
        commandPaletteIndex = (commandPaletteIndex + direction + items.length) % items.length;
        items[commandPaletteIndex].classList.add('active');
        items[commandPaletteIndex].scrollIntoView({ block: 'nearest' });
    }

    function acceptCommandPalette() {
        const active = elCommandPalette.querySelector('.command-item.active .command-name');
        if (active) executeCommand(active.textContent);
    }

    function executeCommand(name) {
        hideCommandPalette();
        elChatInput.value = '';
        autoResize(elChatInput);
        updateSendButtonState();

        switch (name) {
            case '/help': executeHelp(); break;
            case '/new': executeReset('new'); break;
            case '/reset': executeReset(); break;
            case '/status': executeStatus(); break;
            case '/agents': executeAgents(); break;
            default: appendSystemMessage(`Unknown command: ${name}`); break;
        }
    }

    function executeHelp() {
        const lines = COMMANDS.map(c => `  ${c.name.padEnd(12)} ${c.description}`).join('\n');
        appendCommandResult('📖 Available Commands', lines);
        appendSystemMessage('Tip: Type / in the input box or press Ctrl+K to open the command palette.');
    }

    async function executeReset(commandType = 'reset') {
        // Reset streaming state but preserve the timeline
        activeMessageId = null;
        activeToolCalls = {};
        activeToolCount = 0;
        toolCallDepth = 0;
        thinkingBuffer = '';
        clearResponseTimeout();
        resetQueue();
        removeStreamingIndicator();
        hideProcessingStatus();
        isStreaming = false;
        setSendingState(false);

        if (commandType === 'reset' && currentAgentId && currentSessionId && connection?.state === signalR.HubConnectionState.Connected) {
            try {
                await hubInvoke('ResetSession', currentAgentId, currentSessionId);
            } catch (err) {
                console.warn('Failed to reset session via SignalR:', err);
            }
            appendSystemMessage('Session context reset. System prompt regenerated.');
        } else {
            // /new — add a divider for the new session
            const divider = document.createElement('div');
            divider.className = 'session-divider';
            const now = new Date();
            const dateStr = now.toLocaleDateString(undefined, { month: 'short', day: 'numeric', year: 'numeric' });
            const timeStr = now.toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' });
            divider.innerHTML = `<span class="session-divider-line"></span><span class="session-divider-label">New session started ${dateStr} at ${timeStr}</span><span class="session-divider-line"></span>`;
            elChatMessages.appendChild(divider);
            scrollToBottom();
            appendSystemMessage('New session started. Previous messages are still visible above.');
        }

        currentSessionId = null;
        updateSessionIdDisplay();
        currentAgentId = elAgentSelect.value || currentAgentId;
        loadSessions();
    }

    function clearChatForSessionReset() {
        activeMessageId = null;
        activeToolCalls = {};
        activeToolCount = 0;
        toolCallDepth = 0;
        thinkingBuffer = '';
        clearResponseTimeout();
        resetQueue();
        removeStreamingIndicator();
        hideProcessingStatus();
        isStreaming = false;
        setSendingState(false);
        elChatMessages.innerHTML = '';
        elChatTitle.textContent = `${elAgentSelect.value || 'New Chat'} — Web Chat`;        elChatMeta.textContent = `Agent: ${elAgentSelect.value || 'default'} · Session will be created on first message`;
        elSessionIdDisplay.classList.add('hidden');
        elAgentSelect.classList.remove('hidden');
    }

    async function executeStatus() {
        appendSystemMessage('⏳ Checking gateway status...');
        try {
            const res = await fetch('/health');
            if (res.ok) {
                const data = await res.json().catch(() => null);
                if (data) {
                    const lines = Object.entries(data).map(([k, v]) =>
                        `  ${k}: ${typeof v === 'object' ? JSON.stringify(v) : v}`
                    ).join('\n');
                    appendCommandResult('✅ Gateway Status', lines);
                } else {
                    appendCommandResult('✅ Gateway Status', `  HTTP ${res.status} — healthy`);
                }
            } else {
                appendCommandResult('⚠️ Gateway Status', `  HTTP ${res.status} — ${res.statusText}`);
            }
        } catch (e) {
            appendCommandResult('❌ Gateway Unreachable', `  ${e.message}`);
        }
    }

    async function executeAgents() {
        appendSystemMessage('⏳ Fetching agents...');
        const agents = await fetchJson('/agents');
        if (!agents || agents.length === 0) {
            appendCommandResult('🧠 Agents', '  No agents configured.');
            return;
        }
        const lines = agents.map(a => {
            const name = a.name || a.id || 'unnamed';
            const provider = a.provider || '';
            const model = a.model || '';
            const parts = [`  ${name}`];
            if (provider) parts.push(`provider: ${provider}`);
            if (model) parts.push(`model: ${model}`);
            return parts.join(' — ');
        }).join('\n');
        appendCommandResult(`🧠 Agents (${agents.length})`, lines);
    }

    function appendCommandResult(title, body) {
        const div = document.createElement('div');
        div.className = 'message system-msg command-result';
        div.innerHTML = `<div class="command-result-title">${escapeHtml(title)}</div><pre>${escapeHtml(body)}</pre>`;
        elChatMessages.appendChild(div);
        scrollToBottom();
    }

    // =========================================================================
    // Chat actions
    // =========================================================================

    async function sendMessage() {
        const text = elChatInput.value.trim();
        if (!text || !currentAgentId) return;

        // Intercept slash commands
        if (text.startsWith('/')) {
            const cmd = text.split(/\s/)[0].toLowerCase();
            const match = COMMANDS.find(c => c.name === cmd);
            if (match) {
                elChatInput.value = '';
                autoResize(elChatInput);
                updateSendButtonState();
                hideCommandPalette();
                executeCommand(match.name);
                return;
            }
        }

        elChatInput.value = '';
        autoResize(elChatInput);
        updateSendButtonState();

        if (!currentSessionId) {
            // First message — join will create the session
            await joinSession(currentAgentId, null);
        }

        if (isStreaming && connection?.state === signalR.HubConnectionState.Connected) {
            if (sendModeFollowUp) {
                pendingQueuedMessages.push(text);
                incrementQueue();
                showFollowUpIndicator();
                trackActivity('message', currentAgentId, `Follow-up: ${text.substring(0, 60)}`);
                try {
                    await hubInvoke('FollowUp', currentAgentId, currentSessionId, text);
                } catch (err) {
                    appendSystemMessage(`Failed to queue: ${err.message}`, 'error');
                }
            } else {
                appendSystemMessage(`🧭 Steering: ${text}`);
                showSteerIndicator();
                trackActivity('message', currentAgentId, `Steer: ${text.substring(0, 60)}`);
                try {
                    await hubInvoke('Steer', currentAgentId, currentSessionId, text);
                } catch (err) {
                    appendSystemMessage(`Failed to steer: ${err.message}`, 'error');
                }
            }
            return;
        }

        appendChatMessage('user', text);
        trackActivity('message', currentAgentId, text.substring(0, 60));
        setSendingState(true);
        isStreaming = true;
        incrementQueue();
        startResponseTimeout();

        try {
            await hubInvoke('SendMessage', currentAgentId, currentSessionId, text);
        } catch (err) {
            appendSystemMessage(`Error: ${err.message}`, 'error');
            isStreaming = false;
            setSendingState(false);
        }
    }

    async function abortRequest() {
        if (currentAgentId && currentSessionId && connection?.state === signalR.HubConnectionState.Connected) {
            try { await hubInvoke('Abort', currentAgentId, currentSessionId); } catch {}
        }
        isStreaming = false;
        clearResponseTimeout();
        stopToolElapsedTimer();
        elBtnAbort.classList.add('hidden');
        removeStreamingIndicator();
        hideProcessingStatus();
        setSendingState(false);
        resetQueue();
        updateSendButtonState();
        appendSystemMessage('Request aborted.');
    }

    function startNewChat() {
        const agentId = elAgentSelect.value || null;
        if (!agentId) return;

        currentSessionId = null;
        isStreaming = false;
        resetQueue();
        currentAgentId = agentId;
        currentChannelType = 'Web Chat';

        // Open the timeline for this agent — shows all past sessions
        openAgentTimeline(agentId, 'Web Chat');
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

    let sessionsFingerprint = '';
    let sessionsInitialLoad = true;

    async function loadSessions() {
        if (sessionsInitialLoad) {
            elSessionsList.innerHTML = '<div class="loading">Loading...</div>';
        }

        // Fetch agents and sessions in parallel
        const [agents, sessions] = await Promise.all([
            fetchJson('/agents'),
            fetchJson('/sessions')
        ]);

        if (!agents || agents.length === 0) {
            elSessionsList.innerHTML = '<div class="empty-state">No agents configured</div>';
            sessionsFingerprint = '';
            sessionsInitialLoad = false;
            return;
        }

        // Group sessions by agentId
        const sessionsByAgent = {};
        if (sessions) {
            for (const s of sessions) {
                const agentId = s.agentId || s.agentName || 'unknown';
                if (!sessionsByAgent[agentId]) sessionsByAgent[agentId] = [];
                sessionsByAgent[agentId].push(s);
            }
        }

        // Build a fingerprint to detect actual changes
        const newFingerprint = JSON.stringify({
            agents: agents.map(a => a.agentId || a.name),
            sessions: (sessions || []).map(s => s.sessionId + ':' + (s.updatedAt || '')),
            active: currentSessionId
        });

        if (newFingerprint === sessionsFingerprint) return; // No changes
        sessionsFingerprint = newFingerprint;

        // Preserve collapsed state
        const collapsedAgents = new Set();
        elSessionsList.querySelectorAll('.agent-group-header.collapsed').forEach(el => {
            const text = el.textContent.replace('▼', '').trim();
            collapsedAgents.add(text);
        });

        elSessionsList.innerHTML = '';

        // Build agent groups
        for (const agent of agents) {
            const agentId = agent.agentId || agent.name;
            const displayName = agent.displayName || agentId;

            const group = document.createElement('div');
            group.className = 'agent-group';

            // Group header (collapsible)
            const header = document.createElement('div');
            header.className = 'agent-group-header' + (collapsedAgents.has(displayName) ? ' collapsed' : '');
            header.innerHTML = `<span class="collapse-icon">▼</span> ${escapeHtml(displayName)}`;
            header.addEventListener('click', () => header.classList.toggle('collapsed'));
            group.appendChild(header);

            // Channel entries
            const channelsDiv = document.createElement('div');
            channelsDiv.className = 'agent-group-channels';

            // Show one entry per channel so cron and interactive sessions are both visible
            const agentSessions = (sessionsByAgent[agentId] || []).sort((a, b) =>
                new Date(b.updatedAt || b.createdAt || 0) - new Date(a.updatedAt || a.createdAt || 0)
            );
            const latestByChannel = new Map();
            for (const s of agentSessions) {
                const key = (s.channelType || 'signalr').toLowerCase();
                if (!latestByChannel.has(key)) latestByChannel.set(key, s);
            }

            if (latestByChannel.size === 0) {
                const emptyChannelEl = document.createElement('div');
                emptyChannelEl.className = 'list-item';
                emptyChannelEl.dataset.agentId = agentId;
                emptyChannelEl.dataset.channelType = 'web chat';
                emptyChannelEl.innerHTML = `
                    <div class="list-item-row">
                        <span class="item-title">💬 Web Chat</span>
                    </div>
                    <span class="item-meta">No sessions</span>
                `;
                emptyChannelEl.addEventListener('click', () => {
                    elAgentSelect.value = agentId;
                    currentAgentId = agentId;
                    startNewChat();
                });
                channelsDiv.appendChild(emptyChannelEl);
            } else {
                for (const latestSession of latestByChannel.values()) {
                    const channelType = latestSession.channelType || 'signalr';
                    const displayName = channelDisplayName(channelType);
                    const isActive = currentAgentId === agentId &&
                        currentChannelType && currentChannelType.toLowerCase() === channelType.toLowerCase();

                    const channelEl = document.createElement('div');
                    channelEl.className = 'list-item' + (isActive ? ' active' : '');
                    channelEl.dataset.sessionId = latestSession.sessionId;
                    channelEl.dataset.agentId = agentId;
                    channelEl.dataset.channelType = channelType.toLowerCase();

                    const timeStr = relativeTime(latestSession.updatedAt || latestSession.createdAt);
                    const emoji = channelEmoji(channelType);

                    channelEl.innerHTML = `
                        <div class="list-item-row">
                            <span class="item-title">${emoji} ${escapeHtml(displayName)}${isActive ? ' (active)' : ''}</span>
                        </div>
                        <span class="item-meta">${timeStr}</span>
                    `;

                    channelEl.addEventListener('click', () => openAgentTimeline(agentId, channelType));
                    channelsDiv.appendChild(channelEl);
                }
            }

            group.appendChild(channelsDiv);
            elSessionsList.appendChild(group);
        }

        sessionsInitialLoad = false;
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
                            currentSessionId = null;
                            currentAgentId = null;
                            updateSessionIdDisplay();
                            showView('welcome-screen');
                        }
                        loadSessions();
                    } else {
                        appendSystemMessage(`Failed to delete session: ${res.status}`, 'error');
                    }
                } catch (e) {
                    appendSystemMessage(`Failed to delete session: ${e.message}`, 'error');
                }
            },
            'Delete'
        );
    }

    async function openSession(sessionId, agentId) {
        // Opening a single session — load as timeline for this agent+channel
        const session = await fetchJson(`/sessions/${encodeURIComponent(sessionId)}`);
        const channelType = session?.channelType || 'Web Chat';
        await openAgentTimeline(agentId, channelType);
    }

    async function openAgentTimeline(agentId, channelType) {
        elChatMessages.innerHTML = '<div class="loading">Loading timeline...</div>';

        await joinSession(agentId, null);

        elSessionsList.querySelectorAll('.list-item').forEach(el => {
            el.classList.toggle('active',
                el.dataset.agentId === agentId &&
                (el.dataset.channelType || '').toLowerCase() === channelType.toLowerCase());
        });

        showView('chat-view');

        if (agentId) elAgentSelect.value = agentId;
        elAgentSelect.classList.add('hidden');
        currentAgentId = agentId;
        currentChannelType = channelType;

        elChatTitle.textContent = `${agentId} — ${channelDisplayName(channelType)}`;

        // Fetch all sessions for this agent
        const allSessions = await fetchJson(`/sessions?agentId=${encodeURIComponent(agentId)}`);
        if (!allSessions || allSessions.length === 0) {
            elChatMessages.innerHTML = '';
            elChatMeta.textContent = `Agent: ${agentId} · No sessions yet`;
            currentSessionId = null;
            updateSessionIdDisplay();
            loadChatHeaderModels();
            elChatInput.focus();
            return;
        }

        // Filter to matching channel type and sort oldest-first
        const channelSessions = allSessions
            .filter(s => (s.channelType || 'signalr').toLowerCase() === channelType.toLowerCase())
            .sort((a, b) => new Date(a.createdAt || 0) - new Date(b.createdAt || 0));

        if (channelSessions.length === 0) {
            elChatMessages.innerHTML = '';
            elChatMeta.textContent = `Agent: ${agentId} · No ${channelType} sessions`;
            currentSessionId = null;
            updateSessionIdDisplay();
            loadChatHeaderModels();
            elChatInput.focus();
            return;
        }

        elChatMessages.innerHTML = '';

        // Show only the most recent sessions to keep initial load fast
        const maxInitialSessions = 3;
        const recentSessions = channelSessions.slice(-maxInitialSessions);
        const olderCount = channelSessions.length - recentSessions.length;

        if (olderCount > 0) {
            const loadOlderEl = document.createElement('div');
            loadOlderEl.className = 'load-more-history';
            loadOlderEl.dataset.agentId = agentId;
            loadOlderEl.dataset.channelType = channelType;
            loadOlderEl.dataset.olderSessionIds = JSON.stringify(
                channelSessions.slice(0, olderCount).map(s => s.sessionId)
            );
            loadOlderEl.textContent = `↑ Load ${olderCount} older session${olderCount > 1 ? 's' : ''}`;
            loadOlderEl.style.cssText = 'text-align:center;padding:8px;cursor:pointer;color:var(--text-secondary);font-size:0.85rem;';
            loadOlderEl.addEventListener('click', () => loadOlderSessions(loadOlderEl));
            elChatMessages.appendChild(loadOlderEl);
        }

        let totalMessages = 0;
        for (const session of recentSessions) {
            renderSessionDivider(session);
            const count = session.messageCount || 0;
            totalMessages += count;
            await renderSessionMessages(session.sessionId, count);
        }

        // Join the most recent session for sending new messages
        const latestSession = channelSessions[channelSessions.length - 1];
        await joinSession(agentId, latestSession.sessionId);

        elChatMeta.textContent = `Agent: ${agentId} · ${totalMessages} messages across ${channelSessions.length} session${channelSessions.length > 1 ? 's' : ''}`;
        updateSessionIdDisplay();
        scrollToBottom();
        elChatInput.focus();
        updateSendButtonState();
        loadChatHeaderModels();
    }

    function renderSessionDivider(session) {
        const divider = document.createElement('div');
        divider.className = 'session-divider';
        divider.dataset.sessionId = session.sessionId;
        const date = new Date(session.createdAt || Date.now());
        const dateStr = date.toLocaleDateString(undefined, { month: 'short', day: 'numeric', year: 'numeric' });
        const timeStr = date.toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' });
        divider.innerHTML = `<span class="session-divider-line"></span><span class="session-divider-label">Session started ${dateStr} at ${timeStr}</span><span class="session-divider-line"></span>`;
        elChatMessages.appendChild(divider);
    }

    async function renderSessionMessages(sessionId, totalCount) {
        if (totalCount === 0) return;
        const pageSize = 50;
        const offset = Math.max(0, totalCount - pageSize);
        const historyPage = await fetchJson(`/sessions/${encodeURIComponent(sessionId)}/history?offset=${offset}&limit=${pageSize}`);

        if (offset > 0) {
            const loadMoreEl = document.createElement('div');
            loadMoreEl.className = 'load-more-history';
            loadMoreEl.dataset.sessionId = sessionId;
            loadMoreEl.dataset.nextOffset = '0';
            loadMoreEl.dataset.endOffset = String(offset);
            loadMoreEl.textContent = `↑ Load earlier messages (${offset} more)`;
            loadMoreEl.style.cssText = 'text-align:center;padding:8px;cursor:pointer;color:var(--text-secondary);font-size:0.85rem;';
            loadMoreEl.addEventListener('click', () => loadEarlierMessages(sessionId, loadMoreEl));
            elChatMessages.appendChild(loadMoreEl);
        }

        if (historyPage?.entries) {
            for (const entry of historyPage.entries) renderHistoryEntry(entry);
        }
    }

    async function loadOlderSessions(loadOlderEl) {
        const sessionIds = JSON.parse(loadOlderEl.dataset.olderSessionIds || '[]');
        if (sessionIds.length === 0) { loadOlderEl.remove(); return; }

        loadOlderEl.textContent = 'Loading...';

        const fragment = document.createDocumentFragment();
        for (const sessionId of sessionIds) {
            const session = await fetchJson(`/sessions/${encodeURIComponent(sessionId)}`);
            if (!session) continue;

            const divider = document.createElement('div');
            divider.className = 'session-divider';
            divider.dataset.sessionId = sessionId;
            const date = new Date(session.createdAt || Date.now());
            const dateStr = date.toLocaleDateString(undefined, { month: 'short', day: 'numeric', year: 'numeric' });
            const timeStr = date.toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' });
            divider.innerHTML = `<span class="session-divider-line"></span><span class="session-divider-label">Session started ${dateStr} at ${timeStr}</span><span class="session-divider-line"></span>`;
            fragment.appendChild(divider);

            const count = session.messageCount || (session.history ? session.history.length : 0);
            if (count > 0) {
                const historyPage = await fetchJson(`/sessions/${encodeURIComponent(sessionId)}/history?offset=0&limit=200`);
                if (historyPage?.entries) {
                    // Temporarily redirect renderHistoryEntry to fragment
                    const tempContainer = document.createElement('div');
                    const savedParent = elChatMessages;
                    for (const entry of historyPage.entries) {
                        renderHistoryEntryTo(entry, tempContainer);
                    }
                    for (const child of [...tempContainer.children]) {
                        fragment.appendChild(child);
                    }
                }
            }
        }

        // Insert before the load-older button, then remove it
        loadOlderEl.parentNode.insertBefore(fragment, loadOlderEl);
        loadOlderEl.remove();
    }

    async function loadEarlierMessages(sessionId, loadMoreEl) {
        const endOffset = parseInt(loadMoreEl.dataset.endOffset, 10);
        if (endOffset <= 0) return;

        loadMoreEl.textContent = 'Loading...';
        const pageSize = 30;
        const offset = Math.max(0, endOffset - pageSize);
        const limit = endOffset - offset;

        const historyPage = await fetchJson(`/sessions/${encodeURIComponent(sessionId)}/history?offset=${offset}&limit=${limit}`);
        if (!historyPage?.entries?.length) {
            loadMoreEl.remove();
            return;
        }

        // Insert entries after the load-more button (before existing messages)
        const fragment = document.createDocumentFragment();
        for (const entry of historyPage.entries) {
            const tempContainer = document.createElement('div');
            // Render into temp, then move nodes
            const prevCount = elChatMessages.children.length;
            renderHistoryEntry(entry);
            // The renderHistoryEntry appends to elChatMessages, so grab the last added node
        }

        // Simpler approach: remove button, prepend entries, re-add button if needed
        const nextSibling = loadMoreEl.nextSibling;
        loadMoreEl.remove();

        // Re-render: create entries before existing content
        const tempDiv = document.createElement('div');
        const savedHTML = elChatMessages.innerHTML;
        elChatMessages.innerHTML = '';

        if (offset > 0) {
            const newLoadMore = document.createElement('div');
            newLoadMore.className = 'load-more-history';
            newLoadMore.dataset.sessionId = sessionId;
            newLoadMore.dataset.endOffset = String(offset);
            newLoadMore.textContent = `↑ Load earlier messages (${offset} more)`;
            newLoadMore.style.cssText = 'text-align:center;padding:8px;cursor:pointer;color:var(--text-secondary);font-size:0.85rem;';
            newLoadMore.addEventListener('click', () => loadEarlierMessages(sessionId, newLoadMore));
            elChatMessages.appendChild(newLoadMore);
        }

        for (const entry of historyPage.entries) renderHistoryEntry(entry);
        // Re-append existing messages
        const tempContainer = document.createElement('div');
        tempContainer.innerHTML = savedHTML;
        // Skip the old load-more button if it was in savedHTML
        for (const child of [...tempContainer.children]) {
            if (!child.classList.contains('load-more-history')) {
                elChatMessages.appendChild(child);
            }
        }
    }

    // =========================================================================
    // Channels
    // =========================================================================

    let channelsRefreshTimer = null;
    const CHANNELS_REFRESH_MS = 30000;

    function channelEmoji(name) {
        const map = { websocket: '🌐', signalr: '🌐', 'web-chat': '💬', 'web chat': '💬', telegram: '✈️', discord: '🎮', slack: '💼', tui: '🖥️' };
        return map[(name || '').toLowerCase()] || '📡';
    }

    function channelDisplayName(name) {
        const n = (name || '').toLowerCase();
        // Map internal channel IDs to user-facing display names
        if (_channelDisplayNames[n]) return _channelDisplayNames[n];
        // Fallback for known types
        if (n === 'signalr') return 'Web Chat';
        if (n === 'web-chat') return 'Web Chat';
        return name || 'Web Chat';
    }

    // Populated from /api/channels response
    let _channelDisplayNames = {};

    function buildCapabilityIcons(ch) {
        const caps = [];
        if (ch.supportsStreaming) caps.push('<span class="channel-cap" title="Streaming">⚡</span>');
        if (ch.supportsSteering) caps.push('<span class="channel-cap" title="Steering">🎯</span>');
        if (ch.supportsFollowUp) caps.push('<span class="channel-cap" title="Follow-up">🔄</span>');
        if (ch.supportsThinkingDisplay) caps.push('<span class="channel-cap" title="Thinking">💭</span>');
        if (ch.supportsToolDisplay) caps.push('<span class="channel-cap" title="Tools">🔧</span>');
        return caps.join('');
    }

    async function loadChannels() {
        const channels = await fetchJson('/channels');
        if (!channels || channels.length === 0) {
            elChannelsList.innerHTML = '<div class="empty-state">No channels</div>';
            return;
        }

        // Build display name lookup from API response
        for (const ch of channels) {
            if (ch.name && ch.displayName) {
                _channelDisplayNames[ch.name.toLowerCase()] = ch.displayName;
            }
        }

        // Build a set of channel names from the response for pruning stale entries
        const incomingNames = new Set(channels.map(ch => ch.name));

        // Remove channels that no longer exist
        for (const existing of [...elChannelsList.querySelectorAll('.list-item[data-channel]')]) {
            if (!incomingNames.has(existing.dataset.channel)) {
                existing.remove();
            }
        }

        // Remove any loading/empty-state placeholders
        for (const placeholder of [...elChannelsList.querySelectorAll('.loading, .empty-state')]) {
            placeholder.remove();
        }

        for (const ch of channels) {
            const dotClass = ch.isRunning ? 'running' : 'stopped';
            const statusText = ch.isRunning ? 'running' : 'stopped';
            const capsHtml = buildCapabilityIcons(ch);
            const titleHtml = `<span class="channel-status-dot ${dotClass}" aria-hidden="true"></span> ${channelEmoji(ch.name)} ${escapeHtml(ch.displayName || ch.name)}`;

            const existing = elChannelsList.querySelector(`.list-item[data-channel="${CSS.escape(ch.name)}"]`);
            if (existing) {
                // Update only the parts that may have changed
                const titleEl = existing.querySelector('.item-title');
                if (titleEl && titleEl.innerHTML !== titleHtml) titleEl.innerHTML = titleHtml;
                const metaEl = existing.querySelector('.item-meta');
                if (metaEl && metaEl.textContent !== statusText) metaEl.textContent = statusText;
                const capsEl = existing.querySelector('.channel-caps');
                if (capsEl && capsEl.innerHTML !== capsHtml) capsEl.innerHTML = capsHtml;
            } else {
                const el = document.createElement('div');
                el.className = 'list-item';
                el.setAttribute('role', 'listitem');
                el.dataset.channel = ch.name;
                el.innerHTML = `
                    <div class="list-item-row">
                        <span class="item-title">${titleHtml}</span>
                        <span class="item-meta" style="font-size:0.68rem;">${statusText}</span>
                    </div>
                    <div class="channel-caps">${capsHtml}</div>
                `;
                elChannelsList.appendChild(el);
            }
        }
    }

    function scheduleChannelsRefresh() {
        if (channelsRefreshTimer) clearInterval(channelsRefreshTimer);
        channelsRefreshTimer = setInterval(loadChannels, CHANNELS_REFRESH_MS);
    }

    // =========================================================================
    // Extensions
    // =========================================================================

    async function loadExtensions() {
        elExtensionsList.innerHTML = '<div class="loading">Loading...</div>';
        const extensions = await fetchJson('/extensions');
        if (!extensions || extensions.length === 0) {
            elExtensionsList.innerHTML = '<div class="empty-state">No extensions loaded</div>';
            return;
        }
        // Group by extension name
        const groups = {};
        for (const ext of extensions) {
            const key = ext.name || 'Unknown';
            if (!groups[key]) groups[key] = { version: ext.version, types: [] };
            groups[key].types.push(ext.type || 'unknown');
        }
        elExtensionsList.innerHTML = '';
        for (const [name, info] of Object.entries(groups)) {
            const el = document.createElement('div');
            el.className = 'list-item';
            el.setAttribute('role', 'listitem');
            const typeBadges = info.types.map(t => `<span class="extension-type-badge">${escapeHtml(t)}</span>`).join(' ');
            el.innerHTML = `
                <div class="list-item-row">
                    <span class="item-title">${escapeHtml(name)}</span>
                    <span class="item-meta" style="font-size:0.68rem;">v${escapeHtml(info.version || '?')}</span>
                </div>
                <div style="margin-top:2px;">${typeBadges}</div>
            `;
            elExtensionsList.appendChild(el);
        }
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
            const statusLabel = a.status || 'ready';
            el.innerHTML = `
                <div class="list-item-row">
                    <span class="item-title">
                        <span class="agent-status-dot ${statusClass}" aria-hidden="true"></span>
                        ${escapeHtml(name)}
                    </span>
                    <span class="item-meta" style="font-size:0.68rem;">${escapeHtml(statusLabel)}</span>
                </div>
                <span class="item-meta">${model ? 'Model: ' + escapeHtml(model) : ''}</span>
            `;
            el.addEventListener('click', () => { currentAgentId = name; openAgentConfig(name); });
            elAgentsList.appendChild(el);
        }
        populateAgentSelect(agents);
        populateActivityAgentFilter();
        updateSendButtonState();
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
        if (currentAgentId && !elAgentSelect.value) {
            elAgentSelect.value = currentAgentId;
        }
    }

    // =========================================================================
    // View switching helper
    // =========================================================================

    function showView(viewId) {
        ['welcome-screen', 'chat-view', 'agent-config-view', 'cron-view'].forEach(id => {
            const el = document.getElementById(id);
            if (el) el.classList.toggle('hidden', id !== viewId);
        });
    }

    // =========================================================================
    // Agent config view (full canvas — replaces chat area)
    // =========================================================================

    async function openAgentConfig(agentId) {
        const agent = await fetchJson(`/agents/${encodeURIComponent(agentId)}`);
        if (!agent) { appendSystemMessage('Agent not found', 'error'); return; }

        // Show agent config view
        showView('agent-config-view');

        $('#agent-config-title').textContent = agent.displayName || agent.name || agentId;

        // Build the full config form
        const body = $('#agent-config-body');
        body.innerHTML = buildAgentConfigForm(agent, agentId);

        // Wire save button
        $('#btn-agent-save').onclick = () => saveAgentConfig(agentId);

        // Wire toggle label updates
        ['cfg-enabled', 'cfg-memoryEnabled', 'cfg-temporalDecayEnabled'].forEach(id => {
            const cb = $(`#${id}`);
            if (cb) cb.addEventListener('change', () => {
                const lbl = cb.parentElement.querySelector('label');
                if (lbl) lbl.textContent = cb.checked ? 'Active' : 'Disabled';
            });
        });

        $('#btn-agent-chat').onclick = () => {
            // Switch back to chat with this agent
            showView('chat-view');
            currentAgentId = agentId;
            elAgentSelect.value = agentId;
            startNewChat();
        };

        // Load models for the provider dropdown
        const provider = agent.apiProvider || agent.provider || '';
        if (provider) {
            const models = await fetchJson(`/models?provider=${encodeURIComponent(provider)}`);
            if (models) {
                const select = $('#cfg-model');
                if (select) {
                    select.innerHTML = models.map(m =>
                        `<option value="${escapeHtml(m.modelId || m.id)}" ${(m.modelId || m.id) === agent.modelId ? 'selected' : ''}>${escapeHtml(m.name || m.modelId || m.id)}</option>`
                    ).join('');
                }
            }
        }

        // Load runtime status
        await loadAgentStatus(agentId);
    }

    function buildAgentConfigForm(agent, agentId) {
        const memoryEnabled = agent.memory?.enabled || agent.memoryEnabled || false;
        const memoryIndexing = agent.memory?.indexing || 'auto';
        const memoryTopK = agent.memory?.search?.defaultTopK ?? 10;
        const temporalEnabled = agent.memory?.search?.temporalDecay?.enabled ?? true;
        const temporalHalfLife = agent.memory?.search?.temporalDecay?.halfLifeDays ?? 30;
        const allowedModelIds = (agent.allowedModelIds || agent.allowedModels || []).join(', ');
        const subAgentIds = (agent.subAgentIds || agent.subAgents || []).join(', ');
        const agentEnabled = agent.enabled !== undefined ? agent.enabled : true;

        let metadataJson = '{}';
        try { metadataJson = JSON.stringify(agent.metadata || {}, null, 2); } catch { /* ignore */ }
        let isolationJson = '{}';
        try { isolationJson = JSON.stringify(agent.isolationOptions || {}, null, 2); } catch { /* ignore */ }

        return `
            <div class="config-section">
                <h3>Identity</h3>
                <div class="config-grid">
                    <div class="config-field">
                        <label>Agent ID</label>
                        <input type="text" value="${escapeHtml(agentId || agent.agentId || '')}" disabled class="config-input">
                    </div>
                    <div class="config-field">
                        <label>Display Name</label>
                        <input type="text" id="cfg-displayName" value="${escapeHtml(agent.displayName || agent.name || '')}" class="config-input">
                    </div>
                    <div class="config-field full-width">
                        <label>Description</label>
                        <textarea id="cfg-description" class="config-input" rows="3">${escapeHtml(agent.description || '')}</textarea>
                    </div>
                    <div class="config-field">
                        <label>Enabled</label>
                        <div class="config-toggle">
                            <input type="checkbox" id="cfg-enabled" ${agentEnabled ? 'checked' : ''}>
                            <label for="cfg-enabled">${agentEnabled ? 'Active' : 'Disabled'}</label>
                        </div>
                    </div>
                </div>
            </div>

            <div class="config-section">
                <h3>Model</h3>
                <div class="config-grid">
                    <div class="config-field">
                        <label>Provider</label>
                        <input type="text" value="${escapeHtml(agent.apiProvider || agent.provider || '')}" disabled class="config-input">
                    </div>
                    <div class="config-field">
                        <label>Model</label>
                        <select id="cfg-model" class="config-input">
                            <option value="${escapeHtml(agent.modelId || agent.model || agent.defaultModel || '')}" selected>${escapeHtml(agent.modelId || agent.model || agent.defaultModel || 'unknown')}</option>
                        </select>
                    </div>
                    <div class="config-field">
                        <label>Isolation Strategy</label>
                        <input type="text" value="${escapeHtml(agent.isolationStrategy || 'in-process')}" disabled class="config-input">
                    </div>
                    <div class="config-field">
                        <label>Max Concurrent Sessions</label>
                        <input type="number" id="cfg-maxSessions" value="${agent.maxConcurrentSessions || 0}" class="config-input">
                    </div>
                    <div class="config-field full-width">
                        <label>Allowed Model IDs <span class="config-muted">(comma-separated)</span></label>
                        <input type="text" id="cfg-allowedModelIds" value="${escapeHtml(allowedModelIds)}" class="config-input" placeholder="e.g. gpt-4.1, claude-sonnet-4-20250514">
                    </div>
                    <div class="config-field full-width">
                        <label>Sub-Agent IDs <span class="config-muted">(comma-separated)</span></label>
                        <input type="text" id="cfg-subAgentIds" value="${escapeHtml(subAgentIds)}" class="config-input" placeholder="e.g. coding-agent, research-agent">
                    </div>
                </div>
            </div>

            <div class="config-section">
                <h3>System Prompt</h3>
                <div class="config-field full-width">
                    <label>System Prompt Files (loaded in order)</label>
                    <div id="cfg-promptFiles" class="config-prompt-files">
                        ${(agent.systemPromptFiles || []).map(f => `<div class="prompt-file-item">${escapeHtml(f)}</div>`).join('') || '<div class="config-muted">Using default order (AGENTS.md → SOUL.md → TOOLS.md → BOOTSTRAP.md → IDENTITY.md → USER.md)</div>'}
                    </div>
                </div>
                <div class="config-field full-width" style="margin-top:12px;">
                    <label>Inline System Prompt</label>
                    <textarea id="cfg-systemPrompt" class="config-input config-textarea" rows="6" placeholder="Optional inline system prompt...">${escapeHtml(agent.systemPrompt || '')}</textarea>
                </div>
            </div>

            <div class="config-section">
                <h3>Memory</h3>
                <div class="config-grid">
                    <div class="config-field">
                        <label>Memory Enabled</label>
                        <div class="config-toggle">
                            <input type="checkbox" id="cfg-memoryEnabled" ${memoryEnabled ? 'checked' : ''}>
                            <label for="cfg-memoryEnabled">${memoryEnabled ? 'Active' : 'Disabled'}</label>
                        </div>
                    </div>
                    <div class="config-field">
                        <label>Indexing Mode</label>
                        <select id="cfg-memoryIndexing" class="config-input">
                            <option value="auto" ${memoryIndexing === 'auto' ? 'selected' : ''}>Auto</option>
                            <option value="manual" ${memoryIndexing === 'manual' ? 'selected' : ''}>Manual</option>
                            <option value="off" ${memoryIndexing === 'off' ? 'selected' : ''}>Off</option>
                        </select>
                    </div>
                    <div class="config-field">
                        <label>Search Default Top-K</label>
                        <input type="number" id="cfg-memoryTopK" value="${memoryTopK}" min="1" max="100" class="config-input">
                    </div>
                    <div class="config-field">
                        <label>Temporal Decay Enabled</label>
                        <div class="config-toggle">
                            <input type="checkbox" id="cfg-temporalDecayEnabled" ${temporalEnabled ? 'checked' : ''}>
                            <label for="cfg-temporalDecayEnabled">${temporalEnabled ? 'Active' : 'Disabled'}</label>
                        </div>
                    </div>
                    <div class="config-field">
                        <label>Temporal Decay Half-Life (days)</label>
                        <input type="number" id="cfg-temporalHalfLife" value="${temporalHalfLife}" min="1" class="config-input">
                    </div>
                </div>
            </div>

            <div class="config-section">
                <h3>Tools</h3>
                <div class="config-field full-width">
                    <label>Tool IDs</label>
                    <div class="config-value">${(agent.toolIds || []).join(', ') || '<span class="config-muted">All tools available</span>'}</div>
                </div>
            </div>

            <div class="config-section">
                <h3>Metadata</h3>
                <div class="config-field full-width">
                    <label>Agent Metadata <span class="config-muted">(read-only)</span></label>
                    <pre class="config-json">${escapeHtml(metadataJson)}</pre>
                </div>
            </div>

            <div class="config-section">
                <h3>Isolation Options</h3>
                <div class="config-field full-width">
                    <label>Strategy-specific options <span class="config-muted">(read-only)</span></label>
                    <pre class="config-json">${escapeHtml(isolationJson)}</pre>
                </div>
            </div>

            <div class="config-section" id="agent-status-section">
                <h3>Runtime Status</h3>
                <div id="agent-runtime-status">Loading...</div>
            </div>
        `;
    }

    async function loadAgentStatus(agentId) {
        const statusEl = $('#agent-runtime-status');
        if (!statusEl) return;
        try {
            const instances = await fetchJson('/agents/instances') || [];
            const agentInstances = instances.filter(i => i.agentId === agentId);
            if (agentInstances.length === 0) {
                statusEl.innerHTML = '<div class="config-value config-muted">No active instances</div>';
                return;
            }
            statusEl.innerHTML = `<div class="config-value" style="margin-bottom:8px">${agentInstances.length} active instance(s)</div>` +
                agentInstances.map(inst => {
                    const emoji = inst.status === 'Running' ? '🟢' : inst.status === 'Idle' ? '🟡' : '🔴';
                    return `<div class="config-value">${emoji} ${escapeHtml(inst.status || 'unknown')} — <code style="font-size:0.78rem;background:rgba(0,0,0,0.25);padding:1px 5px;border-radius:3px">${escapeHtml((inst.sessionId || '?').substring(0, 12))}…</code></div>`;
                }).join('');
        } catch {
            statusEl.innerHTML = '<div class="config-value config-muted">Unable to load status</div>';
        }
    }

    async function saveAgentConfig(agentId) {
        const agentData = await fetchJson(`/agents/${encodeURIComponent(agentId)}`);
        if (!agentData) { appendSystemMessage('Agent not found', 'error'); return; }

        const displayName = $('#cfg-displayName')?.value;
        const description = $('#cfg-description')?.value;
        const systemPrompt = $('#cfg-systemPrompt')?.value;
        const modelId = $('#cfg-model')?.value;
        const maxSessions = parseInt($('#cfg-maxSessions')?.value, 10);
        const enabled = $('#cfg-enabled')?.checked ?? true;
        const allowedModelIdsRaw = $('#cfg-allowedModelIds')?.value || '';
        const subAgentIdsRaw = $('#cfg-subAgentIds')?.value || '';
        const memoryEnabled = $('#cfg-memoryEnabled')?.checked ?? false;
        const memoryIndexing = $('#cfg-memoryIndexing')?.value || 'auto';
        const memoryTopK = parseInt($('#cfg-memoryTopK')?.value, 10) || 10;
        const temporalDecayEnabled = $('#cfg-temporalDecayEnabled')?.checked ?? true;
        const temporalHalfLife = parseInt($('#cfg-temporalHalfLife')?.value, 10) || 30;

        const updated = { ...agentData };
        if (displayName !== undefined) updated.displayName = displayName;
        if (description !== undefined) updated.description = description;
        if (systemPrompt !== undefined) updated.systemPrompt = systemPrompt;
        if (modelId) updated.modelId = modelId;
        if (!isNaN(maxSessions)) updated.maxConcurrentSessions = maxSessions;
        updated.enabled = enabled;
        updated.allowedModelIds = allowedModelIdsRaw ? allowedModelIdsRaw.split(',').map(s => s.trim()).filter(Boolean) : [];
        updated.subAgentIds = subAgentIdsRaw ? subAgentIdsRaw.split(',').map(s => s.trim()).filter(Boolean) : [];
        updated.memory = {
            enabled: memoryEnabled,
            indexing: memoryIndexing,
            search: {
                defaultTopK: memoryTopK,
                temporalDecay: {
                    enabled: temporalDecayEnabled,
                    halfLifeDays: temporalHalfLife
                }
            }
        };

        const res = await fetch(`${API_BASE}/agents/${encodeURIComponent(agentId)}`, {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(updated)
        });
        if (res.ok) {
            appendSystemMessage('Agent settings saved.');
            loadAgents();
        } else {
            appendSystemMessage(`Failed to save: ${res.status}`, 'error');
        }
    }

    // =========================================================================
    // Agent debug info panel (kept as fallback)
    // =========================================================================

    async function showAgentDebugInfo(agentId) {
        const agent = await fetchJson(`/agents/${encodeURIComponent(agentId)}`);
        if (!agent) {
            // Fall back to cached agent data
            const cached = agentsCache.find(a => (a.name || a.agentId || a.id) === agentId);
            if (!cached) { appendSystemMessage('Agent not found', 'error'); return; }
            return renderAgentDebugPanel(cached, agentId);
        }
        return renderAgentDebugPanel(agent, agentId);
    }

    async function renderAgentDebugPanel(agent, agentId) {
        const agentName = agent.displayName || agent.name || agentId;

        // Fetch instances in parallel
        let agentInstances = [];
        try {
            const instances = await fetchJson('/agents/instances') || [];
            agentInstances = instances.filter(i => i.agentId === agentId);
        } catch { /* ignore */ }

        let html = `<div class="debug-panel">`;
        html += `<h3>🔍 ${escapeHtml(agentName)}</h3>`;

        // Agent configuration
        html += `<div class="debug-section"><h4>Configuration</h4>`;
        html += `<table class="debug-table">`;
        html += `<tr><td>Agent ID</td><td><code>${escapeHtml(agentId)}</code></td></tr>`;
        if (agent.displayName) html += `<tr><td>Display Name</td><td>${escapeHtml(agent.displayName)}</td></tr>`;
        if (agent.apiProvider || agent.provider) html += `<tr><td>Provider</td><td>${escapeHtml(agent.apiProvider || agent.provider || '')}</td></tr>`;
        if (agent.modelId || agent.model || agent.defaultModel) html += `<tr><td>Model</td><td>${escapeHtml(agent.modelId || agent.model || agent.defaultModel || '')}</td></tr>`;
        if (agent.isolationStrategy) html += `<tr><td>Isolation</td><td>${escapeHtml(agent.isolationStrategy)}</td></tr>`;
        html += `<tr><td>Memory</td><td>${agent.memoryEnabled ? '✅ Enabled' : '❌ Disabled'}</td></tr>`;
        if (agent.systemPromptFiles && agent.systemPromptFiles.length > 0) {
            html += `<tr><td>Prompt Files</td><td>${agent.systemPromptFiles.map(f => `<code>${escapeHtml(f)}</code>`).join(', ')}</td></tr>`;
        }
        if (agent.status) html += `<tr><td>Status</td><td>${escapeHtml(agent.status)}</td></tr>`;
        html += `</table></div>`;

        // Active instances
        html += `<div class="debug-section"><h4>Active Instances (${agentInstances.length})</h4>`;
        if (agentInstances.length === 0) {
            html += `<p class="debug-muted">No active instances</p>`;
        } else {
            for (const inst of agentInstances) {
                const statusEmoji = inst.status === 'Running' ? '🟢' : inst.status === 'Idle' ? '🟡' : '🔴';
                const sid = escapeHtml(inst.sessionId || '');
                const escapedAgentId = escapeHtml(agentId).replace(/'/g, "\\'");
                const escapedSid = sid.replace(/'/g, "\\'");
                html += `<div class="debug-instance">`;
                html += `<span>${statusEmoji} ${escapeHtml(inst.status || 'unknown')}</span>`;
                html += `<code>${sid}</code>`;
                if (inst.isolationStrategy) html += `<span style="font-size:0.75rem;color:var(--text-secondary)">${escapeHtml(inst.isolationStrategy)}</span>`;
                html += `<button class="btn-sm btn-danger-sm" data-stop-agent="${escapedAgentId}" data-stop-session="${escapedSid}">Stop</button>`;
                html += `</div>`;
            }
        }
        html += `</div>`;

        // Current session info
        if (currentSessionId && currentAgentId === agentId) {
            html += `<div class="debug-section"><h4>Current Session</h4>`;
            html += `<table class="debug-table">`;
            html += `<tr><td>Session ID</td><td><code>${escapeHtml(currentSessionId)}</code></td></tr>`;
            html += `<tr><td>Connection ID</td><td><code>${escapeHtml(connectionId || 'none')}</code></td></tr>`;
            html += `<tr><td>SignalR</td><td>${connection?.state === signalR.HubConnectionState.Connected ? '🟢 Connected' : '🔴 Disconnected'}</td></tr>`;
            html += `<tr><td>Streaming</td><td>${isStreaming ? '⏳ Yes' : 'No'}</td></tr>`;
            html += `</table></div>`;

            // Try to fetch session status
            try {
                const sessionStatus = await fetchJson(`/agents/${encodeURIComponent(agentId)}/sessions/${encodeURIComponent(currentSessionId)}/status`);
                if (sessionStatus) {
                    html += `<div class="debug-section"><h4>Session Status</h4>`;
                    html += `<table class="debug-table">`;
                    if (sessionStatus.status) html += `<tr><td>Status</td><td>${escapeHtml(sessionStatus.status)}</td></tr>`;
                    if (sessionStatus.messageCount != null) html += `<tr><td>Messages</td><td>${sessionStatus.messageCount}</td></tr>`;
                    if (sessionStatus.channelType) html += `<tr><td>Channel</td><td>${escapeHtml(sessionStatus.channelType)}</td></tr>`;
                    if (sessionStatus.createdAt) html += `<tr><td>Created</td><td>${escapeHtml(new Date(sessionStatus.createdAt).toLocaleString())}</td></tr>`;
                    if (sessionStatus.updatedAt) html += `<tr><td>Updated</td><td>${escapeHtml(new Date(sessionStatus.updatedAt).toLocaleString())}</td></tr>`;
                    html += `</table></div>`;
                }
            } catch { /* ignore */ }
        }

        // Quick actions
        html += `<div class="debug-section"><h4>Quick Actions</h4>`;
        html += `<div class="debug-actions">`;
        if (currentSessionId && currentAgentId === agentId) {
            html += `<button class="btn-sm btn-danger-sm" id="debug-btn-stop">⏹ Stop Agent</button>`;
            html += `<button class="btn-sm" id="debug-btn-reset">🔄 Reset Session</button>`;
            html += `<button class="btn-sm" id="debug-btn-copy-sid">📋 Copy Session ID</button>`;
        }
        html += `<button class="btn-sm" id="debug-btn-refresh" data-debug-agent="${escapeHtml(agentId)}">↻ Refresh</button>`;
        html += `</div></div>`;

        html += `</div>`;

        showDebugModal(html, agentId);
    }

    function showDebugModal(html, agentId) {
        const body = $('#debug-modal-body');
        body.innerHTML = html;
        elDebugModal.classList.remove('hidden');

        // Bind quick-action buttons
        const btnStop = body.querySelector('#debug-btn-stop');
        if (btnStop) btnStop.addEventListener('click', async () => {
            if (currentSessionId && currentAgentId) {
                await fetch(`${API_BASE}/agents/${encodeURIComponent(currentAgentId)}/sessions/${encodeURIComponent(currentSessionId)}/stop`, { method: 'POST' });
                showAgentDebugInfo(agentId);
            }
        });

        const btnReset = body.querySelector('#debug-btn-reset');
        if (btnReset) btnReset.addEventListener('click', () => {
            closeDebugModal();
            executeReset();
        });

        const btnCopy = body.querySelector('#debug-btn-copy-sid');
        if (btnCopy) btnCopy.addEventListener('click', () => {
            if (currentSessionId) {
                navigator.clipboard.writeText(currentSessionId).then(() => {
                    btnCopy.textContent = '✅ Copied!';
                    setTimeout(() => { btnCopy.textContent = '📋 Copy Session ID'; }, 1200);
                }).catch(() => {
                    const ta = document.createElement('textarea');
                    ta.value = currentSessionId;
                    ta.style.cssText = 'position:fixed;opacity:0';
                    document.body.appendChild(ta);
                    ta.select();
                    document.execCommand('copy');
                    document.body.removeChild(ta);
                    btnCopy.textContent = '✅ Copied!';
                    setTimeout(() => { btnCopy.textContent = '📋 Copy Session ID'; }, 1200);
                });
            }
        });

        const btnRefresh = body.querySelector('#debug-btn-refresh');
        if (btnRefresh) btnRefresh.addEventListener('click', () => {
            showAgentDebugInfo(btnRefresh.dataset.debugAgent);
        });

        // Bind instance stop buttons
        body.querySelectorAll('[data-stop-agent]').forEach(btn => {
            btn.addEventListener('click', async () => {
                const aId = btn.dataset.stopAgent;
                const sId = btn.dataset.stopSession;
                btn.disabled = true;
                btn.textContent = '...';
                await fetch(`${API_BASE}/agents/${encodeURIComponent(aId)}/sessions/${encodeURIComponent(sId)}/stop`, { method: 'POST' });
                showAgentDebugInfo(agentId);
            });
        });
    }

    function closeDebugModal() {
        elDebugModal.classList.add('hidden');
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
            providers.sort((a, b) => (a.name || '').localeCompare(b.name || ''));
            for (const p of providers) {
                const opt = document.createElement('option');
                opt.value = p.providerId || p.id || p.name || 'unknown';
                opt.textContent = p.name || opt.value;
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
            filtered.sort((a, b) => {
                const nameA = a.name || a.modelId || a.id || '';
                const nameB = b.name || b.modelId || b.id || '';
                return nameA.localeCompare(nameB);
            });
            for (const m of filtered) {
                const opt = document.createElement('option');
                opt.value = m.modelId || m.id || 'unknown';
                opt.textContent = m.name || opt.value;
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

        const body = {
            agentId: name,
            displayName: name,
            modelId: model,
            apiProvider: provider
        };
        if (systemPrompt) body.systemPrompt = systemPrompt;

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
    // Model selector in chat header
    // =========================================================================

    async function loadChatHeaderModels() {
        if (!currentAgentId) return;
        try {
            // First, get the current model from agent
            const agent = await fetchJson(`/agents/${encodeURIComponent(currentAgentId)}`);
            const currentModel = agent?.modelId || '';
            
            // Try to fetch models list
            let models = [];
            try {
                models = await fetchJson('/models');
            } catch (e) {
                console.warn('Models endpoint not available, showing current model only');
            }
            
            elModelSelect.innerHTML = '';
            
            if (models && models.length > 0) {
                // Filter to the agent's provider to avoid cross-provider duplicates
                const agentProvider = (agent?.apiProvider || '').toLowerCase();
                const filtered = agentProvider
                    ? models.filter(m => (m.provider || '').toLowerCase() === agentProvider)
                    : models;
                filtered.sort((a, b) => {
                    const nameA = a.name || a.modelId || a.id || '';
                    const nameB = b.name || b.modelId || b.id || '';
                    return nameA.localeCompare(nameB);
                });
                for (const m of filtered) {
                    const opt = document.createElement('option');
                    const modelId = m.modelId || m.id || 'unknown';
                    opt.value = modelId;
                    opt.textContent = m.name || modelId;
                    if (modelId === currentModel) opt.selected = true;
                    elModelSelect.appendChild(opt);
                }
            } else {
                // Fallback: show at least the current model
                const opt = document.createElement('option');
                opt.value = currentModel;
                opt.textContent = currentModel || 'Unknown model';
                opt.selected = true;
                elModelSelect.appendChild(opt);
            }
        } catch (e) {
            console.error('Failed to load models:', e);
            // Show a placeholder if everything fails
            elModelSelect.innerHTML = '<option value="">Error loading models</option>';
        }
    }

    async function handleModelChange() {
        if (!currentAgentId || !elModelSelect.value) return;
        
        const newModel = elModelSelect.value;
        try {
            // Get current agent descriptor
            const agent = await fetchJson(`/agents/${encodeURIComponent(currentAgentId)}`);
            if (!agent) {
                appendSystemMessage('❌ Failed to load agent details', 'error');
                return;
            }
            
            // Update model
            agent.modelId = newModel;
            
            const res = await fetch(`${API_BASE}/agents/${encodeURIComponent(currentAgentId)}`, {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(agent)
            });
            
            if (res.ok) {
                appendSystemMessage(`✅ Model changed to ${newModel}`);
            } else {
                appendSystemMessage(`❌ Failed to change model: ${res.status}`, 'error');
            }
        } catch (e) {
            appendSystemMessage(`❌ Failed to change model: ${e.message}`, 'error');
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
        let filterType = '';
        let badgeClass = 'system';
        let icon = '📌';
        if (eventType.includes('Error') || eventType === 'error') {
            cssClass += ' error'; filterType = 'error'; badgeClass = 'error'; icon = '❌';
        } else if (eventType.includes('Response') || eventType.includes('Sent')) {
            cssClass += ' response-sent'; filterType = 'response'; badgeClass = 'response'; icon = '✅';
        } else if (eventType.includes('Tool')) {
            cssClass += ' msg-received'; filterType = 'tool'; badgeClass = 'tool'; icon = '🔧';
        } else if (eventType.includes('Message') || eventType.includes('Received')) {
            cssClass += ' msg-received'; filterType = 'message'; badgeClass = 'message'; icon = '💬';
        }
        el.className = cssClass;
        el.dataset.agent = evt.agentId || evt.agent || '';
        el.dataset.eventCategory = filterType;

        const time = formatTime(evt.timestamp || new Date().toISOString());
        const channel = evt.channel || evt.source || '';
        const preview = (evt.content || evt.message || '').substring(0, 80);
        el.innerHTML = `
            <span class="activity-time">${time}</span>
            ${channel ? `<span class="activity-channel">[${escapeHtml(channel)}]</span>` : ''}
            <span class="activity-type-badge ${badgeClass}">${icon} ${escapeHtml(eventType)}</span>
            ${preview ? escapeHtml(preview) : ''}${(evt.content || evt.message || '').length > 80 ? '...' : ''}
        `;
        elActivityItems.insertBefore(el, elActivityItems.firstChild);
        while (elActivityItems.children.length > MAX_ACTIVITY_ITEMS) {
            elActivityItems.removeChild(elActivityItems.lastChild);
        }
        applyActivityFilters();
    }

    function trackActivity(category, agentId, content) {
        if (!isActivitySubscribed) return;
        handleActivityEvent({
            eventType: category === 'message' ? 'MessageSent' : category === 'response' ? 'ResponseReceived' : category === 'tool' ? 'ToolCall' : 'Error',
            agentId: agentId || '',
            content: content || '',
            timestamp: new Date().toISOString(),
            channel: 'WebUI'
        });
    }

    function applyActivityFilters() {
        const agentFilter = elActivityFilterAgent.value;
        const typeFilter = elActivityFilterType.value;
        elActivityItems.querySelectorAll('.activity-item').forEach(el => {
            const matchAgent = !agentFilter || el.dataset.agent === agentFilter;
            const matchType = !typeFilter || el.dataset.eventCategory === typeFilter;
            el.classList.toggle('filtered-out', !(matchAgent && matchType));
        });
    }

    function populateActivityAgentFilter() {
        const current = elActivityFilterAgent.value;
        elActivityFilterAgent.innerHTML = '<option value="">All Agents</option>';
        for (const a of agentsCache) {
            const name = a.name || a.agentId || a.id || 'unknown';
            const opt = document.createElement('option');
            opt.value = name;
            opt.textContent = name;
            elActivityFilterAgent.appendChild(opt);
        }
        if (current) elActivityFilterAgent.value = current;
    }

    // =========================================================================
    // Cron view
    // =========================================================================

    function openCronView() {
        showView('cron-view');
        loadCronJobs();
    }

    async function loadCronJobs() {
        const body = $('#cron-body');
        if (!body) return;

        const jobs = await fetchJson('/cron');
        if (!jobs || !Array.isArray(jobs) || jobs.length === 0) {
            body.innerHTML = `
                <div class="cron-empty">
                    <p>No cron jobs configured. Add one to schedule agent tasks.</p>
                </div>`;
            return;
        }

        let html = `<table class="cron-table">
            <thead><tr>
                <th>Name</th><th>Schedule</th><th>Agent</th><th>Last Run</th><th>Next Run</th><th>Status</th><th>Actions</th>
            </tr></thead><tbody>`;
        for (const job of jobs) {
            const statusLabel = job.enabled ? 'active' : 'paused';
            html += `<tr>
                <td>${escapeHtml(job.name || '')}</td>
                <td><code>${escapeHtml(job.schedule || '')}</code></td>
                <td>${escapeHtml(job.agentId || '')}</td>
                <td>${job.lastRunAt ? escapeHtml(relativeTime(job.lastRunAt)) : '—'}</td>
                <td>${job.nextRunAt ? escapeHtml(relativeTime(job.nextRunAt)) : '—'}</td>
                <td>${statusLabel}</td>
                <td>
                    <button class="btn btn-sm" onclick="runCronJob('${job.id}')">▶ Run</button>
                    <button class="btn btn-sm btn-danger-sm" onclick="deleteCronJob('${job.id}')">🗑</button>
                </td>
            </tr>`;
        }
        html += '</tbody></table>';
        body.innerHTML = html;
    }

    window.runCronJob = async function(jobId) {
        try {
            const res = await fetch(`${API_BASE}/cron/${encodeURIComponent(jobId)}/run`, { method: 'POST' });
            if (res.ok) {
                appendSystemMessage('Cron job triggered.');
                loadCronJobs();
                loadSessions();
            } else {
                appendSystemMessage(`Failed to run: ${res.status}`, 'error');
            }
        } catch (e) {
            appendSystemMessage(`Error: ${e.message}`, 'error');
        }
    };

    window.deleteCronJob = async function(jobId) {
        showConfirm('Delete this cron job?', 'Delete Cron Job', async () => {
            try {
                const res = await fetch(`${API_BASE}/cron/${encodeURIComponent(jobId)}`, { method: 'DELETE' });
                if (res.ok || res.status === 204) {
                    loadCronJobs();
                } else {
                    appendSystemMessage(`Failed to delete: ${res.status}`, 'error');
                }
            } catch (e) {
                appendSystemMessage(`Error: ${e.message}`, 'error');
            }
        }, 'Delete');
    };

    function showAddCronForm() {
        const body = $('#cron-body');
        if (!body) return;

        // Don't add duplicate form
        if (body.querySelector('.cron-form')) return;

        const formHtml = `
            <div class="cron-form">
                <h3>New Cron Job</h3>
                <div class="config-grid">
                    <div class="config-field">
                        <label>Job Name</label>
                        <input type="text" id="cron-name" class="config-input" placeholder="e.g. daily-summary">
                    </div>
                    <div class="config-field">
                        <label>Cron Expression</label>
                        <input type="text" id="cron-schedule" class="config-input" placeholder="e.g. 0 9 * * *">
                    </div>
                    <div class="config-field">
                        <label>Target Agent</label>
                        <select id="cron-agent" class="config-input">
                            ${agentsCache.map(a => {
                                const name = a.name || a.agentId || a.id || 'unknown';
                                return `<option value="${escapeHtml(name)}">${escapeHtml(name)}</option>`;
                            }).join('')}
                        </select>
                    </div>
                    <div class="config-field">
                        <label>Enabled</label>
                        <div class="config-toggle">
                            <input type="checkbox" id="cron-enabled" checked>
                            <label for="cron-enabled">Active</label>
                        </div>
                    </div>
                    <div class="config-field full-width">
                        <label>Message / Task</label>
                        <textarea id="cron-message" class="config-input" rows="3" placeholder="Message to send to the agent on each run..."></textarea>
                    </div>
                </div>
                <div class="form-actions">
                    <button class="btn btn-secondary" id="btn-cancel-cron">Cancel</button>
                    <button class="btn btn-primary" id="btn-submit-cron">Create Job</button>
                </div>
            </div>`;

        body.insertAdjacentHTML('afterbegin', formHtml);

        $('#btn-cancel-cron').addEventListener('click', () => {
            body.querySelector('.cron-form')?.remove();
        });
        $('#btn-submit-cron').addEventListener('click', () => {
            appendSystemMessage('Cron API not yet available — job was not created.', 'error');
        });
        const cronEnabledCb = $('#cron-enabled');
        if (cronEnabledCb) cronEnabledCb.addEventListener('change', () => {
            const lbl = cronEnabledCb.parentElement.querySelector('label');
            if (lbl) lbl.textContent = cronEnabledCb.checked ? 'Active' : 'Disabled';
        });
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
            connectActivityWs();
            elActivityFeed.classList.remove('collapsed');
        } else {
            disconnectActivityWs();
            elActivityFeed.classList.add('collapsed');
        }
    }

    // =========================================================================
    // Activity WebSocket (separate connection)
    // =========================================================================

    function connectActivityWs() {
        if (activityWs && (activityWs.readyState === WebSocket.OPEN || activityWs.readyState === WebSocket.CONNECTING)) return;
        const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
        const url = `${proto}//${location.host}/ws/activity`;
        activityWs = new WebSocket(url);

        activityWs.onopen = () => {
            console.log('Activity WebSocket connected');
        };

        activityWs.onmessage = (event) => {
            try {
                const msg = JSON.parse(event.data);
                if (msg.type === 'activity' || msg.eventType) {
                    handleActivityEvent(msg);
                }
            } catch (e) {
                console.error('Activity WS parse error:', e);
            }
        };

        activityWs.onclose = () => {
            if (isActivitySubscribed) {
                activityReconnectTimer = setTimeout(connectActivityWs, 5000);
            }
        };

        activityWs.onerror = () => {};
    }

    function disconnectActivityWs() {
        if (activityReconnectTimer) { clearTimeout(activityReconnectTimer); activityReconnectTimer = null; }
        if (activityWs) { activityWs.onclose = null; activityWs.close(); activityWs = null; }
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
        // Cron sidebar header opens cron view
        $$('.section-header[data-view]').forEach(header => {
            header.addEventListener('click', () => {
                const view = header.dataset.view;
                if (view === 'cron') openCronView();
            });
        });
    }

    // =========================================================================
    // Event listeners
    // =========================================================================

    function initEventListeners() {
        elBtnSend.addEventListener('click', sendMessage);
        elBtnAbort.addEventListener('click', abortRequest);

        elChatInput.addEventListener('keydown', (e) => {
            if (isCommandPaletteVisible()) {
                if (e.key === 'ArrowDown') { e.preventDefault(); navigateCommandPalette(1); return; }
                if (e.key === 'ArrowUp') { e.preventDefault(); navigateCommandPalette(-1); return; }
                if (e.key === 'Tab' || (e.key === 'Enter' && !e.shiftKey)) { e.preventDefault(); acceptCommandPalette(); return; }
                if (e.key === 'Escape') { e.preventDefault(); hideCommandPalette(); return; }
            }
            if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); sendMessage(); }
        });
        elChatInput.addEventListener('input', () => {
            autoResize(elChatInput);
            updateSendButtonState();
            const text = elChatInput.value;
            if (text.startsWith('/') && !text.includes(' ')) {
                showCommandPalette(text);
            } else {
                hideCommandPalette();
            }
        });

        elAgentSelect.addEventListener('change', () => {
            const newAgent = elAgentSelect.value;
            const hasMessages = elChatMessages.children.length > 0;
            if (hasMessages && currentSessionId && currentAgentId && newAgent !== currentAgentId) {
                showConfirm(
                    `Switch to agent "${newAgent}"? This will start a new session.`,
                    'Switch Agent',
                    () => { elAgentSelect.value = newAgent; startNewChat(); },
                    'Switch'
                );
                elAgentSelect.value = currentAgentId;
                return;
            }
            startNewChat();
        });

        elToggleTools.addEventListener('change', toggleToolVisibility);
        elToggleThinking.addEventListener('change', toggleThinkingVisibility);
        elToggleActivity.addEventListener('change', toggleActivity);

        $('#btn-refresh-sessions').addEventListener('click', (e) => { e.stopPropagation(); loadSessions(); });
        $('#btn-refresh-channels').addEventListener('click', (e) => { e.stopPropagation(); loadChannels(); });
        $('#btn-refresh-extensions').addEventListener('click', (e) => { e.stopPropagation(); loadExtensions(); });
        $('#btn-refresh-agents').addEventListener('click', (e) => { e.stopPropagation(); loadAgents(); });

        $('#btn-stop-gateway').addEventListener('click', () => {
            showConfirm(
                'Stop the gateway? It will be restarted by the management process.',
                'Stop Gateway',
                async () => {
                    try {
                        await fetch(`${API_BASE}/gateway/shutdown`, { method: 'POST' });
                    } catch {}
                    appendSystemMessage('Gateway shutdown initiated.');
                },
                'Stop'
            );
        });

        // Delegated click handlers for dynamic chat content
        elChatMessages.addEventListener('click', (e) => {
            // Copy message button
            const copyBtn = e.target.closest('.btn-copy-msg');
            if (copyBtn) {
                const msgEl = copyBtn.closest('.message');
                if (msgEl) copyMessageContent(msgEl, copyBtn);
                return;
            }

            const toggle = e.target.closest('.thinking-toggle');
            if (toggle) {
                const block = toggle.closest('.thinking-block');
                if (block) {
                    block.classList.toggle('collapsed');
                    const expanded = !block.classList.contains('collapsed');
                    toggle.setAttribute('aria-expanded', expanded);
                    const chevron = block.querySelector('.thinking-chevron');
                    if (chevron) chevron.textContent = expanded ? '▾' : '▸';
                }
                return;
            }

            const toolCall = e.target.closest('.tool-call');
            if (toolCall) {
                toolCall.classList.toggle('expanded');
                return;
            }
        });

        // Scroll-to-bottom button
        elScrollBottom.addEventListener('click', () => scrollToBottom(true));
        elChatMessages.addEventListener('scroll', updateScrollButton);

        // Reconnect button
        elBtnReconnect.addEventListener('click', manualReconnect);

        // Session ID copy
        $('#btn-copy-session-id').addEventListener('click', copySessionId);

        // Activity filters
        elActivityFilterAgent.addEventListener('change', applyActivityFilters);
        elActivityFilterType.addEventListener('change', applyActivityFilters);

        // Tool modal
        elModalClose.addEventListener('click', closeToolModal);
        elModalOverlay.addEventListener('click', closeToolModal);

        // Agent form modal
        $('#btn-add-agent').addEventListener('click', (e) => { e.stopPropagation(); openAddAgentForm(); });
        elAgentFormModal.querySelector('.agent-form-close').addEventListener('click', closeAgentForm);
        elAgentFormModal.querySelector('.agent-form-overlay').addEventListener('click', closeAgentForm);

        // Debug info modal
        elDebugModal.querySelector('.debug-modal-close').addEventListener('click', closeDebugModal);
        elDebugModal.querySelector('.debug-modal-overlay').addEventListener('click', closeDebugModal);

        $('#btn-cancel-agent').addEventListener('click', closeAgentForm);
        $('#btn-save-agent').addEventListener('click', saveAgent);

        // Cron view
        $('#btn-add-cron').addEventListener('click', showAddCronForm);

        $('#form-agent-provider').addEventListener('change', () => { loadModelsForProvider($('#form-agent-provider').value); });
        $('#form-agent-temperature-enabled').addEventListener('change', (e) => { $('#form-agent-temperature').disabled = !e.target.checked; });
        $('#form-agent-max-tokens-enabled').addEventListener('change', (e) => { $('#form-agent-max-tokens').disabled = !e.target.checked; });

        // Model selector in chat header
        elModelSelect.addEventListener('change', handleModelChange);

        // Confirm dialog
        $('#btn-confirm-ok').addEventListener('click', () => { if (confirmCallback) confirmCallback(); closeConfirm(); });
        $('#btn-confirm-cancel').addEventListener('click', closeConfirm);
        elConfirmDialog.querySelector('.confirm-overlay').addEventListener('click', closeConfirm);

        // Send mode toggle (steer vs follow-up)
        elBtnSendMode.addEventListener('click', () => {
            sendModeFollowUp = !sendModeFollowUp;
            updateSendButtonState();
        });

        // Mobile sidebar toggle
        elSidebarToggle.addEventListener('click', toggleSidebar);
        elSidebarOverlay.addEventListener('click', closeSidebar);

        // Global keyboard shortcuts
        document.addEventListener('keydown', (e) => {
            // Ctrl+K / Cmd+K to open command palette
            if ((e.ctrlKey || e.metaKey) && e.key === 'k') {
                e.preventDefault();
                if (!elChatView.classList.contains('hidden')) {
                    elChatInput.focus();
                    elChatInput.value = '/';
                    showCommandPalette('/');
                }
                return;
            }
            if (e.key === 'Escape') {
                if (isCommandPaletteVisible()) { hideCommandPalette(); return; }
                if (!elAgentConfigView.classList.contains('hidden')) {
                    // Escape from agent config goes back to welcome
                    showView('welcome-screen');
                    return;
                }
                if (!elCronView.classList.contains('hidden')) {
                    showView('welcome-screen');
                    return;
                }
                if (!elToolModal.classList.contains('hidden')) { closeToolModal(); return; }
                if (!elDebugModal.classList.contains('hidden')) { closeDebugModal(); return; }
                if (!elAgentFormModal.classList.contains('hidden')) { closeAgentForm(); return; }
                if (!elConfirmDialog.classList.contains('hidden')) { closeConfirm(); return; }
                if (isStreaming) { abortRequest(); return; }
            }
        });
    }

    // =========================================================================
    // Copy message content
    // =========================================================================

    function copyMessageContent(msgEl, btn) {
        const raw = msgEl.dataset.rawContent;
        const text = raw || msgEl.querySelector('.msg-content')?.textContent || '';
        navigator.clipboard.writeText(text).then(() => {
            btn.textContent = '✅';
            btn.classList.add('copied');
            setTimeout(() => { btn.textContent = '📋'; btn.classList.remove('copied'); }, 1200);
        }).catch(() => {
            const ta = document.createElement('textarea');
            ta.value = text;
            ta.style.cssText = 'position:fixed;opacity:0';
            document.body.appendChild(ta);
            ta.select();
            document.execCommand('copy');
            document.body.removeChild(ta);
            btn.textContent = '✅';
            setTimeout(() => { btn.textContent = '📋'; }, 1200);
        });
    }

    // =========================================================================
    // Mobile sidebar
    // =========================================================================

    function toggleSidebar() {
        const isCollapsed = elSidebar.classList.toggle('collapsed');
        elSidebarOverlay.classList.toggle('hidden', isCollapsed);
        elSidebarToggle.setAttribute('aria-expanded', !isCollapsed);
    }

    function closeSidebar() {
        elSidebar.classList.add('collapsed');
        elSidebarOverlay.classList.add('hidden');
        elSidebarToggle.setAttribute('aria-expanded', 'false');
    }

    // =========================================================================
    // Initialization
    // =========================================================================

    function init() {
        initMarkdown();
        initSectionToggles();
        initEventListeners();
        loadSessions();
        loadChannels();
        loadExtensions();
        loadAgents();
        scheduleChannelsRefresh();
        startHealthCheck();
        setStatus('online');
        updateSendButtonState();
        // Initialize SignalR (replaces connectWebSocket)
        initSignalR();
        // Collapse sidebar on mobile by default
        if (window.innerWidth <= 768) {
            elSidebar.classList.add('collapsed');
            elSidebarOverlay.classList.add('hidden');
        }
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();


