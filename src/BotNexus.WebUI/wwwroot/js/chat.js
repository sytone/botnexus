// BotNexus WebUI — Chat canvas, message rendering, commands, sub-agents

import {
    API_BASE, fetchJson, normalizeChannelKey, toHubChannelType, channelDisplayName,
    debugLog, serverLog, sealSession, getCommands, postCommandExecute
} from './api.js';
import {
    dom, $, escapeHtml, formatTime, relativeTime, renderMarkdown, scrollToBottom,
    autoResize, incrementNewMessageCount, resetNewMessageCount,
    renderProcessingStatus, showSteerIndicator, showFollowUpIndicator,
    showView, showConfirm, closeSidebar, setBatchRenderingState
} from './ui.js';
import {
    channelManager, getCurrentSessionId, getCurrentAgentId, getStreamState,
    isCurrentSessionStreaming, setCurrentChannelType, getCurrentChannelType
} from './session-store.js';
import { hubInvoke, getConnection } from './hub.js';
import { loadSessions, trackActivity, updateSidebarBadge } from './sidebar.js';
import { activeSubAgents } from './events.js';
import { getShowTools, setShowTools, getShowThinking, setShowThinking, setLastContext } from './storage.js';
import { isAudioRecordingSupported, startRecording, stopRecording, isRecording, cancelRecording } from './audio.js';

// ── Module state ────────────────────────────────────────────────────

// Toggle state is per-channel (stored on each SessionStore).
// These module-level vars mirror the active store for fast access during rendering.
let showTools = true;
let showThinking = true;

// Sync module-level toggle vars from the active session store
export function syncTogglesFromActiveStore() {
    const ctx = channelManager.active;
    if (ctx) {
        showTools = ctx.showTools;
        showThinking = ctx.showThinking;
    }
    // Sync checkbox DOM
    if (dom.toggleTools) dom.toggleTools.checked = showTools;
    if (dom.toggleThinking) dom.toggleThinking.checked = showThinking;
}
let sendModeFollowUp = false;
let pendingAudio = null;
let messageQueueCount = 0;
let pendingQueuedMessages = [];
let commandPaletteIndex = -1;
let responseTimeoutTimer = null;
let hasReceivedResponse = false;
let toolElapsedTimer = null;
const _scrollbackCleanups = new Map();
const RESPONSE_TIMEOUT_MS = 30000;

// ── Sub-agent read-only view state ──────────────────────────────────
let _subAgentViewSessionId = null;   // non-null ⇒ we are in read-only mode
let _subAgentViewCtx = null;         // the ChannelContext hosting the view

/** Get the messages element for the active channel, or null. */
function getActiveMessagesEl() {
    return channelManager.active?.messagesEl || null;
}

// ── Event handler entry points (called from events.js) ──────────────

export function onMessageStart(ctx, evt, sid) {
    markResponseReceived();
    hideTranscriptionIndicator(ctx?.messagesEl);
    removeStreamingIndicator(ctx);
    showStreamingIndicator(sid, ctx);
    showProcessingStatus('Agent is responding...', '🤖', sid);
    decrementQueue();
    // Only update shared UI if this is the active channel
    if (ctx.key === channelManager.activeKey) {
        dom.btnAbort.classList.remove('hidden');
        updateSendButtonState();
    }
}

export function onContentDelta(ctx, text) {
    markResponseReceived();
    hideTranscriptionIndicator(ctx?.messagesEl);
    removeStreamingIndicator(ctx);
    appendDelta(text, ctx.messagesEl);
    if (ctx.key === channelManager.activeKey) scrollToBottom(false, ctx.messagesEl);
}

export function onThinkingDelta(ctx, text) {
    markResponseReceived();
    handleThinkingDelta(text, ctx);
}

export function onToolStart(ctx, evt) {
    markResponseReceived();
    handleToolStart(evt, ctx);
}

export function onToolEnd(ctx, evt) {
    handleToolEnd(evt, ctx);
}

export function onMessageEnd(ctx, evt) {
    finalizeMessage(evt, ctx);
}

export function onError(ctx, evt) {
    handleError(evt, ctx);
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
    if (channelManager.isSwitchingView) {
        dom.chatInput.disabled = true;
        dom.btnSend.disabled = true;
        return;
    }
    // Streaming check must run before isRestRequestInFlight — the hub invoke
    // blocks for the full stream so isRestRequestInFlight stays true during
    // streaming.  Users still need to steer/follow-up while the stream runs.
    if (isCurrentSessionStreaming() && connection?.state === signalR.HubConnectionState.Connected) {
        dom.chatInput.disabled = false;
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
    if (channelManager.isRestRequestInFlight) {
        dom.btnSend.disabled = true;
        return;
    }
    dom.chatInput.disabled = false;
    dom.btnSend.classList.remove('btn-steer', 'btn-followup');
    dom.btnSend.textContent = 'Send';
    dom.chatInput.placeholder = 'Type a message... (Enter to send, Shift+Enter for newline)';
    const hasAudio = !!pendingAudio;
    dom.btnSend.disabled = (!hasText && !hasAudio) || !dom.chatView || dom.chatView.classList.contains('hidden');
    dom.btnSendMode.classList.add('hidden');
    sendModeFollowUp = false;
}

export function setSendingState(isSending) {
    channelManager.setRestRequestInFlight(isSending);
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
    const ctx = channelManager.active;
    if (!ctx) {
        renderProcessingStatus(false);
        dom.btnAbort.classList.add('hidden');
        return;
    }
    const ss = ctx.streamState;
    renderProcessingStatus(ss.processingVisible, ss.processingStage, ss.processingIcon);
    dom.btnAbort.classList.toggle('hidden', !ss.isStreaming);
    if (!ss.showStreamingIndicator) {
        ctx.messagesEl.querySelectorAll('.streaming-indicator').forEach(el => el.remove());
    }
}

function showProcessingStatus(stage, icon, sessionId) {
    const ctx = sessionId ? channelManager.getBySessionId(sessionId) : channelManager.active;
    if (!ctx) return;
    const ss = ctx.streamState;
    ss.processingVisible = true;
    ss.processingStage = stage || 'Processing...';
    ss.processingIcon = icon || '⏳';
    if (ctx.key === channelManager.activeKey) {
        renderProcessingStatus(true, ss.processingStage, ss.processingIcon);
    }
}

export function hideProcessingStatus(sessionId) {
    const ctx = sessionId ? channelManager.getBySessionId(sessionId) : channelManager.active;
    if (ctx) {
        const ss = ctx.streamState;
        ss.processingVisible = false;
        ss.processingStage = '';
        ss.processingIcon = '⏳';
    }
    if (!ctx || ctx.key === channelManager.activeKey) {
        renderProcessingStatus(false);
    }
}

// ── Streaming indicator ─────────────────────────────────────────────

function showStreamingIndicator(sessionId, ctx) {
    const sid = sessionId || getCurrentSessionId();
    if (!sid && !ctx) return;
    const ss = ctx ? ctx.streamState : getStreamState(sid);
    ss.showStreamingIndicator = true;
}

function hideStreamingIndicator(ctx) {
    const targetCtx = ctx || channelManager.active;
    if (targetCtx) {
        targetCtx.streamState.showStreamingIndicator = false;
        targetCtx.messagesEl.querySelectorAll('.streaming-indicator').forEach(el => el.remove());
    }
}

function removeStreamingIndicator(ctx) { hideStreamingIndicator(ctx); }

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

function handleThinkingDelta(text, ctx) {
    if (!text) return;
    const ss = ctx ? ctx.streamState : getStreamState(getCurrentSessionId());
    const el = ctx ? ctx.messagesEl : getActiveMessagesEl();
    if (!el) return;

    let thinkingEl = el.querySelector('.thinking-block');
    if (!thinkingEl) {
        removeStreamingIndicator(ctx);
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
        el.appendChild(thinkingEl);
    }
    thinkingEl.querySelector('.thinking-pre').textContent = ss.thinkingBuffer;
    thinkingEl.querySelector('.thinking-stats').textContent = formatCharCount(ss.thinkingBuffer.length);
    const activeEl = getActiveMessagesEl();
    if (el === activeEl) scrollToBottom(false, el);
}

function finalizeThinkingBlock(ctx) {
    const el = ctx ? ctx.messagesEl : getActiveMessagesEl();
    if (!el) return;
    const thinkingEl = el.querySelector('.thinking-block');
    if (thinkingEl) {
        const ss = ctx ? ctx.streamState : getStreamState(getCurrentSessionId());
        const charCount = ss.thinkingBuffer.length > 0
            ? ` (${formatCharCount(ss.thinkingBuffer.length)})` : '';
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
    const el = getActiveMessagesEl();
    if (!el) return;
    const thinkingEl = el.querySelector('.thinking-block:not(.complete)');
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

function handleToolStart(msg, ctx) {
    const ss = ctx ? ctx.streamState : getStreamState(getCurrentSessionId());
    const callId = msg.toolCallId || `tc-${Date.now()}`;
    ss.activeToolCount++;
    ss.toolStartTimes[callId] = Date.now();
    const depth = msg.depth || ss.toolCallDepth;
    ss.activeToolCalls[callId] = {
        toolName: msg.toolName || 'unknown', args: msg.toolArgs || '',
        result: '', status: 'running', depth
    };
    appendToolCall(callId, msg.toolName, 'running', msg.toolArgs, depth, ctx);
    if (!ctx || ctx.key === channelManager.activeKey) startToolElapsedTimer();
}

function handleToolEnd(msg, ctx) {
    const ss = ctx ? ctx.streamState : getStreamState(getCurrentSessionId());
    const callId = msg.toolCallId || 'unknown';
    const isError = msg.toolIsError === true;
    const status = isError ? 'error' : 'complete';
    if (ss.activeToolCalls[callId]) {
        ss.activeToolCalls[callId].result = msg.toolResult || '';
        ss.activeToolCalls[callId].status = status;
    }
    const elapsed = ss.toolStartTimes[callId] ? Math.round((Date.now() - ss.toolStartTimes[callId]) / 1000) : 0;
    delete ss.toolStartTimes[callId];
    updateToolCallStatus(callId, status, elapsed, msg.toolResult, ctx);
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

function appendToolCall(callId, toolName, status, toolArgs, depth, ctx) {
    const el = ctx ? ctx.messagesEl : getActiveMessagesEl();
    if (!el) return;
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
    el.appendChild(div);
    const activeEl = getActiveMessagesEl();
    if (el === activeEl) scrollToBottom(false, el);
}

function updateToolCallStatus(callId, status, elapsed, result, ctx) {
    const container = ctx ? ctx.messagesEl : getActiveMessagesEl();
    if (!container) return;
    const el = container.querySelector(`.tool-call[data-call-id="${callId}"]`);
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
        const ctx = channelManager.active;
        if (!ctx) return;
        for (const [callId, startTime] of Object.entries(ctx.streamState.toolStartTimes)) {
            const toolEl = ctx.messagesEl.querySelector(`.tool-call[data-call-id="${callId}"] .tool-elapsed`);
            if (toolEl) toolEl.textContent = `${Math.round((Date.now() - startTime) / 1000)}s`;
        }
    }, 1000);
}

function stopToolElapsedTimer() {
    if (toolElapsedTimer) { clearInterval(toolElapsedTimer); toolElapsedTimer = null; }
}

// ── Message finalization ────────────────────────────────────────────

function finalizeMessage(msg, ctx) {
    const ss = ctx ? ctx.streamState : getStreamState(getCurrentSessionId());
    const el = ctx ? ctx.messagesEl : getActiveMessagesEl();
    ss.isStreaming = false;
    ss.activeMessageId = null;
    ss.processingVisible = false;
    ss.processingStage = '';
    ss.processingIcon = '⏳';
    ss.showStreamingIndicator = false;

    // Only update shared UI for active channel
    const isActive = !ctx || ctx.key === channelManager.activeKey;
    if (isActive) {
        clearResponseTimeout();
        dom.btnAbort.classList.add('hidden');
        removeStreamingIndicator(ctx);
        hideProcessingStatus(ctx?.sessionId || getCurrentSessionId());
    }
    finalizeThinkingBlock(ctx);

    if (el) {
        const streaming = el.querySelector('.message.assistant.streaming');
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
    }

    ss.activeToolCalls = {};
    ss.activeToolCount = 0;
    ss.toolCallDepth = 0;
    ss.toolStartTimes = {};
    stopToolElapsedTimer();
    ss.thinkingBuffer = '';

    if (isActive) {
        resetQueue();
        setSendingState(false);
        updateSendButtonState();
        updateSessionIdDisplay();
        loadSessions();
        incrementNewMessageCount();
        scrollToBottom(false, el);
    }
}

function formatUsage(usage) {
    if (!usage) return '';
    const parts = [];
    if (usage.inputTokens) parts.push(`↑${usage.inputTokens}`);
    if (usage.outputTokens) parts.push(`↓${usage.outputTokens}`);
    if (usage.totalTokens) parts.push(`Σ${usage.totalTokens}`);
    return parts.join(' ');
}

function handleError(msg, ctx) {
    const ss = ctx ? ctx.streamState : getStreamState(getCurrentSessionId());
    ss.isStreaming = false;
    const isActive = !ctx || ctx.key === channelManager.activeKey;
    if (isActive) {
        clearResponseTimeout();
        stopToolElapsedTimer();
        dom.btnAbort.classList.add('hidden');
        removeStreamingIndicator(ctx);
        hideProcessingStatus();
    }
    // Always render error to the correct container
    const el = ctx ? ctx.messagesEl : getActiveMessagesEl();
    if (el) {
        const div = document.createElement('div');
        div.className = 'message assistant message-error';
        const now = formatTime(new Date().toISOString());
        div.innerHTML = `
            <div class="msg-header">
                <span class="msg-role">AGENT ERROR</span>
                <span class="msg-time">${now}</span>
            </div>
            <div class="msg-content">${escapeHtml(`❌ ${msg.message || 'Unknown error'}${msg.code ? ` (${msg.code})` : ''}`)}</div>
        `;
        el.appendChild(div);
        if (isActive) scrollToBottom(false, el);
    }
    if (isActive) {
        setSendingState(false);
        updateSendButtonState();
    }
}

// ── Chat message rendering ──────────────────────────────────────────

function stripControlTags(text) {
    if (!text) return text;
    return text.replace(/\[\[\s*reply_to_current\s*\]\]/gi, '')
               .replace(/\[\[\s*reply_to:\s*\S+\s*\]\]/gi, '')
               .replace(/\[\[reply_to_current\]\]/g, '');
}

export function appendChatMessage(role, content, timestamp) {
    const el = getActiveMessagesEl();
    if (el) appendChatMessageTo(role, content, el, timestamp);
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
    // Scroll if this container is the active channel's
    const activeEl = getActiveMessagesEl();
    if (container === activeEl) scrollToBottom(false, activeEl);
}

export function appendSystemMessage(text, level) {
    const el = getActiveMessagesEl();
    if (!el) return;
    const div = document.createElement('div');
    div.className = `message system-msg${level ? ' ' + level : ''}`;
    div.textContent = text;
    el.appendChild(div);
    scrollToBottom(false, el);
}

function appendErrorMessage(text) {
    const el = getActiveMessagesEl();
    if (!el) return;
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
    el.appendChild(div);
    scrollToBottom(false, el);
}

function appendDelta(content, targetEl) {
    if (!content) return;
    content = stripControlTags(content);
    if (!content) return;
    const el = targetEl || getActiveMessagesEl();
    if (!el) return;
    let streaming = el.querySelector('.message.assistant.streaming');
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
        el.appendChild(streaming);
    }
    streaming.querySelector('.delta-content').textContent += content;
    const activeEl = getActiveMessagesEl();
    if (el === activeEl) scrollToBottom(false, el);
}

// ── History rendering ───────────────────────────────────────────────

function renderHistoryEntry(entry) {
    const el = getActiveMessagesEl();
    if (el) renderHistoryEntryTo(entry, el);
}

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
    const ctx = channelManager.active;
    if (ctx) {
        ctx.streamState.activeToolCalls[callId] = {
            toolName, args: argsStr, result: resultStr, status: 'complete'
        };
    }
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
    container = container || getActiveMessagesEl();
    if (!container) return;
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

const FALLBACK_COMMANDS = [
    { name: '/help', description: 'Show available commands', clientSideOnly: true },
    { name: '/new', description: 'Start a new chat session', clientSideOnly: true },
    { name: '/reset', description: 'Clear chat and reset current session', clientSideOnly: true },
];

let _commands = [...FALLBACK_COMMANDS];

/** Fetch commands from the backend and cache them. Called on startup and reconnect. */
export async function loadCommands() {
    const cmds = await getCommands();
    if (cmds && cmds.length > 0) {
        _commands = cmds;
        debugLog('commands', `Loaded ${cmds.length} commands from backend`);
    } else {
        debugLog('commands', 'No commands from backend, using fallback');
    }
}

export function showCommandPalette(text) {
    const input = text.toLowerCase();
    const spaceIdx = input.indexOf(' ');
    let items = [];

    if (spaceIdx === -1) {
        // Top-level: filter commands by prefix
        const matches = _commands.filter(c => c.name.startsWith(input));
        items = matches.map(c => ({
            label: c.name,
            desc: c.description,
            action: (c.subCommands && c.subCommands.length > 0) ? 'expand' : 'execute',
            fullInput: c.name
        }));
    } else {
        // Sub-command: find parent command, filter its sub-commands
        const cmdName = input.substring(0, spaceIdx);
        const subFilter = input.substring(spaceIdx + 1);
        const parent = _commands.find(c => c.name === cmdName);
        if (parent?.subCommands) {
            const matches = parent.subCommands.filter(sc => sc.name.startsWith(subFilter));
            items = matches.map(sc => ({
                label: sc.name,
                desc: sc.description,
                action: 'execute',
                fullInput: `${parent.name} ${sc.name}`
            }));
        }
    }

    if (items.length === 0) { hideCommandPalette(); return; }
    commandPaletteIndex = 0;
    dom.commandPalette.innerHTML = '';
    for (let i = 0; i < items.length; i++) {
        const item = items[i];
        const el = document.createElement('div');
        el.className = 'command-item' + (i === 0 ? ' active' : '');
        el.setAttribute('role', 'option');
        el.dataset.index = i;
        el.dataset.action = item.action;
        el.dataset.fullInput = item.fullInput;
        el.innerHTML = `<span class="command-name">${escapeHtml(item.label)}</span><span class="command-desc">${escapeHtml(item.desc)}</span>`;
        el.addEventListener('click', () => _selectPaletteItem(item));
        dom.commandPalette.appendChild(el);
    }
    const hint = document.createElement('div');
    hint.className = 'command-palette-hint';
    hint.textContent = '↑↓ navigate · Tab or Enter to select · Esc to dismiss';
    dom.commandPalette.appendChild(hint);
    dom.commandPalette.classList.remove('hidden');
}

function _selectPaletteItem(item) {
    if (item.action === 'expand') {
        hideCommandPalette();
        dom.chatInput.value = item.fullInput + ' ';
        autoResize(dom.chatInput);
        showCommandPalette(dom.chatInput.value);
    } else {
        executeCommand(item.fullInput);
    }
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
    const active = dom.commandPalette.querySelector('.command-item.active');
    if (!active) return;
    _selectPaletteItem({
        action: active.dataset.action,
        fullInput: active.dataset.fullInput
    });
}

async function executeCommand(input) {
    hideCommandPalette();
    dom.chatInput.value = '';
    autoResize(dom.chatInput);
    updateSendButtonState();

    const cmdName = input.split(/\s/)[0].toLowerCase();
    const cmd = _commands.find(c => c.name === cmdName);

    if (!cmd) {
        appendSystemMessage(`Unknown command: ${input}`);
        return;
    }

    // Client-side commands execute locally
    if (cmd.clientSideOnly) {
        switch (cmdName) {
            case '/reset': executeReset(); break;
            case '/help': _executeHelpFallback(); break;
            case '/new': executeReset('new'); break;
            default: appendSystemMessage(`Unknown client command: ${cmdName}`); break;
        }
        return;
    }

    // Backend commands
    appendSystemMessage(`⏳ Running ${input}...`);
    const result = await postCommandExecute(input, getCurrentAgentId(), getCurrentSessionId());
    appendCommandResult(result.title, result.body, result.isError);

    // /new requires client-side session switch after backend confirms
    if (cmdName === '/new' && !result.isError) {
        executeReset('new');
    }
}

function _executeHelpFallback() {
    const lines = _commands.map(c => `  ${c.name.padEnd(12)} ${c.description}`).join('\n');
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
        const el = getActiveMessagesEl();
        if (el) {
            el.appendChild(divider);
            scrollToBottom(false, el);
        }
        appendSystemMessage('New session started. Previous messages are still visible above.');
    }

    channelManager.setActiveView(null, getCurrentAgentId(), getCurrentChannelType());
    syncLoadingUiForActiveSession();
    updateSessionIdDisplay();
    channelManager.setSelectedAgent(dom.agentSelect.value || getCurrentAgentId());
    loadSessions();
}

function appendCommandResult(title, body, isError = false) {
    const el = getActiveMessagesEl();
    if (!el) return;
    const div = document.createElement('div');
    div.className = `message system-msg command-result${isError ? ' command-error' : ''}`;
    div.innerHTML = `<div class="command-result-title">${escapeHtml(title)}</div><pre>${escapeHtml(body)}</pre>`;
    el.appendChild(div);
    scrollToBottom(false, el);
}

// ── Chat actions ────────────────────────────────────────────────────

export async function sendMessage() {
    if (_subAgentViewSessionId) return; // read-only — no input allowed
    if (channelManager.isSwitchingView) {
        appendSystemMessage('Please wait for session switch to complete.', 'warning');
        return;
    }
    const activeCtx = channelManager.active;
    const activeAgentId = activeCtx?.agentId || channelManager.activeAgentId;
    const activeSessionId = channelManager.activeViewId;
    if (!activeAgentId) return;
    const text = dom.chatInput.value.trim();
    if (!text && !pendingAudio) return;

    if (text.startsWith('/')) {
        const cmd = text.split(/\s/)[0].toLowerCase();
        const match = _commands.find(c => c.name === cmd);
        if (match) {
            dom.chatInput.value = '';
            autoResize(dom.chatInput);
            updateSendButtonState();
            hideCommandPalette();
            executeCommand(text);
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

    // Capture and clear pending audio before async work
    const audioToSend = pendingAudio;
    pendingAudio = null;
    clearAudioPendingIndicator();

    // Render user message — with inline audio player when audio is attached
    if (audioToSend) {
        appendChatMessageWithAudio(text, audioToSend);
        showTranscriptionIndicator();
    } else {
        appendChatMessage('user', text);
    }
    trackActivity('message', activeAgentId, (text || '🎤 Audio').substring(0, 60));
    setSendingState(true);
    if (activeSessionId) getStreamState(activeSessionId).isStreaming = true;
    startResponseTimeout();

    try {
        const channelType = toHubChannelType(activeCtx?.channelType || getCurrentChannelType() || 'Web Chat');
        let result;
        if (audioToSend) {
            result = await sendMessageWithAudio(activeAgentId, channelType, text, audioToSend);
        } else {
            serverLog('info', 'SendMessage request', { agentId: activeAgentId, channelType, sessionId: activeSessionId, textLength: text.length });
            result = await hubInvoke('SendMessage', activeAgentId, channelType, text);
            serverLog('info', 'SendMessage response', { agentId: result?.agentId || activeAgentId, sessionId: result?.sessionId, channelType: result?.channelType || channelType });
        }
        if (result?.sessionId) {
            const sessionChannelType = result.channelType || channelType;
            const ctx = channelManager.getOrCreate(result.agentId || activeAgentId, sessionChannelType);
            channelManager.registerSession(result.sessionId, ctx);
            // Only re-activate if user hasn't navigated away during the async invoke
            if (channelManager.activeKey === ctx.key) {
                channelManager.activate(ctx.key);
            }
            setLastContext(result.agentId || activeAgentId, sessionChannelType, result.sessionId);
            updateSessionIdDisplay();
        }
        setSendingState(false);
    } catch (err) {
        serverLog('error', 'SendMessage failed', { agentId: activeAgentId, sessionId: activeSessionId, error: err?.message || String(err) });
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

// ── Audio recording integration ─────────────────────────────────────

/**
 * Send a message with attached audio content via SendMessageWithMedia hub method.
 * @param {string} agentId - The active agent ID
 * @param {string} channelType - The hub channel type
 * @param {string} text - The text message (may be empty)
 * @param {{base64: string, mimeType: string, durationMs: number, sizeBytes: number}} audio - The audio data
 */
async function sendMessageWithAudio(agentId, channelType, text, audio) {
    const activeSessionId = getCurrentSessionId();
    const contentParts = [
        { mimeType: audio.mimeType, base64Data: audio.base64 }
    ];

    serverLog('info', 'SendMessageWithMedia request', {
        agentId, channelType,
        sessionId: activeSessionId, textLength: text.length,
        audioDurationMs: audio.durationMs, audioSizeBytes: audio.sizeBytes
    });

    const result = await hubInvoke('SendMessageWithMedia', agentId, channelType, text, contentParts);

    serverLog('info', 'SendMessageWithMedia response', {
        agentId: result?.agentId || agentId,
        sessionId: result?.sessionId
    });

    return result;
}

function showAudioPendingIndicator(durationMs) {
    let indicator = document.getElementById('audio-pending');
    if (!indicator) {
        indicator = document.createElement('div');
        indicator.id = 'audio-pending';
        indicator.className = 'audio-pending-indicator';
        const inputArea = document.getElementById('chat-input')?.closest('.chat-input-area');
        if (inputArea) inputArea.prepend(indicator);
    }
    const secs = (durationMs / 1000).toFixed(1);
    indicator.innerHTML = `🎤 Audio attached (${secs}s) <button class="btn-clear-audio" title="Remove audio">✕</button>`;
    indicator.style.display = 'block';

    // Wire the clear button
    const clearBtn = indicator.querySelector('.btn-clear-audio');
    if (clearBtn) {
        clearBtn.addEventListener('click', () => {
            pendingAudio = null;
            clearAudioPendingIndicator();
            updateSendButtonState();
        });
    }
}

function clearAudioPendingIndicator() {
    const indicator = document.getElementById('audio-pending');
    if (indicator) {
        indicator.style.display = 'none';
        indicator.innerHTML = '';
    }
}

/**
 * Render a user message bubble that contains an inline audio player.
 * @param {string} text - The user's text (may be empty for voice-only messages)
 * @param {{base64: string, mimeType: string, durationMs: number}} audio - Recorded audio data
 */
function appendChatMessageWithAudio(text, audio) {
    const el = getActiveMessagesEl();
    if (!el) return;
    const div = document.createElement('div');
    div.className = 'message user';
    const timeStr = formatTime(new Date().toISOString());
    const secs = (audio.durationMs / 1000).toFixed(1);

    div.innerHTML = `
        <div class="msg-header">
            <span class="msg-role">USER</span>
            <span class="msg-time">${timeStr}</span>
            <button class="btn-copy-msg" title="Copy message" aria-label="Copy message">📋</button>
        </div>
        <div class="msg-content">
            ${text ? `<p>${escapeHtml(text)}</p>` : ''}
            <div class="audio-message">
                <audio controls src="data:${escapeHtml(audio.mimeType)};base64,${audio.base64}"></audio>
                <span class="audio-meta">🎤 ${secs}s</span>
            </div>
        </div>
    `;
    div.dataset.rawContent = text || '🎤 Audio message';
    el.appendChild(div);
    const activeEl = getActiveMessagesEl();
    if (el === activeEl) scrollToBottom(false, activeEl);
}

/**
 * Show a "Transcribing audio..." indicator in the chat area.
 * Appears as a temporary assistant-side message; auto-cleared on first ContentDelta.
 */
function showTranscriptionIndicator() {
    const el = getActiveMessagesEl();
    if (!el) return;
    // Don't duplicate
    if (el.querySelector('.transcription-indicator')) return;
    const div = document.createElement('div');
    div.className = 'transcription-indicator';
    div.innerHTML = '🎤 Transcribing audio...';
    el.appendChild(div);
    scrollToBottom(false, el);
}

/**
 * Remove the transcription indicator if present.
 */
function hideTranscriptionIndicator(targetEl) {
    const el = targetEl || getActiveMessagesEl();
    if (!el) return;
    el.querySelectorAll('.transcription-indicator').forEach(ind => ind.remove());
}

export function initAudioRecording() {
    const recordBtn = document.getElementById('record-btn');
    if (!recordBtn || !isAudioRecordingSupported()) return;

    recordBtn.style.display = '';
    let recordingTimer = null;

    recordBtn.addEventListener('click', async () => {
        if (isRecording()) {
            // Stop recording
            clearInterval(recordingTimer);
            recordBtn.textContent = '🎤';
            recordBtn.classList.remove('recording');
            recordBtn.title = 'Record audio';

            try {
                pendingAudio = await stopRecording();
                showAudioPendingIndicator(pendingAudio.durationMs);
                updateSendButtonState();
            } catch (err) {
                debugLog('audio', 'Stop failed', err);
            }
        } else {
            // Start recording
            try {
                await startRecording();
                recordBtn.textContent = '🔴';
                recordBtn.classList.add('recording');

                let seconds = 0;
                recordBtn.title = 'Recording... 0s';
                recordingTimer = setInterval(() => {
                    seconds++;
                    recordBtn.title = `Recording... ${seconds}s`;
                }, 1000);
            } catch (err) {
                debugLog('audio', 'Start failed', err);
            }
        }
    });
}

export function startNewChat() {
    const agentId = dom.agentSelect.value || null;
    if (!agentId) return;
    channelManager.setActiveView(null, agentId, 'Web Chat');
    syncLoadingUiForActiveSession();
    resetQueue();
    channelManager.setSelectedAgent(agentId);
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
                        channelManager.setActiveView(null, null, getCurrentChannelType());
                        syncLoadingUiForActiveSession();
                        channelManager.setSelectedAgent(null);
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
    // Close any active sub-agent read-only view before switching
    closeSubAgentView();

    channelManager.setSwitchingView(true);
    updateSendButtonState();

    if (channelManager.isRestRequestInFlight) setSendingState(false);
    resetQueue();
    clearResponseTimeout();
    stopToolElapsedTimer();
    resetNewMessageCount();

    // Get or create the channel context (creates DOM if needed)
    const ctx = channelManager.getOrCreate(agentId, channelType);

    // Activate this channel (hides all others, shows this one)
    channelManager.activate(ctx.key);

    // Highlight currently viewed channel in sidebar
    dom.sessionsList.querySelectorAll('.list-item').forEach(el => {
        el.classList.toggle('viewing',
            el.dataset.agentId === agentId &&
            normalizeChannelKey(el.dataset.channelType) === normalizeChannelKey(channelType));
    });

    showView('chat-view');
    if (agentId) dom.agentSelect.value = agentId;
    dom.agentSelect.classList.add('hidden');
    channelManager.setSelectedAgent(agentId);

    // Update header
    dom.chatMeta.textContent = `Agent: ${agentId} · ${channelDisplayName(channelType)}`;
    dom.chatTitle.textContent = `${agentId} — ${channelDisplayName(channelType)}`;
    document.title = `${agentId} — ${channelDisplayName(channelType)} | BotNexus`;

    // Sync toggles for this channel
    showTools = ctx.showTools;
    showThinking = ctx.showThinking;
    if (dom.toggleTools) dom.toggleTools.checked = showTools;
    if (dom.toggleThinking) dom.toggleThinking.checked = showThinking;

    updateSessionIdDisplay();
    syncLoadingUiForActiveSession();

    const hash = `#/agents/${encodeURIComponent(agentId)}/channels/${encodeURIComponent(toHubChannelType(channelType))}`;
    if (location.hash !== hash) history.pushState(null, '', hash);

    // Reset unread
    ctx.unreadCount = 0;
    updateSidebarBadge(ctx.sessionId, 0);

    // Persist last context for restore on reload
    setLastContext(agentId, channelType, ctx.sessionId);

    try {
        // If history already loaded, just scroll and focus
        if (ctx.historyLoaded) {
            ctx.messagesEl.scrollTop = ctx.messagesEl.scrollHeight;
            dom.chatInput.focus();
            updateSendButtonState();
            loadChatHeaderModels();
            fetchSubAgents();
            return;
        }

        // Show loading in this channel's container
        ctx.messagesEl.innerHTML = '<div class="loading">Loading timeline...</div>';

        const historyChannelType = toHubChannelType(channelType);
        const data = await fetchJson(
            `/channels/${encodeURIComponent(historyChannelType)}/agents/${encodeURIComponent(agentId)}/history?limit=50`
        );

        if (!data || !data.messages || data.messages.length === 0) {
            ctx.messagesEl.innerHTML = '';
            dom.chatMeta.textContent = `Agent: ${agentId} · No messages yet`;
            ctx.historyLoaded = true;
            updateSessionIdDisplay();
            loadChatHeaderModels();
            dom.chatInput.focus();
            return;
        }

        const latestSessionId = data.messages[data.messages.length - 1].sessionId;
        channelManager.registerSession(latestSessionId, ctx);
        setLastContext(agentId, channelType, latestSessionId);

        ctx.messagesEl.innerHTML = '';
        renderHistoryBatch(data.messages, data.sessionBoundaries, ctx.messagesEl);
        ctx.historyLoaded = true;

        await checkAgentRunningStatus(agentId, latestSessionId);

        updateSessionIdDisplay();
        scrollToBottom(true, ctx.messagesEl);
        // Set up the scrollback observer after scrolling so the sentinel is out
        // of view and the IntersectionObserver doesn't fire prematurely.
        setupScrollbackObserver(channelType, agentId, data.nextCursor, data.hasMore, ctx);
        dom.chatInput.focus();
        updateSendButtonState();
        loadChatHeaderModels();
    } finally {
        channelManager.setSwitchingView(false);
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

function setupScrollbackObserver(channelType, agentId, initialCursor, initialHasMore, ctx) {
    const viewKey = ctx ? ctx.key : `${agentId}::${normalizeChannelKey(channelType)}`;
    const messagesEl = ctx ? ctx.messagesEl : getActiveMessagesEl();
    if (!messagesEl) return () => {};

    const oldCleanup = _scrollbackCleanups.get(viewKey);
    if (oldCleanup) oldCleanup();

    const sentinel = document.createElement('div');
    sentinel.className = 'history-sentinel';
    messagesEl.prepend(sentinel);

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
    }, { root: messagesEl, rootMargin: '200px 0px 0px 0px' });
    observer.observe(sentinel);

    async function fetchOlder() {
        isFetching = true;
        showTopSpinner(sentinel);
        const historyChannelType = toHubChannelType(channelType);
        const data = await fetchJson(
            `/channels/${encodeURIComponent(historyChannelType)}/agents/${encodeURIComponent(agentId)}/history?cursor=${encodeURIComponent(nextCursor)}&limit=50`
        );
        // Check this is still the right context
        if (ctx && channelManager.activeKey !== ctx.key) {
            isFetching = false; return;
        }
        if (!data || !data.messages || data.messages.length === 0) {
            observer.disconnect();
            showEndOfHistory(sentinel);
            isFetching = false; return;
        }
        hideTopSpinner(sentinel);
        const scrollHeightBefore = messagesEl.scrollHeight;
        const fragment = document.createDocumentFragment();
        renderHistoryBatch(data.messages, data.sessionBoundaries, fragment);
        sentinel.after(fragment);
        messagesEl.scrollTop += messagesEl.scrollHeight - scrollHeightBefore;
        nextCursor = data.nextCursor;
        hasMore = data.hasMore;
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
    TimedOut:  { icon: '⏱',  label: 'Timed Out', css: 'timedout' },
    Active:    { icon: '🟢', label: 'Running',   css: 'running' },
    Expired:   { icon: '✅', label: 'Completed', css: 'completed' },
    Sealed:    { icon: '🔒', label: 'Sealed',    css: 'sealed' }
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
    const el = getActiveMessagesEl();
    if (el) el.innerHTML = '';
}

// ── Sub-agent read-only session view ────────────────────────────────

/**
 * True when the UI is showing a read-only sub-agent conversation.
 * Exported so events.js can route live messages into the view.
 */
export function getSubAgentViewSessionId() { return _subAgentViewSessionId; }

/**
 * Reactively update the read-only banner status when a sub-agent lifecycle
 * event fires while the user is viewing that sub-agent's session.
 * @param {string} newStatus — one of: Completed, Failed, Killed, TimedOut
 */
export function updateSubAgentViewStatus(newStatus) {
    if (!_subAgentViewSessionId) return;

    const statusInfo = SUBAGENT_STATUS_MAP[newStatus] || SUBAGENT_STATUS_MAP.Running;
    const isTerminal = ['Completed', 'Failed', 'Killed', 'TimedOut', 'Expired', 'Sealed'].includes(newStatus);

    // Update the status span in the read-only banner
    const banner = document.getElementById('subagent-readonly-banner');
    if (!banner) return;

    const statusEl = banner.querySelector('.readonly-status');
    if (statusEl) {
        statusEl.innerHTML = `${statusInfo.icon} ${escapeHtml(statusInfo.label)}`;
    }

    // Update the chat meta line
    if (dom.chatMeta) {
        dom.chatMeta.textContent = `Read-only · ${newStatus}`;
    }

    // Show the seal button if terminal and not already present
    if (isTerminal && !banner.querySelector('#btn-seal-subagent')) {
        const closeBtn = banner.querySelector('#btn-close-subagent');
        const sealBtn = document.createElement('button');
        sealBtn.className = 'readonly-seal-btn';
        sealBtn.id = 'btn-seal-subagent';
        sealBtn.textContent = 'Seal';
        sealBtn.addEventListener('click', async () => {
            sealBtn.disabled = true;
            sealBtn.textContent = 'Sealing…';
            const ok = await sealSession(_subAgentViewSessionId);
            if (ok) {
                closeSubAgentView();
                loadSessions();
            } else {
                sealBtn.disabled = false;
                sealBtn.textContent = 'Seal';
                appendSystemMessage('Failed to seal session.', 'error');
            }
        });
        if (closeBtn) {
            banner.insertBefore(sealBtn, closeBtn);
        } else {
            banner.appendChild(sealBtn);
        }
    }
}

/**
 * Open a read-only conversation view for a sub-agent session.
 * Fetches history from `GET /sessions/{sessionId}/history`, renders it into
 * the active channel's message container with a sticky read-only banner, and
 * hides the input bar.
 */
export async function openSubAgentSession(sessionId) {
    if (!sessionId) return;
    debugLog('subagent', 'openSubAgentSession', sessionId);

    // If already showing this session, no-op
    if (_subAgentViewSessionId === sessionId) return;

    // Close any previous sub-agent view first
    closeSubAgentView(true /* silent — don't restore normal UI yet */);

    // Fetch the session metadata to get status + agent info
    const session = await fetchJson(`/sessions/${encodeURIComponent(sessionId)}`);
    if (!session) {
        appendSystemMessage(`Failed to load sub-agent session ${sessionId.substring(0, 12)}…`, 'error');
        return;
    }

    // Determine the sub-agent lifecycle status.
    // The session object has session-level status (active/sealed).
    // The real lifecycle status comes from the activeSubAgents map (events.js).
    let rawStatus = 'Running';
    for (const sa of activeSubAgents.values()) {
        if (sa.sessionId === sessionId || sa.subAgentId === sessionId) {
            rawStatus = sa.status || 'Running';
            break;
        }
    }
    // Fallback: if session is sealed, show it accordingly
    if (rawStatus === 'Running' && (session.status || '').toLowerCase() === 'sealed') {
        rawStatus = 'Completed';
    }
    const statusInfo = SUBAGENT_STATUS_MAP[rawStatus] || SUBAGENT_STATUS_MAP.Running;
    const isTerminal = ['Completed', 'Failed', 'Killed', 'TimedOut', 'Expired', 'Sealed'].includes(rawStatus);

    // Use the current active channel's context for display (overlay model).
    // We stash the existing innerHTML so we can restore it on close.
    const ctx = channelManager.active;
    if (!ctx) return;

    _subAgentViewSessionId = sessionId;
    _subAgentViewCtx = ctx;

    // Highlight the viewed sub-agent in the sidebar
    document.querySelectorAll('.subagent-sidebar-item.viewing').forEach(el => el.classList.remove('viewing'));
    const sidebarItem = document.querySelector(`.subagent-sidebar-item[data-session-id="${sessionId}"]`);
    if (sidebarItem) sidebarItem.classList.add('viewing');

    // Save existing content for restore
    ctx._savedMessagesHtml = ctx.messagesEl.innerHTML;
    ctx._savedHistoryLoaded = ctx.historyLoaded;

    // Switch chat view to read-only mode
    const chatView = dom.chatView;
    chatView.classList.add('readonly-mode');

    // Update header
    const agentLabel = session.agentId || 'sub-agent';
    dom.chatTitle.textContent = `${agentLabel} — sub-agent session`;
    dom.chatMeta.textContent = `Read-only · ${rawStatus}`;

    // Clear messages area and insert banner + history
    ctx.messagesEl.innerHTML = '';

    // ─── Read-only banner ───
    const banner = document.createElement('div');
    banner.className = 'readonly-banner';
    banner.id = 'subagent-readonly-banner';
    let bannerHtml = `
        <span class="readonly-icon">🔒</span>
        <span class="readonly-text">Read-only — sub-agent session</span>
        <span class="readonly-status">${statusInfo.icon} ${escapeHtml(statusInfo.label)}</span>`;
    if (isTerminal) {
        bannerHtml += `<button class="readonly-seal-btn" id="btn-seal-subagent">Seal</button>`;
    }
    bannerHtml += `<button class="readonly-seal-btn" id="btn-close-subagent" title="Close sub-agent view">✕ Close</button>`;
    banner.innerHTML = bannerHtml;
    ctx.messagesEl.appendChild(banner);

    // ─── Fetch history ───
    const loading = document.createElement('div');
    loading.className = 'loading';
    loading.textContent = 'Loading sub-agent conversation...';
    ctx.messagesEl.appendChild(loading);

    const data = await fetchJson(`/sessions/${encodeURIComponent(sessionId)}/history?limit=200`);
    loading.remove();

    if (data && data.entries && data.entries.length > 0) {
        // Map SessionEntry format (role/content/timestamp/toolName) → history entry format
        const mapped = data.entries.map(e => ({
            role: typeof e.role === 'string' ? e.role.toLowerCase() : 'assistant',
            content: e.content || '',
            timestamp: e.timestamp,
            toolName: e.toolName || null,
            toolCallId: e.toolCallId || null
        }));
        renderHistoryBatch(mapped, null, ctx.messagesEl);
    } else {
        const empty = document.createElement('div');
        empty.className = 'message system-msg';
        empty.textContent = 'No messages in this sub-agent session.';
        ctx.messagesEl.appendChild(empty);
    }

    scrollToBottom(true, ctx.messagesEl);

    // ─── Wire banner buttons ───
    const sealBtn = ctx.messagesEl.querySelector('#btn-seal-subagent');
    if (sealBtn) {
        sealBtn.addEventListener('click', async () => {
            sealBtn.disabled = true;
            sealBtn.textContent = 'Sealing…';
            const ok = await sealSession(sessionId);
            if (ok) {
                closeSubAgentView();
                loadSessions();
            } else {
                sealBtn.disabled = false;
                sealBtn.textContent = 'Seal';
                appendSystemMessage('Failed to seal session.', 'error');
            }
        });
    }

    const closeBtn = ctx.messagesEl.querySelector('#btn-close-subagent');
    if (closeBtn) {
        closeBtn.addEventListener('click', () => closeSubAgentView());
    }

    // Register the session in the channel manager so SignalR events route here
    channelManager.registerSession(sessionId, ctx);
}

/**
 * Close the read-only sub-agent view and restore the normal chat canvas.
 * @param {boolean} silent — if true, don't restore DOM yet (used when re-opening another sub-agent)
 */
export function closeSubAgentView(silent = false) {
    if (!_subAgentViewSessionId) return;

    const ctx = _subAgentViewCtx;
    _subAgentViewSessionId = null;
    _subAgentViewCtx = null;

    if (!ctx || silent) return;

    // Remove readonly mode and sidebar highlight
    dom.chatView.classList.remove('readonly-mode');
    document.querySelectorAll('.subagent-sidebar-item.viewing').forEach(el => el.classList.remove('viewing'));

    // Restore saved messages content
    if (ctx._savedMessagesHtml !== undefined) {
        ctx.messagesEl.innerHTML = ctx._savedMessagesHtml;
        delete ctx._savedMessagesHtml;
    }
    if (ctx._savedHistoryLoaded !== undefined) {
        ctx.historyLoaded = ctx._savedHistoryLoaded;
        delete ctx._savedHistoryLoaded;
    }

    // Restore header to the normal agent view
    const agentId = getCurrentAgentId();
    const channelType = getCurrentChannelType();
    if (agentId) {
        dom.chatTitle.textContent = `${agentId} — ${channelDisplayName(channelType)}`;
        dom.chatMeta.textContent = `Agent: ${agentId} · ${channelDisplayName(channelType)}`;
    }

    scrollToBottom(false, ctx.messagesEl);
    updateSendButtonState();
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
    const ctx = channelManager.active;
    if (ctx) {
        ctx.showTools = showTools;
        setShowTools(ctx.agentId, ctx.channelType, showTools);
    } else {
        setShowTools(null, null, showTools);
    }
    applyToggleState(ctx?.messagesEl);
}

export function toggleThinkingVisibility() {
    showThinking = dom.toggleThinking.checked;
    const ctx = channelManager.active;
    if (ctx) {
        ctx.showThinking = showThinking;
        setShowThinking(ctx.agentId, ctx.channelType, showThinking);
    } else {
        setShowThinking(null, null, showThinking);
    }
    applyToggleState(ctx?.messagesEl);
}

function applyToggleState(container) {
    const el = container || getActiveMessagesEl();
    if (!el) return;
    el.querySelectorAll('.tool-call').forEach(tc => {
        tc.classList.toggle('hidden', !showTools);
    });
    el.querySelectorAll('.thinking-block').forEach(tb => {
        tb.classList.toggle('hidden', !showThinking);
        tb.classList.toggle('collapsed', !showThinking);
        const toggle = tb.querySelector('.thinking-toggle');
        if (toggle) {
            toggle.setAttribute('aria-expanded', showThinking);
            tb.querySelector('.thinking-chevron').textContent = showThinking ? '▾' : '▸';
        }
    });
}
