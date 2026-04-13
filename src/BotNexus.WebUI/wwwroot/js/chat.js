// BotNexus WebUI — Chat canvas, message rendering, commands, sub-agents

import {
    API_BASE, fetchJson, normalizeChannelKey, toHubChannelType, channelDisplayName,
    debugLog
} from './api.js';
import {
    dom, $, escapeHtml, formatTime, relativeTime, renderMarkdown, scrollToBottom,
    autoResize, incrementNewMessageCount, resetNewMessageCount,
    renderProcessingStatus, showSteerIndicator, showFollowUpIndicator,
    showView, showConfirm, closeSidebar, setBatchRenderingState
} from './ui.js';
import {
    storeManager, getCurrentSessionId, getCurrentAgentId, getStreamState,
    isCurrentSessionStreaming, setCurrentChannelType, getCurrentChannelType
} from './session-store.js';
import { hubInvoke, getConnection } from './hub.js';
import { loadSessions, trackActivity } from './sidebar.js';
import { activeSubAgents } from './events.js';
import { getShowTools, setShowTools, getShowThinking, setShowThinking } from './storage.js';

// ── Module state ────────────────────────────────────────────────────

// Toggle state is per-channel (stored on each SessionStore).
// These module-level vars mirror the active store for fast access during rendering.
let showTools = true;
let showThinking = true;

// Sync module-level toggle vars from the active session store
export function syncTogglesFromActiveStore() {
    const store = storeManager.activeStore;
    if (store) {
        showTools = store.showTools;
        showThinking = store.showThinking;
    }
    // Sync checkbox DOM
    if (dom.toggleTools) dom.toggleTools.checked = showTools;
    if (dom.toggleThinking) dom.toggleThinking.checked = showThinking;
}
let sendModeFollowUp = false;
let messageQueueCount = 0;
let pendingQueuedMessages = [];
let commandPaletteIndex = -1;
let responseTimeoutTimer = null;
let hasReceivedResponse = false;
let toolElapsedTimer = null;
const _scrollbackCleanups = new Map();
const RESPONSE_TIMEOUT_MS = 30000;

// ── Event handler entry points (called from events.js) ──────────────

export function onMessageStart(evt, sid) {
    markResponseReceived();
    removeStreamingIndicator();
    showStreamingIndicator(sid);
    showProcessingStatus('Agent is responding...', '🤖', sid);
    dom.btnAbort.classList.remove('hidden');
    updateSendButtonState();
}

export function onContentDelta(text) {
    markResponseReceived();
    removeStreamingIndicator();
    appendDelta(text);
}

export function onThinkingDelta(text) {
    markResponseReceived();
    handleThinkingDelta(text);
}

export function onToolStart(evt) {
    markResponseReceived();
    handleToolStart(evt);
}

export function onToolEnd(evt) {
    handleToolEnd(evt);
}

export function onMessageEnd(evt) {
    finalizeMessage(evt);
}

export function onError(evt) {
    handleError(evt);
}

// ── Session ID display ──────────────────────────────────────────────

export function updateSessionIdDisplay() {
    const sessionId = getCurrentSessionId();
    if (sessionId) {
        const truncated = sessionId.length > 12
            ? sessionId.substring(0, 12) + '...'
            : sessionId;
        dom.sessionIdText.textContent = `Session: ${truncated}`;
        dom.sessionIdText.title = sessionId;
        dom.sessionIdDisplay.classList.remove('hidden');
    } else if (getCurrentAgentId()) {
        dom.sessionIdText.textContent = 'Session: —';
        dom.sessionIdText.title = '';
        dom.sessionIdDisplay.classList.remove('hidden');
    } else {
        dom.sessionIdDisplay.classList.add('hidden');
    }
}

export function copySessionId() {
    if (!getCurrentSessionId()) return;
    navigator.clipboard.writeText(getCurrentSessionId()).then(() => {
        const btn = $('#btn-copy-session-id');
        btn.textContent = '✅';
        btn.classList.add('copy-flash');
        setTimeout(() => { btn.textContent = '📋'; btn.classList.remove('copy-flash'); }, 1200);
    }).catch(() => {
        const ta = document.createElement('textarea');
        ta.value = getCurrentSessionId();
        ta.style.cssText = 'position:fixed;opacity:0';
        document.body.appendChild(ta);
        ta.select();
        document.execCommand('copy');
        document.body.removeChild(ta);
    });
}

// ── Send button state ───────────────────────────────────────────────

export function updateSendButtonState() {
    const hasText = !!dom.chatInput.value.trim();
    const connection = getConnection();
    if (storeManager.isSwitchingView) {
        dom.chatInput.disabled = true;
        dom.btnSend.disabled = true;
        return;
    }
    if (storeManager.isRestRequestInFlight) {
        dom.btnSend.disabled = true;
        return;
    }
    dom.chatInput.disabled = false;
    if (isCurrentSessionStreaming() && connection?.state === signalR.HubConnectionState.Connected) {
        dom.btnSend.disabled = !hasText;
        if (sendModeFollowUp) {
            dom.btnSend.textContent = '📨 Follow-up';
            dom.btnSend.classList.remove('btn-steer');
            dom.btnSend.classList.add('btn-followup');
            dom.chatInput.placeholder = 'Queue a follow-up message...';
        } else {
            dom.btnSend.textContent = '🧭 Steer';
            dom.btnSend.classList.add('btn-steer');
            dom.btnSend.classList.remove('btn-followup');
            dom.chatInput.placeholder = 'Steer the agent... (Enter to send)';
        }
        dom.btnSendMode.classList.remove('hidden');
        dom.btnSendMode.classList.toggle('followup-mode', sendModeFollowUp);
        return;
    }
    dom.btnSend.classList.remove('btn-steer', 'btn-followup');
    dom.btnSend.textContent = 'Send';
    dom.chatInput.placeholder = 'Type a message... (Enter to send, Shift+Enter for newline)';
    dom.btnSend.disabled = !hasText || !dom.chatView || dom.chatView.classList.contains('hidden');
    dom.btnSendMode.classList.add('hidden');
    sendModeFollowUp = false;
}

export function setSendingState(isSending) {
    storeManager.setRestRequestInFlight(isSending);
    dom.btnSend.classList.toggle('btn-sending', isSending);
    dom.btnSend.textContent = isSending ? 'Sending' : 'Send';
    updateSendButtonState();
}

export function toggleSendMode() {
    sendModeFollowUp = !sendModeFollowUp;
    updateSendButtonState();
}

// ── Response timeout ────────────────────────────────────────────────

function startResponseTimeout() {
    clearResponseTimeout();
    hasReceivedResponse = false;
    responseTimeoutTimer = setTimeout(() => {
        if (!hasReceivedResponse && isCurrentSessionStreaming()) {
            appendSystemMessage('⏳ Agent is taking longer than expected...', 'warning');
        }
    }, RESPONSE_TIMEOUT_MS);
}

function markResponseReceived() {
    hasReceivedResponse = true;
    clearResponseTimeout();
}

function clearResponseTimeout() {
    if (responseTimeoutTimer) { clearTimeout(responseTimeoutTimer); responseTimeoutTimer = null; }
}

// ── Processing status ───────────────────────────────────────────────

export function syncLoadingUiForActiveSession() {
    const activeId = getCurrentSessionId();
    const store = activeId ? storeManager.getStore(activeId) : null;
    if (!store) {
        renderProcessingStatus(false);
        dom.btnAbort.classList.add('hidden');
        dom.chatMessages.querySelectorAll('.streaming-indicator').forEach(el => el.remove());
        return;
    }
    const ss = store.streamState;
    renderProcessingStatus(ss.processingVisible, ss.processingStage, ss.processingIcon);
    dom.btnAbort.classList.toggle('hidden', !ss.isStreaming);
    if (!ss.showStreamingIndicator) {
        dom.chatMessages.querySelectorAll('.streaming-indicator').forEach(el => el.remove());
    }
}

function showProcessingStatus(stage, icon, sessionId = getCurrentSessionId()) {
    if (!sessionId) return;
    const ss = getStreamState(sessionId);
    ss.processingVisible = true;
    ss.processingStage = stage || 'Processing...';
    ss.processingIcon = icon || '⏳';
    if (sessionId !== getCurrentSessionId()) return;
    renderProcessingStatus(true, ss.processingStage, ss.processingIcon);
}

export function hideProcessingStatus(sessionId = getCurrentSessionId()) {
    if (sessionId) {
        const ss = getStreamState(sessionId);
        ss.processingVisible = false;
        ss.processingStage = '';
        ss.processingIcon = '⏳';
    }
    if (sessionId !== getCurrentSessionId()) return;
    renderProcessingStatus(false);
}

// ── Streaming indicator ─────────────────────────────────────────────

function showStreamingIndicator(sessionId = getCurrentSessionId()) {
    if (!sessionId) return;
    getStreamState(sessionId).showStreamingIndicator = true;
}

function hideStreamingIndicator(sessionId = getCurrentSessionId()) {
    if (sessionId) getStreamState(sessionId).showStreamingIndicator = false;
    if (sessionId !== getCurrentSessionId()) return;
    dom.chatMessages.querySelectorAll('.streaming-indicator').forEach(el => el.remove());
}

function removeStreamingIndicator() { hideStreamingIndicator(); }

// ── Queue management ────────────────────────────────────────────────

function incrementQueue() { messageQueueCount++; updateQueueDisplay(); }
function decrementQueue() {
    if (messageQueueCount > 0) messageQueueCount--;
    if (pendingQueuedMessages.length > 0) {
        const text = pendingQueuedMessages.shift();
        appendChatMessage('user', text);
    }
    updateQueueDisplay();
}
function resetQueue() { messageQueueCount = 0; pendingQueuedMessages = []; updateQueueDisplay(); }
function updateQueueDisplay() {
    if (messageQueueCount > 0) {
        dom.queueStatus.classList.remove('hidden');
        dom.queueCount.textContent = `${messageQueueCount} message${messageQueueCount > 1 ? 's' : ''} queued`;
    } else {
        dom.queueStatus.classList.add('hidden');
    }
}

// ── Thinking display ────────────────────────────────────────────────

function formatCharCount(len) {
    if (len < 1000) return `${len} chars`;
    return `${(len / 1000).toFixed(1)}k chars`;
}

function handleThinkingDelta(text) {
    if (!text) return;
    const ss = getStreamState(getCurrentSessionId());

    let thinkingEl = dom.chatMessages.querySelector('.thinking-block');
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
        dom.chatMessages.appendChild(thinkingEl);
    }
    thinkingEl.querySelector('.thinking-pre').textContent = ss.thinkingBuffer;
    thinkingEl.querySelector('.thinking-stats').textContent = formatCharCount(ss.thinkingBuffer.length);
    scrollToBottom();
}

function finalizeThinkingBlock() {
    const thinkingEl = dom.chatMessages.querySelector('.thinking-block');
    if (thinkingEl) {
        const charCount = getStreamState(getCurrentSessionId()).thinkingBuffer.length > 0
            ? ` (${formatCharCount(getStreamState(getCurrentSessionId()).thinkingBuffer.length)})` : '';
        thinkingEl.querySelector('.thinking-label').textContent = `Thought process${charCount}`;
        thinkingEl.querySelector('.thinking-stats').textContent = '';
        thinkingEl.classList.add('complete', 'collapsed');
        const toggle = thinkingEl.querySelector('.thinking-toggle');
        if (toggle) {
            toggle.setAttribute('aria-expanded', 'false');
            thinkingEl.querySelector('.thinking-chevron').textContent = '▸';
        }
    }
}

export function autoCollapseThinking() {
    const thinkingEl = dom.chatMessages.querySelector('.thinking-block:not(.complete)');
    if (thinkingEl && !thinkingEl.classList.contains('collapsed')) {
        thinkingEl.classList.add('collapsed');
        const toggle = thinkingEl.querySelector('.thinking-toggle');
        if (toggle) {
            toggle.setAttribute('aria-expanded', 'false');
            thinkingEl.querySelector('.thinking-chevron').textContent = '▸';
        }
    }
}

// ── Tool call handling ──────────────────────────────────────────────

function handleToolStart(msg) {
    const ss = getStreamState(getCurrentSessionId());
    const callId = msg.toolCallId || `tc-${Date.now()}`;
    ss.activeToolCount++;
    ss.toolStartTimes[callId] = Date.now();
    const depth = msg.depth || ss.toolCallDepth;
    ss.activeToolCalls[callId] = {
        toolName: msg.toolName || 'unknown', args: msg.toolArgs || '',
        result: '', status: 'running', depth
    };
    appendToolCall(callId, msg.toolName, 'running', msg.toolArgs, depth);
    startToolElapsedTimer();
}

function handleToolEnd(msg) {
    const ss = getStreamState(getCurrentSessionId());
    const callId = msg.toolCallId || 'unknown';
    const isError = msg.toolIsError === true;
    const status = isError ? 'error' : 'complete';
    if (ss.activeToolCalls[callId]) {
        ss.activeToolCalls[callId].result = msg.toolResult || '';
        ss.activeToolCalls[callId].status = status;
    }
    const elapsed = ss.toolStartTimes[callId] ? Math.round((Date.now() - ss.toolStartTimes[callId]) / 1000) : 0;
    delete ss.toolStartTimes[callId];
    updateToolCallStatus(callId, status, elapsed, msg.toolResult);
    if (isError) {
        trackActivity('error', getCurrentAgentId(), `🔧 ${msg.toolName || ss.activeToolCalls[callId]?.toolName || 'tool'} failed`);
    }
    // Skill-loaded notification
    const toolName = msg.toolName || ss.activeToolCalls[callId]?.toolName || '';
    if (toolName === 'skills' && !isError) {
        const result = msg.toolResult || '';
        const skillMatch = result.match(/^## Skill:\s*(.+)$/m);
        if (skillMatch) appendSystemMessage(`📚 Skill loaded: ${skillMatch[1].trim()}`);
    }
    if (Object.keys(ss.toolStartTimes).length === 0) stopToolElapsedTimer();
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
    dom.chatMessages.appendChild(div);
    scrollToBottom();
}

function updateToolCallStatus(callId, status, elapsed, result) {
    const el = dom.chatMessages.querySelector(`.tool-call[data-call-id="${callId}"]`);
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
    const resultCode = el.querySelector('.tool-result-code');
    if (resultCode && result !== undefined) {
        resultCode.textContent = typeof result === 'string' ? result : JSON.stringify(result, null, 2);
        if (!resultCode.textContent) resultCode.textContent = '(no result)';
    } else if (resultCode) {
        resultCode.textContent = status === 'error' ? '(error)' : '(no result)';
    }
    el.style.cursor = 'pointer';
}

function startToolElapsedTimer() {
    if (toolElapsedTimer) return;
    toolElapsedTimer = setInterval(() => {
        for (const [callId, startTime] of Object.entries(getStreamState(getCurrentSessionId()).toolStartTimes)) {
            const el = dom.chatMessages.querySelector(`.tool-call[data-call-id="${callId}"] .tool-elapsed`);
            if (el) el.textContent = `${Math.round((Date.now() - startTime) / 1000)}s`;
        }
    }, 1000);
}

function stopToolElapsedTimer() {
    if (toolElapsedTimer) { clearInterval(toolElapsedTimer); toolElapsedTimer = null; }
}

// ── Message finalization ────────────────────────────────────────────

function finalizeMessage(msg) {
    const ss = getStreamState(getCurrentSessionId());
    ss.isStreaming = false;
    ss.activeMessageId = null;
    clearResponseTimeout();
    dom.btnAbort.classList.add('hidden');
    removeStreamingIndicator();
    hideProcessingStatus();
    finalizeThinkingBlock();

    const streaming = dom.chatMessages.querySelector('.message.assistant.streaming');
    if (streaming) {
        streaming.classList.remove('streaming', 'message-streaming');
        const deltaEl = streaming.querySelector('.delta-content');
        if (deltaEl) {
            const rawText = deltaEl.textContent;
            streaming.dataset.rawContent = rawText;
            deltaEl.innerHTML = renderMarkdown(rawText);
        }
        const timeEl = streaming.querySelector('.msg-time');
        if (timeEl) timeEl.textContent = formatTime(new Date().toISOString());

        const footer = document.createElement('div');
        footer.className = 'msg-footer';
        const parts = [];
        if (ss.activeToolCount > 0) parts.push(`🔧 ${ss.activeToolCount} tool call${ss.activeToolCount > 1 ? 's' : ''}`);
        if (msg.usage) { const u = formatUsage(msg.usage); if (u) parts.push(u); }
        if (parts.length > 0) { footer.textContent = parts.join(' · '); streaming.appendChild(footer); }
    }

    ss.activeToolCalls = {};
    ss.activeToolCount = 0;
    ss.toolCallDepth = 0;
    ss.toolStartTimes = {};
    stopToolElapsedTimer();
    ss.thinkingBuffer = '';
    resetQueue();
    setSendingState(false);
    updateSendButtonState();
    updateSessionIdDisplay();
    loadSessions();
    incrementNewMessageCount();
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
    getStreamState(getCurrentSessionId()).isStreaming = false;
    clearResponseTimeout();
    stopToolElapsedTimer();
    dom.btnAbort.classList.add('hidden');
    removeStreamingIndicator();
    hideProcessingStatus();
    appendErrorMessage(`❌ ${msg.message || 'Unknown error'}${msg.code ? ` (${msg.code})` : ''}`);
    setSendingState(false);
    updateSendButtonState();
}

// ── Chat message rendering ──────────────────────────────────────────

function stripControlTags(text) {
    if (!text) return text;
    return text.replace(/\[\[\s*reply_to_current\s*\]\]/gi, '')
               .replace(/\[\[\s*reply_to:\s*\S+\s*\]\]/gi, '')
               .replace(/\[\[reply_to_current\]\]/g, '')
               .trim();
}

export function appendChatMessage(role, content, timestamp) {
    appendChatMessageTo(role, content, dom.chatMessages, timestamp);
}

function appendChatMessageTo(role, content, container, timestamp) {
    content = stripControlTags(content);
    if (!content || !content.trim()) return;
    const div = document.createElement('div');
    div.className = `message ${role}`;
    const timeStr = formatTime(timestamp || new Date().toISOString());
    const contentHtml = role === 'assistant' ? renderMarkdown(content) : escapeHtml(content);
    div.innerHTML = `
        <div class="msg-header">
            <span class="msg-role">${escapeHtml(role.toUpperCase())}</span>
            <span class="msg-time">${timeStr}</span>
            <button class="btn-copy-msg" title="Copy message" aria-label="Copy message">📋</button>
        </div>
        <div class="msg-content">${contentHtml}</div>
    `;
    div.dataset.rawContent = content;
    container.appendChild(div);
    if (container === dom.chatMessages) scrollToBottom();
}

export function appendSystemMessage(text, level) {
    const div = document.createElement('div');
    div.className = `message system-msg${level ? ' ' + level : ''}`;
    div.textContent = text;
    dom.chatMessages.appendChild(div);
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
    dom.chatMessages.appendChild(div);
    scrollToBottom();
}

function appendDelta(content) {
    if (!content) return;
    content = stripControlTags(content);
    if (!content) return;
    let streaming = dom.chatMessages.querySelector('.message.assistant.streaming');
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
        dom.chatMessages.appendChild(streaming);
    }
    streaming.querySelector('.delta-content').textContent += content;
    scrollToBottom();
}

// ── History rendering ───────────────────────────────────────────────

function renderHistoryEntry(entry) { renderHistoryEntryTo(entry, dom.chatMessages); }

function renderHistoryEntryTo(entry, container) {
    if (!entry) return;
    if ((entry.role === 'user' || entry.role === 'assistant') && entry.content && entry.content.trim()) {
        appendChatMessageTo(entry.role, entry.content, container, entry.timestamp);
    }
    if (entry.role === 'assistant' && entry.toolCalls && entry.toolCalls.length > 0) {
        for (const tc of entry.toolCalls) renderToolCallHistoryTo(tc, container);
    }
    if (entry.role === 'tool') renderToolCallHistoryTo(entry, container);
}

function parseToolNameFromContent(content) {
    if (!content) return null;
    const match = content.match(/Tool '([^']+)'/);
    return match ? match[1] : null;
}

function renderToolCallHistoryTo(tc, container) {
    const callId = `hist-${Date.now()}-${Math.random().toString(36).slice(2, 7)}`;
    const div = document.createElement('div');
    div.className = `message tool-call tool-complete${showTools ? '' : ' hidden'}`;
    div.dataset.callId = callId;
    const toolName = tc.toolName || tc.name || parseToolNameFromContent(tc.content) || 'unknown';
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
    getStreamState(getCurrentSessionId()).activeToolCalls[callId] = {
        toolName, args: argsStr, result: resultStr, status: 'complete'
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

function renderHistoryBatch(messages, sessionBoundaries, container) {
    container = container || dom.chatMessages;
    const boundaryMap = new Map();
    if (sessionBoundaries) {
        for (const b of sessionBoundaries) boundaryMap.set(b.insertBeforeIndex, b);
    }
    setBatchRenderingState(true);
    try {
        for (let i = 0; i < messages.length; i++) {
            const boundary = boundaryMap.get(i);
            if (boundary) container.appendChild(createSessionDividerEl(boundary.sessionId, boundary.startedAt));
            renderHistoryEntryTo(messages[i], container);
        }
    } finally {
        setBatchRenderingState(false);
        applyToggleState(container);
    }
}

// ── Tool modal ──────────────────────────────────────────────────────

export function openToolModal(toolData) {
    $('#modal-tool-name').textContent = toolData.toolName || 'unknown';
    $('#modal-tool-args').textContent = typeof toolData.args === 'string'
        ? toolData.args : JSON.stringify(toolData.args || {}, null, 2);
    $('#modal-tool-result').textContent = toolData.result || '(no result)';
    dom.toolModal.classList.remove('hidden');
}

export function closeToolModal() { dom.toolModal.classList.add('hidden'); }

// ── Command palette ─────────────────────────────────────────────────

const COMMANDS = [
    { name: '/help', description: 'Show available commands' },
    { name: '/new', description: 'Start a new chat session' },
    { name: '/reset', description: 'Clear chat and reset current session' },
    { name: '/status', description: 'Show gateway health status' },
    { name: '/agents', description: 'List available agents' },
];

export function showCommandPalette(filter) {
    const query = filter.toLowerCase();
    const matches = COMMANDS.filter(c => c.name.startsWith(query));
    if (matches.length === 0) { hideCommandPalette(); return; }
    commandPaletteIndex = 0;
    dom.commandPalette.innerHTML = '';
    for (let i = 0; i < matches.length; i++) {
        const el = document.createElement('div');
        el.className = 'command-item' + (i === 0 ? ' active' : '');
        el.setAttribute('role', 'option');
        el.dataset.index = i;
        el.innerHTML = `<span class="command-name">${escapeHtml(matches[i].name)}</span><span class="command-desc">${escapeHtml(matches[i].description)}</span>`;
        el.addEventListener('click', () => executeCommand(matches[i].name));
        dom.commandPalette.appendChild(el);
    }
    const hint = document.createElement('div');
    hint.className = 'command-palette-hint';
    hint.textContent = '↑↓ navigate · Tab or Enter to select · Esc to dismiss';
    dom.commandPalette.appendChild(hint);
    dom.commandPalette.classList.remove('hidden');
}

export function hideCommandPalette() {
    dom.commandPalette.classList.add('hidden');
    dom.commandPalette.innerHTML = '';
    commandPaletteIndex = -1;
}

export function isCommandPaletteVisible() {
    return !dom.commandPalette.classList.contains('hidden');
}

export function navigateCommandPalette(direction) {
    const items = dom.commandPalette.querySelectorAll('.command-item');
    if (items.length === 0) return;
    items[commandPaletteIndex]?.classList.remove('active');
    commandPaletteIndex = (commandPaletteIndex + direction + items.length) % items.length;
    items[commandPaletteIndex].classList.add('active');
    items[commandPaletteIndex].scrollIntoView({ block: 'nearest' });
}

export function acceptCommandPalette() {
    const active = dom.commandPalette.querySelector('.command-item.active .command-name');
    if (active) executeCommand(active.textContent);
}

function executeCommand(name) {
    hideCommandPalette();
    dom.chatInput.value = '';
    autoResize(dom.chatInput);
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

export async function executeReset(commandType = 'reset') {
    {
        const ss = getStreamState(getCurrentSessionId());
        ss.activeMessageId = null;
        ss.activeToolCalls = {};
        ss.activeToolCount = 0;
        ss.toolCallDepth = 0;
        ss.thinkingBuffer = '';
        ss.isStreaming = false;
    }
    clearResponseTimeout();
    resetQueue();
    removeStreamingIndicator();
    hideProcessingStatus();
    setSendingState(false);

    const connection = getConnection();
    if (commandType === 'reset' && getCurrentAgentId() && getCurrentSessionId() && connection?.state === signalR.HubConnectionState.Connected) {
        try { await hubInvoke('ResetSession', getCurrentAgentId(), getCurrentSessionId()); }
        catch (err) { console.warn('Failed to reset session via SignalR:', err); }
        appendSystemMessage('Session context reset. System prompt regenerated.');
    } else {
        const divider = document.createElement('div');
        divider.className = 'session-divider';
        const now = new Date();
        const dateStr = now.toLocaleDateString(undefined, { month: 'short', day: 'numeric', year: 'numeric' });
        const timeStr = now.toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' });
        divider.innerHTML = `<span class="session-divider-line"></span><span class="session-divider-label">New session started ${dateStr} at ${timeStr}</span><span class="session-divider-line"></span>`;
        dom.chatMessages.appendChild(divider);
        scrollToBottom();
        appendSystemMessage('New session started. Previous messages are still visible above.');
    }

    storeManager.setActiveView(null, getCurrentAgentId(), getCurrentChannelType());
    syncLoadingUiForActiveSession();
    updateSessionIdDisplay();
    storeManager.setSelectedAgent(dom.agentSelect.value || getCurrentAgentId());
    loadSessions();
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
    if (!agents || agents.length === 0) { appendCommandResult('🧠 Agents', '  No agents configured.'); return; }
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
    dom.chatMessages.appendChild(div);
    scrollToBottom();
}

// ── Chat actions ────────────────────────────────────────────────────

export async function sendMessage() {
    if (storeManager.isSwitchingView) {
        appendSystemMessage('Please wait for session switch to complete.', 'warning');
        return;
    }
    const activeStore = storeManager.activeStore;
    const activeAgentId = activeStore?.agentId || storeManager.activeAgentId;
    const activeSessionId = storeManager.activeViewId;
    if (!activeAgentId) return;
    const text = dom.chatInput.value.trim();
    if (!text) return;

    if (text.startsWith('/')) {
        const cmd = text.split(/\s/)[0].toLowerCase();
        const match = COMMANDS.find(c => c.name === cmd);
        if (match) {
            dom.chatInput.value = '';
            autoResize(dom.chatInput);
            updateSendButtonState();
            hideCommandPalette();
            executeCommand(match.name);
            return;
        }
    }

    dom.chatInput.value = '';
    autoResize(dom.chatInput);
    updateSendButtonState();

    const connection = getConnection();
    if (isCurrentSessionStreaming() && connection?.state === signalR.HubConnectionState.Connected) {
        if (!activeSessionId) {
            appendSystemMessage('Unable to send control message while session is switching.', 'warning');
            return;
        }
        if (sendModeFollowUp) {
            pendingQueuedMessages.push(text);
            incrementQueue();
            showFollowUpIndicator();
            trackActivity('message', activeAgentId, `Follow-up: ${text.substring(0, 60)}`);
            try { await hubInvoke('FollowUp', activeAgentId, activeSessionId, text); }
            catch (err) { appendSystemMessage(`Failed to queue: ${err.message}`, 'error'); }
        } else {
            appendSystemMessage(`🧭 Steering: ${text}`);
            showSteerIndicator();
            trackActivity('message', activeAgentId, `Steer: ${text.substring(0, 60)}`);
            try { await hubInvoke('Steer', activeAgentId, activeSessionId, text); }
            catch (err) { appendSystemMessage(`Failed to steer: ${err.message}`, 'error'); }
        }
        return;
    }

    appendChatMessage('user', text);
    trackActivity('message', activeAgentId, text.substring(0, 60));
    setSendingState(true);
    if (activeSessionId) getStreamState(activeSessionId).isStreaming = true;
    incrementQueue();
    startResponseTimeout();

    try {
        const channelType = toHubChannelType(activeStore?.channelType || getCurrentChannelType() || 'Web Chat');
        const result = await hubInvoke('SendMessage', activeAgentId, channelType, text);
        if (result?.sessionId) {
            const sessionChannelType = result.channelType || channelType;
            storeManager.getOrCreateStore(result.sessionId, {
                agentId: result.agentId || activeAgentId,
                channelType: sessionChannelType
            });
            storeManager.switchView(result.sessionId);
            updateSessionIdDisplay();
        }
    } catch (err) {
        appendSystemMessage(`Error: ${err.message}`, 'error');
        if (activeSessionId) getStreamState(activeSessionId).isStreaming = false;
        setSendingState(false);
    }
}

export async function abortRequest() {
    const connection = getConnection();
    if (getCurrentAgentId() && getCurrentSessionId() && connection?.state === signalR.HubConnectionState.Connected) {
        try { await hubInvoke('Abort', getCurrentAgentId(), getCurrentSessionId()); } catch {}
    }
    getStreamState(getCurrentSessionId()).isStreaming = false;
    clearResponseTimeout();
    stopToolElapsedTimer();
    dom.btnAbort.classList.add('hidden');
    removeStreamingIndicator();
    hideProcessingStatus();
    setSendingState(false);
    resetQueue();
    updateSendButtonState();
    appendSystemMessage('Request aborted.');
}

export function startNewChat() {
    const agentId = dom.agentSelect.value || null;
    if (!agentId) return;
    storeManager.setActiveView(null, agentId, 'Web Chat');
    syncLoadingUiForActiveSession();
    resetQueue();
    storeManager.setSelectedAgent(agentId);
    setCurrentChannelType('Web Chat');
    openAgentTimeline(agentId, 'Web Chat');
}

export async function deleteSession(sessionId) {
    showConfirm(
        `Delete session ${sessionId.substring(0, 8)}...? This cannot be undone.`,
        'Delete Session',
        async () => {
            try {
                const res = await fetch(`${API_BASE}/sessions/${encodeURIComponent(sessionId)}`, { method: 'DELETE' });
                if (res.ok || res.status === 204) {
                    if (getCurrentSessionId() === sessionId) {
                        storeManager.setActiveView(null, null, getCurrentChannelType());
                        syncLoadingUiForActiveSession();
                        storeManager.setSelectedAgent(null);
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

export async function openSession(sessionId, agentId) {
    const session = await fetchJson(`/sessions/${encodeURIComponent(sessionId)}`);
    const channelType = normalizeChannelKey(session?.channelType || 'web chat');
    await openAgentTimeline(agentId, channelType, sessionId);
}

// ── Agent timeline ──────────────────────────────────────────────────

export async function openAgentTimeline(agentId, channelType, targetSessionId = null) {
    storeManager.setSwitchingView(true);
    updateSendButtonState();

    if (storeManager.isRestRequestInFlight) setSendingState(false);
    resetQueue();
    clearResponseTimeout();
    stopToolElapsedTimer();
    resetNewMessageCount();

    dom.sessionsList.querySelectorAll('.list-item').forEach(el => {
        el.classList.toggle('active',
            el.dataset.agentId === agentId &&
            normalizeChannelKey(el.dataset.channelType) === normalizeChannelKey(channelType));
    });

    showView('chat-view');
    if (agentId) dom.agentSelect.value = agentId;
    dom.agentSelect.classList.add('hidden');
    storeManager.setSelectedAgent(agentId);
    setCurrentChannelType(channelType);

    // Save old view's DOM before clearing
    storeManager.snapshotActiveView();

    // Immediately clear display for the new agent — prevents stale data from the
    // previous agent's view bleeding into this one during async resolution.
    dom.chatMessages.innerHTML = '';
    dom.chatMeta.textContent = `Agent: ${agentId} · ${channelDisplayName(channelType)}`;
    dom.chatTitle.textContent = `${agentId} — ${channelDisplayName(channelType)}`;
    document.title = `${agentId} — ${channelDisplayName(channelType)} | BotNexus`;

    // Defer activeViewId — set to null until the real session resolves.
    // This ensures events from the old session don't render here.
    storeManager.setActiveView(null, agentId, channelType);
    updateSessionIdDisplay();
    syncTogglesFromActiveStore();
    syncLoadingUiForActiveSession();
    const hash = `#/agents/${encodeURIComponent(agentId)}/channels/${encodeURIComponent(toHubChannelType(channelType))}`;
    if (location.hash !== hash) history.pushState(null, '', hash);

    try {
        const existingStore = storeManager.findStoreForAgent(agentId, channelType);
        if (existingStore && existingStore.sessionId) {
            const warm = storeManager.switchView(existingStore.sessionId);
            const hasWarmContent = !!dom.chatMessages.querySelector('.history-sentinel');
            if (warm && hasWarmContent) {
                scrollToBottom();
                dom.chatInput.focus();
                updateSendButtonState();
                loadChatHeaderModels();
                fetchSubAgents();
                return;
            }
        }

        dom.chatMessages.innerHTML = '<div class="loading">Loading timeline...</div>';

        const historyChannelType = toHubChannelType(channelType);
        const data = await fetchJson(
            `/channels/${encodeURIComponent(historyChannelType)}/agents/${encodeURIComponent(agentId)}/history?limit=50`
        );

        if (!data || !data.messages || data.messages.length === 0) {
            dom.chatMessages.innerHTML = '';
            dom.chatMeta.textContent = `Agent: ${agentId} · No messages yet`;
            updateSessionIdDisplay();
            loadChatHeaderModels();
            dom.chatInput.focus();
            return;
        }

        const latestSessionId = data.messages[data.messages.length - 1].sessionId;
        storeManager.getOrCreateStore(latestSessionId, { agentId, channelType });
        storeManager.switchView(latestSessionId);

        dom.chatMessages.innerHTML = '';
        renderHistoryBatch(data.messages, data.sessionBoundaries);
        setupScrollbackObserver(channelType, agentId, data.nextCursor, data.hasMore);

        await checkAgentRunningStatus(agentId, latestSessionId);

        updateSessionIdDisplay();
        scrollToBottom();
        dom.chatInput.focus();
        updateSendButtonState();
        loadChatHeaderModels();
    } finally {
        storeManager.setSwitchingView(false);
        updateSendButtonState();
    }
}

async function checkAgentRunningStatus(agentId, sessionId) {
    try {
        const status = await fetchJson(`/agents/${encodeURIComponent(agentId)}/sessions/${encodeURIComponent(sessionId)}/status`);
        if (status && (status.status === 'Running' || status.status === 'Idle')) {
            getStreamState(sessionId).isStreaming = true;
            showStreamingIndicator(sessionId);
            showProcessingStatus('Agent is processing...', '⏳', sessionId);
            setSendingState(false);
            updateSendButtonState();
        }
    } catch { /* 404 if no instance — fine */ }
}

// ── Scrollback (IntersectionObserver) ───────────────────────────────

function createSessionDividerEl(sessionId, timestamp) {
    const divider = document.createElement('div');
    divider.className = 'session-divider';
    divider.dataset.sessionId = sessionId;
    const date = new Date(timestamp || Date.now());
    const dateStr = date.toLocaleDateString(undefined, { month: 'short', day: 'numeric', year: 'numeric' });
    const timeStr = date.toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' });
    divider.innerHTML = `<span class="session-divider-line"></span><span class="session-divider-label">Session started ${dateStr} at ${timeStr}</span><span class="session-divider-line"></span>`;
    return divider;
}

function setupScrollbackObserver(channelType, agentId, initialCursor, initialHasMore) {
    const viewKey = `${agentId}::${normalizeChannelKey(channelType)}`;
    const oldCleanup = _scrollbackCleanups.get(viewKey);
    if (oldCleanup) oldCleanup();

    const sentinel = document.createElement('div');
    sentinel.className = 'history-sentinel';
    dom.chatMessages.prepend(sentinel);

    let nextCursor = initialCursor;
    let hasMore = initialHasMore !== false;
    let isFetching = false;

    if (!hasMore || nextCursor === null) {
        showEndOfHistory(sentinel);
        _scrollbackCleanups.delete(viewKey);
        return () => {};
    }

    const observer = new IntersectionObserver(entries => {
        if (entries[0].isIntersecting && !isFetching && nextCursor !== null) fetchOlder();
    }, { root: dom.chatMessages, rootMargin: '200px 0px 0px 0px' });
    observer.observe(sentinel);

    async function fetchOlder() {
        isFetching = true;
        showTopSpinner(sentinel);
        const historyChannelType = toHubChannelType(channelType);
        const data = await fetchJson(
            `/channels/${encodeURIComponent(historyChannelType)}/agents/${encodeURIComponent(agentId)}/history?cursor=${encodeURIComponent(nextCursor)}&limit=50`
        );
        if (getCurrentAgentId() !== agentId ||
            normalizeChannelKey(getCurrentChannelType()) !== normalizeChannelKey(channelType)) {
            isFetching = false; return;
        }
        if (!data || !data.messages || data.messages.length === 0) {
            observer.disconnect();
            showEndOfHistory(sentinel);
            isFetching = false; return;
        }
        const scrollHeightBefore = dom.chatMessages.scrollHeight;
        const fragment = document.createDocumentFragment();
        renderHistoryBatch(data.messages, data.sessionBoundaries, fragment);
        sentinel.after(fragment);
        dom.chatMessages.scrollTop += dom.chatMessages.scrollHeight - scrollHeightBefore;
        nextCursor = data.nextCursor;
        hasMore = data.hasMore;
        hideTopSpinner(sentinel);
        isFetching = false;
        if (!hasMore) { observer.disconnect(); showEndOfHistory(sentinel); }
    }

    const cleanup = () => observer.disconnect();
    _scrollbackCleanups.set(viewKey, cleanup);
    return cleanup;
}

function showTopSpinner(sentinel) {
    let spinner = sentinel.querySelector('.history-spinner');
    if (!spinner) {
        spinner = document.createElement('div');
        spinner.className = 'history-spinner';
        spinner.textContent = 'Loading older messages...';
        spinner.style.cssText = 'text-align:center;padding:8px;color:var(--text-secondary);font-size:0.85rem;';
        sentinel.appendChild(spinner);
    }
    spinner.classList.remove('hidden');
}

function hideTopSpinner(sentinel) {
    const spinner = sentinel.querySelector('.history-spinner');
    if (spinner) spinner.classList.add('hidden');
}

function showEndOfHistory(sentinel) {
    sentinel.innerHTML = '';
    const endEl = document.createElement('div');
    endEl.className = 'history-end end-of-history';
    endEl.style.cssText = 'text-align:center;padding:12px;color:var(--text-secondary);font-size:0.85rem;';
    endEl.innerHTML = '<span class="session-divider-line"></span> Beginning of conversation history <span class="session-divider-line"></span>';
    sentinel.appendChild(endEl);
}

// ── Sub-agent panel ─────────────────────────────────────────────────

const SUBAGENT_STATUS_MAP = {
    Running:   { icon: '🟢', label: 'Running',   css: 'running' },
    Completed: { icon: '✅', label: 'Completed', css: 'completed' },
    Failed:    { icon: '❌', label: 'Failed',    css: 'failed' },
    Killed:    { icon: '🛑', label: 'Killed',    css: 'killed' },
    TimedOut:  { icon: '⏱',  label: 'Timed Out', css: 'timedout' }
};

export async function fetchSubAgents() {
    if (!getCurrentSessionId()) return;
    const data = await fetchJson(`/sessions/${encodeURIComponent(getCurrentSessionId())}/subagents`);
    if (!data) return;
    activeSubAgents.clear();
    const list = Array.isArray(data) ? data : (data.subAgents || data.subagents || []);
    for (const sa of list) { if (sa.subAgentId) activeSubAgents.set(sa.subAgentId, sa); }
    renderSubAgentPanel();
}

export function renderSubAgentPanel() {
    const count = activeSubAgents.size;
    if (count === 0) { dom.subAgentPanel.classList.add('hidden'); return; }
    dom.subAgentPanel.classList.remove('hidden');
    dom.subAgentCountBadge.textContent = count;
    dom.subAgentCountBadge.classList.toggle('empty', count === 0);

    const normalizeStatus = (status) => {
        if (typeof status === 'number') {
            return ['Running', 'Completed', 'Failed', 'Killed', 'TimedOut'][status] || 'Running';
        }
        return status || 'Running';
    };

    const sorted = [...activeSubAgents.values()].sort((a, b) => {
        const aS = normalizeStatus(a.status), bS = normalizeStatus(b.status);
        if (aS === 'Running' && bS !== 'Running') return -1;
        if (bS === 'Running' && aS !== 'Running') return 1;
        return new Date(b.startedAt || 0) - new Date(a.startedAt || 0);
    });

    dom.subAgentList.innerHTML = '';
    for (const sa of sorted) {
        const ns = normalizeStatus(sa.status);
        const info = SUBAGENT_STATUS_MAP[ns] || SUBAGENT_STATUS_MAP.Running;
        const isRunning = ns === 'Running';
        const taskPreview = (sa.task || '').length > 80 ? sa.task.substring(0, 80) + '…' : (sa.task || '');
        const hasResult = sa.resultSummary && !isRunning;

        const item = document.createElement('div');
        item.className = `subagent-item ${info.css}`;
        item.setAttribute('role', 'listitem');
        item.dataset.subAgentId = sa.subAgentId;

        let html = `
            <div class="subagent-item-row">
                <span class="subagent-status-icon" title="${info.label}">${info.icon}</span>
                <span class="subagent-name">${escapeHtml(sa.name || sa.subAgentId)}</span>
                ${sa.model ? `<span class="subagent-model">${escapeHtml(sa.model)}</span>` : ''}
                <div class="subagent-actions">
                    ${isRunning ? `<button class="btn-kill-subagent" data-id="${escapeHtml(sa.subAgentId)}" title="Kill sub-agent">Kill</button>` : ''}
                </div>
            </div>`;
        if (taskPreview) html += `<div class="subagent-task" title="${escapeHtml(sa.task || '')}">${escapeHtml(taskPreview)}</div>`;
        const meta = [];
        if (sa.startedAt) {
            if (isRunning) { meta.push(relativeTime(sa.startedAt)); }
            else if (sa.completedAt) {
                const elapsed = Math.round((new Date(sa.completedAt) - new Date(sa.startedAt)) / 1000);
                meta.push(elapsed < 60 ? `${elapsed}s` : `${Math.floor(elapsed / 60)}m ${elapsed % 60}s`);
            }
        }
        if (sa.turnsUsed > 0) meta.push(`${sa.turnsUsed} turns`);
        if (meta.length) html += `<div class="subagent-meta">${meta.join(' · ')}</div>`;
        if (hasResult) {
            html += `<div class="subagent-result" data-id="${escapeHtml(sa.subAgentId)}">
                <button class="subagent-result-toggle">Show result</button>
                <div class="subagent-result-content">${escapeHtml(sa.resultSummary)}</div>
            </div>`;
        }
        item.innerHTML = html;
        dom.subAgentList.appendChild(item);
    }

    dom.subAgentList.querySelectorAll('.btn-kill-subagent').forEach(btn => {
        btn.addEventListener('click', (e) => { e.stopPropagation(); killSubAgent(btn.dataset.id); });
    });
    dom.subAgentList.querySelectorAll('.subagent-result-toggle').forEach(btn => {
        btn.addEventListener('click', (e) => {
            e.stopPropagation();
            const resultDiv = btn.closest('.subagent-result');
            const isExpanded = resultDiv.classList.toggle('expanded');
            btn.textContent = isExpanded ? 'Hide result' : 'Show result';
        });
    });
}

async function killSubAgent(subAgentId) {
    if (!getCurrentSessionId() || !subAgentId) return;
    const btn = dom.subAgentList.querySelector(`.btn-kill-subagent[data-id="${subAgentId}"]`);
    if (btn) { btn.disabled = true; btn.textContent = '…'; }
    try {
        const res = await fetch(
            `${API_BASE}/sessions/${encodeURIComponent(getCurrentSessionId())}/subagents/${encodeURIComponent(subAgentId)}`,
            { method: 'DELETE' }
        );
        if (res.ok) {
            const sa = activeSubAgents.get(subAgentId);
            if (sa) { sa.status = 'Killed'; sa.completedAt = new Date().toISOString(); }
            renderSubAgentPanel();
        } else {
            console.error('Kill sub-agent failed:', res.status);
            if (btn) { btn.disabled = false; btn.textContent = 'Kill'; }
        }
    } catch (err) {
        console.error('Kill sub-agent error:', err);
        if (btn) { btn.disabled = false; btn.textContent = 'Kill'; }
    }
}

export function initSubAgentPanel() {
    const toggle = $('#subagent-panel-toggle');
    if (toggle) {
        toggle.addEventListener('click', (e) => {
            if (e.target.closest('.btn-icon')) return;
            dom.subAgentPanel.classList.toggle('collapsed');
        });
    }
    const refreshBtn = $('#btn-refresh-subagents');
    if (refreshBtn) {
        refreshBtn.addEventListener('click', (e) => { e.stopPropagation(); fetchSubAgents(); });
    }
}

export function clearSubAgentPanel() {
    activeSubAgents.clear();
    dom.subAgentPanel.classList.add('hidden');
    dom.subAgentList.innerHTML = '';
}

export function clearChatMessages() {
    dom.chatMessages.innerHTML = '';
}

// ── Model selector in chat header ───────────────────────────────────

export async function loadChatHeaderModels() {
    if (!getCurrentAgentId()) return;
    try {
        const agent = await fetchJson(`/agents/${encodeURIComponent(getCurrentAgentId())}`);
        const currentModel = agent?.modelId || '';
        let models = [];
        try { models = await fetchJson('/models'); } catch { /* models endpoint may not exist */ }
        dom.modelSelect.innerHTML = '';
        if (models && models.length > 0) {
            const agentProvider = (agent?.apiProvider || '').toLowerCase();
            const filtered = agentProvider
                ? models.filter(m => (m.provider || '').toLowerCase() === agentProvider)
                : models;
            filtered.sort((a, b) => (a.name || a.modelId || a.id || '').localeCompare(b.name || b.modelId || b.id || ''));
            for (const m of filtered) {
                const opt = document.createElement('option');
                const modelId = m.modelId || m.id || 'unknown';
                opt.value = modelId;
                opt.textContent = m.name || modelId;
                if (modelId === currentModel) opt.selected = true;
                dom.modelSelect.appendChild(opt);
            }
        } else {
            const opt = document.createElement('option');
            opt.value = currentModel;
            opt.textContent = currentModel || 'Unknown model';
            opt.selected = true;
            dom.modelSelect.appendChild(opt);
        }
    } catch (e) {
        console.error('Failed to load models:', e);
        dom.modelSelect.innerHTML = '<option value="">Error loading models</option>';
    }
}

export async function handleModelChange() {
    if (!getCurrentAgentId() || !dom.modelSelect.value) return;
    const newModel = dom.modelSelect.value;
    try {
        const agent = await fetchJson(`/agents/${encodeURIComponent(getCurrentAgentId())}`);
        if (!agent) { appendSystemMessage('❌ Failed to load agent details', 'error'); return; }
        agent.modelId = newModel;
        const res = await fetch(`${API_BASE}/agents/${encodeURIComponent(getCurrentAgentId())}`, {
            method: 'PUT', headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(agent)
        });
        if (res.ok) appendSystemMessage(`✅ Model changed to ${newModel}`);
        else appendSystemMessage(`❌ Failed to change model: ${res.status}`, 'error');
    } catch (e) {
        appendSystemMessage(`❌ Failed to change model: ${e.message}`, 'error');
    }
}

// ── Toggle visibility ───────────────────────────────────────────────

export function toggleToolVisibility() {
    showTools = dom.toggleTools.checked;
    const store = storeManager.activeStore;
    if (store) {
        store.showTools = showTools;
        setShowTools(store.agentId, store.channelType, showTools);
    } else {
        setShowTools(null, null, showTools);
    }
    applyToggleState();
}

export function toggleThinkingVisibility() {
    showThinking = dom.toggleThinking.checked;
    const store = storeManager.activeStore;
    if (store) {
        store.showThinking = showThinking;
        setShowThinking(store.agentId, store.channelType, showThinking);
    } else {
        setShowThinking(null, null, showThinking);
    }
    applyToggleState();
}

function applyToggleState(container = dom.chatMessages) {
    container.querySelectorAll('.tool-call').forEach(el => {
        el.classList.toggle('hidden', !showTools);
    });
    container.querySelectorAll('.thinking-block').forEach(el => {
        el.classList.toggle('hidden', !showThinking);
        el.classList.toggle('collapsed', !showThinking);
        const toggle = el.querySelector('.thinking-toggle');
        if (toggle) {
            toggle.setAttribute('aria-expanded', showThinking);
            el.querySelector('.thinking-chevron').textContent = showThinking ? '▾' : '▸';
        }
    });
}
