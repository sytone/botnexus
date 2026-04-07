// BotNexus WebUI — connects to the Gateway via WebSocket and REST APIs
(function () {
    'use strict';

    // --- Configuration ---
    const API_BASE = '/api';
    const WS_PATH = '/ws';
    const RECONNECT_BASE_MS = 1000;
    const RECONNECT_MAX_MS = 30000;
    const RECONNECT_MAX_ATTEMPTS = 10;
    const PING_INTERVAL_MS = 30000;
    const RESPONSE_TIMEOUT_MS = 30000;
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
    let responseTimeoutTimer = null;
    let steerIndicatorTimer = null;
    let isStreaming = false;
    let hasReceivedResponse = false;
    let isWsConnecting = false;
    let isRestRequestInFlight = false;
    let shouldReconnect = true;
    let connectionHadOpen = false;
    let currentWsUrl = '';
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
    /** @type {number} */
    let toolCallDepth = 0;
    /** @type {boolean} */
    let sendModeFollowUp = false;
    /** @type {WebSocket|null} */
    let activityWs = null;
    let activityReconnectTimer = null;
    let userScrolledUp = false;
    let lastSequenceId = 0;
    let sessionKey = null;
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
    const elProcessingStage = $('#processing-stage');
    const elProcessingToolCount = $('#processing-tool-count');
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
        if (isStreaming && ws && ws.readyState === WebSocket.OPEN) {
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
        elProcessingStage.innerHTML = `<span aria-hidden="true">${icon || '⏳'}</span> ${escapeHtml(stage)}`;
        updateProcessingToolCount();
        elProcessingStatus.classList.remove('hidden');
    }

    function updateProcessingToolCount() {
        const runningCount = Object.values(activeToolCalls).filter(t => t.status === 'running').length;
        if (runningCount > 0) {
            elProcessingToolCount.textContent = `🔧 ${runningCount} tool${runningCount > 1 ? 's' : ''} active`;
        } else if (activeToolCount > 0) {
            elProcessingToolCount.textContent = `🔧 ${activeToolCount} tool${activeToolCount > 1 ? 's' : ''} used`;
        } else {
            elProcessingToolCount.textContent = '';
        }
    }

    function hideProcessingStatus() {
        elProcessingStatus.classList.add('hidden');
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

    async function checkGatewayHealth() {
        try {
            const response = await fetch('/health');
            const wasHealthy = gatewayHealthy;
            gatewayHealthy = response.ok;
            
            // Update status if not connected via WebSocket
            if (!ws || ws.readyState !== WebSocket.OPEN) {
                setStatus(gatewayHealthy ? 'online' : 'disconnected');
            }
            
            // If health state changed, show banner
            if (wasHealthy !== gatewayHealthy) {
                if (!gatewayHealthy) {
                    showConnectionBanner('⚠️ Gateway offline', 'warning');
                } else if (!currentAgentId) {
                    hideConnectionBanner();
                }
            }
        } catch (e) {
            gatewayHealthy = false;
            if (!ws || ws.readyState !== WebSocket.OPEN) {
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
    // WebSocket connection
    // =========================================================================

    function connectWebSocket() {
        if (ws && (ws.readyState === WebSocket.OPEN || ws.readyState === WebSocket.CONNECTING)) return;
        if (!currentAgentId) return;

        isWsConnecting = true;
        setStatus(reconnectAttempts > 0 ? 'reconnecting' : 'connecting');
        showConnectionBanner(reconnectAttempts > 0 ? '⚠️ Connection lost. Reconnecting...' : 'Connecting...', 'warning');
        const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
        let url = `${proto}//${location.host}${WS_PATH}?agent=${encodeURIComponent(currentAgentId)}`;
        if (currentSessionId) url += `&session=${encodeURIComponent(currentSessionId)}`;
        currentWsUrl = url;

        ws = new WebSocket(url);

        ws.onopen = () => {
            isWsConnecting = false;
            connectionHadOpen = true;
            setStatus('connected');
            const wasReconnect = reconnectAttempts > 0;
            reconnectAttempts = 0;
            startPing();
            if (wasReconnect) {
                loadChannels();
                loadExtensions();
                reloadCurrentSessionHistory().then((msgCount) => {
                    const countStr = msgCount > 0 ? ` Loaded ${msgCount} previous messages.` : '';
                    showConnectionBanner(`✅ Reconnected to gateway.${countStr}`, 'success');
                    setTimeout(() => hideConnectionBanner(), 3000);
                });
            } else {
                hideConnectionBanner();
            }
        };

        ws.onclose = () => {
            isWsConnecting = false;
            setStatus('disconnected');
            connectionId = null;
            stopPing();
            if (!shouldReconnect) return;
            if (!connectionHadOpen && reconnectAttempts === 0) {
                showConnectionBanner(`❌ Cannot connect to Gateway at ${currentWsUrl}. Check that the server is running.`, 'error', true);
            } else {
                showConnectionBanner('⚠️ Connection lost. Reconnecting...', 'warning');
            }
            scheduleReconnect();
        };

        ws.onerror = () => {
            setStatus('disconnected');
            if (!connectionHadOpen && reconnectAttempts === 0) {
                showConnectionBanner(`❌ Cannot connect to Gateway at ${currentWsUrl}. Check that the server is running.`, 'error');
            }
        };

        ws.onmessage = (event) => {
            try { handleWsMessage(JSON.parse(event.data)); }
            catch (e) { console.error('Failed to parse WS message:', e); }
        };
    }

    function disconnectWebSocket() {
        shouldReconnect = false;
        stopPing();
        if (reconnectTimer) { clearTimeout(reconnectTimer); reconnectTimer = null; }
        reconnectAttempts = 0;
        if (ws) { ws.onclose = null; ws.close(); ws = null; }
        isWsConnecting = false;
        setStatus('disconnected');
        connectionId = null;
        lastSequenceId = 0;
        sessionKey = null;
        hideConnectionBanner();
        setTimeout(() => { shouldReconnect = true; }, 0);
    }

    function scheduleReconnect() {
        if (reconnectTimer) return;
        if (reconnectAttempts >= RECONNECT_MAX_ATTEMPTS) {
            setStatus('disconnected');
            showConnectionBanner('❌ Unable to reconnect after multiple attempts. Check Gateway health and reconnect manually.', 'error', true);
            return;
        }

        const delay = Math.min(RECONNECT_BASE_MS * Math.pow(2, reconnectAttempts), RECONNECT_MAX_MS);
        reconnectAttempts++;
        setStatus('reconnecting');
        showConnectionBanner(`⚠️ Connection lost. Reconnecting in ${Math.round(delay / 1000)}s... (attempt ${reconnectAttempts}/${RECONNECT_MAX_ATTEMPTS})`, 'warning');
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

    // =========================================================================
    // WebSocket message handler
    // =========================================================================

    function handleWsMessage(msg) {
        if (msg.sequenceId !== undefined) lastSequenceId = msg.sequenceId;

        switch (msg.type) {
            case 'connected':
                connectionId = msg.connectionId;
                if (msg.sessionId) {
                    currentSessionId = msg.sessionId;
                    updateSessionIdDisplay();
                }
                if (msg.sessionKey) sessionKey = msg.sessionKey;
                break;
            case 'message_start':
                activeMessageId = msg.messageId;
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
                break;
            case 'content_delta':
                removeStreamingIndicator();
                markResponseReceived();
                autoCollapseThinking();
                showProcessingStatus('Writing response...', '✍️');
                appendDelta(msg.delta);
                break;
            case 'thinking_delta':
                showProcessingStatus('Thinking...', '💭');
                handleThinkingDelta(msg);
                break;
            case 'tool_start':
                handleToolStart(msg);
                showProcessingStatus(`Using tool: ${msg.toolName || 'tool'}`, '🔧');
                trackActivity('tool', currentAgentId, `🔧 ${msg.toolName || 'tool'} started`);
                break;
            case 'tool_end':
                handleToolEnd(msg);
                // After tool ends, update stage based on remaining active tools
                const remainingTools = Object.values(activeToolCalls).filter(t => t.status === 'running');
                if (remainingTools.length > 0) {
                    showProcessingStatus(`Using tool: ${remainingTools[0].toolName}`, '🔧');
                } else {
                    showProcessingStatus('Processing...', '⏳');
                }
                break;
            case 'message_end':
                markResponseReceived();
                trackActivity('response', currentAgentId, 'Response complete');
                hideProcessingStatus();
                finalizeMessage(msg);
                break;
            case 'error':
                markResponseReceived();
                trackActivity('error', currentAgentId, msg.message || 'Error');
                hideProcessingStatus();
                handleError(msg);
                break;
            case 'session_reset':
                disconnectWebSocket();
                currentSessionId = null;
                updateSessionIdDisplay();
                currentAgentId = elAgentSelect.value || currentAgentId;
                if (currentAgentId) connectWebSocket();
                loadSessions();
                break;
            case 'reconnect_ack':
                if (msg.sessionKey) sessionKey = msg.sessionKey;
                if (msg.lastSeqId !== undefined) lastSequenceId = msg.lastSeqId;
                break;
            case 'pong':
                break;
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
        updateQueueDisplay();
    }

    function resetQueue() {
        messageQueueCount = 0;
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
        reconnectAttempts = 0;
        connectionHadOpen = false;
        hideConnectionBanner();
        if (currentAgentId) connectWebSocket();
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
                <button class="btn-copy-msg" title="Copy message" aria-label="Copy message">📋</button>
            </div>
            <div class="msg-content">${contentHtml}</div>
        `;
        div.dataset.rawContent = content;
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
        const previousSessionId = currentSessionId;
        const canResetViaWebSocket = !!(previousSessionId && ws && ws.readyState === WebSocket.OPEN);

        clearChatForSessionReset();
        appendSystemMessage('Session reset. System prompt regenerated.');

        if (canResetViaWebSocket) {
            currentSessionId = null;
            updateSessionIdDisplay();
            sendWs({ type: commandType });
            loadSessions();
            return;
        }

        if (previousSessionId) {
            try {
                await fetch(`${API_BASE}/sessions/${encodeURIComponent(previousSessionId)}`, { method: 'DELETE' });
            } catch (e) {
                console.warn('Failed to delete previous session during reset:', e);
            }
        }

        disconnectWebSocket();
        currentSessionId = null;
        currentAgentId = elAgentSelect.value || currentAgentId;
        if (currentAgentId) connectWebSocket();
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
        elChatTitle.textContent = `${elAgentSelect.value || 'New Chat'} — WebSocket`;        elChatMeta.textContent = `Agent: ${elAgentSelect.value || 'default'} · Session will be created on first message`;
        elSessionIdDisplay.classList.add('hidden');
        elAgentSelect.disabled = false;
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

    function sendMessage() {
        const text = elChatInput.value.trim();
        if (!text) return;

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

        if (isStreaming && ws && ws.readyState === WebSocket.OPEN) {
            if (sendModeFollowUp) {
                sendWs({ type: 'follow_up', content: text });
                showFollowUpIndicator();
                appendSystemMessage(`📨 Follow-up queued: ${text}`);
                trackActivity('message', currentAgentId, `Follow-up: ${text.substring(0, 60)}`);
                incrementQueue();
            } else {
                sendWs({ type: 'steer', content: text });
                showSteerIndicator();
                appendSystemMessage(`🧭 Steering: ${text}`);
                trackActivity('message', currentAgentId, `Steer: ${text.substring(0, 60)}`);
            }
            return;
        }

        appendChatMessage('user', text);
        trackActivity('message', currentAgentId, text.substring(0, 60));
        setSendingState(true);
        isStreaming = true;
        incrementQueue();
        startResponseTimeout();

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
            markResponseReceived();
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
                appendErrorMessage(`❌ ${res.status} — ${await res.text()}`);
            }
        } catch (e) {
            removeStreamingIndicator();
            appendErrorMessage(`❌ Connection error: ${e.message}`);
        } finally {
            isStreaming = false;
            clearResponseTimeout();
            setSendingState(false);
            removeStreamingIndicator();
        }
    }

    function abortRequest() {
        sendWs({ type: 'abort' });
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
        disconnectWebSocket();
        currentSessionId = null;
        activeMessageId = null;
        activeToolCalls = {};
        activeToolCount = 0;
        toolCallDepth = 0;
        thinkingBuffer = '';
        clearResponseTimeout();
        resetQueue();

        elWelcome.classList.add('hidden');
        elChatView.classList.remove('hidden');
        elChatTitle.textContent = `${elAgentSelect.value || 'New Chat'} — WebSocket`;
        elChatMeta.textContent = `Agent: ${elAgentSelect.value || 'default'} · Session will be created on first message`;
        elChatMessages.innerHTML = '';
        elSessionIdDisplay.classList.add('hidden');
        setSendingState(false);
        elAgentSelect.disabled = false;
        elSessionsList.querySelectorAll('.list-item').forEach(el => el.classList.remove('active'));

        currentAgentId = elAgentSelect.value || null;
        if (currentAgentId) {
            connectWebSocket();
            loadChatHeaderModels();
        }
        else updateSendButtonState();
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
            const msgCount = (s.history && s.history.length) || 0;
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
            },
            'Delete'
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
        elChatTitle.textContent = agentId ? `${agentId} — ${session?.channelType || 'WebSocket'}` : 'Chat';
        elChatMessages.innerHTML = '<div class="loading">Loading messages...</div>';
        setSendingState(false);
        updateSessionIdDisplay();

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
        updateSendButtonState();
        loadChatHeaderModels();
    }

    // =========================================================================
    // Channels
    // =========================================================================

    let channelsRefreshTimer = null;
    const CHANNELS_REFRESH_MS = 30000;

    function channelEmoji(name) {
        const map = { websocket: '🌐', telegram: '✈️', discord: '🎮', slack: '💼', tui: '🖥️' };
        return map[(name || '').toLowerCase()] || '📡';
    }

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
            el.setAttribute('role', 'listitem');
            const dotClass = ch.isRunning ? 'running' : 'stopped';
            el.innerHTML = `
                <div class="list-item-row">
                    <span class="item-title">
                        <span class="channel-status-dot ${dotClass}" aria-hidden="true"></span>
                        ${channelEmoji(ch.name)} ${escapeHtml(ch.displayName || ch.name)}
                    </span>
                    <span class="item-meta" style="font-size:0.68rem;">${ch.isRunning ? 'running' : 'stopped'}</span>
                </div>
                <div class="channel-caps">${buildCapabilityIcons(ch)}</div>
            `;
            elChannelsList.appendChild(el);
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
            el.addEventListener('click', () => { elAgentSelect.value = name; currentAgentId = name; });
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
    }

    // =========================================================================
    // Event listeners
    // =========================================================================

    function initEventListeners() {
        $('#btn-new-chat').addEventListener('click', startNewChat);
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
                    () => {
                        currentAgentId = newAgent;
                        startNewChat();
                    },
                    'Switch'
                );
                elAgentSelect.value = currentAgentId;
                return;
            }
            currentAgentId = newAgent;
            if (!currentSessionId && ws) { disconnectWebSocket(); connectWebSocket(); }
            updateSendButtonState();
            if (currentAgentId) loadChatHeaderModels();
        });

        elToggleTools.addEventListener('change', toggleToolVisibility);
        elToggleThinking.addEventListener('change', toggleThinkingVisibility);
        elToggleActivity.addEventListener('change', toggleActivity);

        $('#btn-refresh-sessions').addEventListener('click', (e) => { e.stopPropagation(); loadSessions(); });
        $('#btn-refresh-channels').addEventListener('click', (e) => { e.stopPropagation(); loadChannels(); });
        $('#btn-refresh-extensions').addEventListener('click', (e) => { e.stopPropagation(); loadExtensions(); });
        $('#btn-refresh-agents').addEventListener('click', (e) => { e.stopPropagation(); loadAgents(); });

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
        $('#btn-cancel-agent').addEventListener('click', closeAgentForm);
        $('#btn-save-agent').addEventListener('click', saveAgent);
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
                if (!elToolModal.classList.contains('hidden')) { closeToolModal(); return; }
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
