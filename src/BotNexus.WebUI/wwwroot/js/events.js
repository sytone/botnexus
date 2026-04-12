// BotNexus WebUI — SignalR event handlers

import { debugLog } from './api.js';
import {
    storeManager, getStreamState, getCurrentSessionId, getCurrentAgentId,
    cleanupSessionState, getCurrentChannelType
} from './session-store.js';
import { hubInvoke, setConnectionId } from './hub.js';
import { setStatus, hideConnectionBanner } from './ui.js';
// Circular-import–safe: hoisted function declarations used only at call-time.
import {
    onMessageStart, onContentDelta, onThinkingDelta, onToolStart, onToolEnd,
    onMessageEnd, onError, updateSessionIdDisplay, syncLoadingUiForActiveSession,
    appendSystemMessage, clearChatMessages, clearSubAgentPanel, renderSubAgentPanel
} from './chat.js';
import { loadSessions, setAgentsCache, trackActivity } from './sidebar.js';

// ── Sub-agent state ─────────────────────────────────────────────────

export const activeSubAgents = new Map();
export function clearActiveSubAgents() { activeSubAgents.clear(); }

// ── Handler registration ────────────────────────────────────────────

export function registerEventHandlers(connection) {

    connection.on('Connected', (data) => {
        setConnectionId(data.connectionId);
        setAgentsCache(data.agents || []);
        setStatus('connected');
        hideConnectionBanner();
        debugLog('lifecycle', 'Connected! connectionId:', data.connectionId);

        hubInvoke('SubscribeAll').then(result => {
            if (result?.sessions) {
                storeManager.subscribe(result.sessions);
                debugLog('lifecycle', `SubscribeAll: ${result.sessions.length} sessions`);
            }
        }).catch(err => {
            debugLog('lifecycle', 'SubscribeAll failed:', err.message);
        });
    });

    connection.on('SessionReset', (data) => {
        const sid = data?.sessionId || storeManager.activeViewId;
        cleanupSessionState(sid);
        if (sid !== storeManager.activeViewId) return;
        storeManager.setActiveView(null, getCurrentAgentId(), getCurrentChannelType());
        updateSessionIdDisplay();
        syncLoadingUiForActiveSession();
        clearSubAgentPanel();
        clearChatMessages();
        appendSystemMessage('Session reset. System prompt regenerated.');
        loadSessions();
    });

    // ── Stream lifecycle ────────────────────────────────────────────

    connection.on('MessageStart', (evt) => {
        const sid = evt?.sessionId || storeManager.activeViewId;
        {
            const ss = getStreamState(sid);
            ss.activeMessageId = evt.messageId;
            ss.isStreaming = true;
            ss.activeToolCount = 0;
            ss.thinkingBuffer = '';
            ss.toolCallDepth = 0;
            ss.toolStartTimes = {};
        }
        const { isActive } = storeManager.routeEvent(evt);
        if (!isActive) return;
        onMessageStart(evt, sid);
    });

    connection.on('ContentDelta', (evt) => {
        const { isActive } = storeManager.routeEvent(evt);
        if (!isActive) return;
        const text = typeof evt === 'string' ? evt : (evt?.contentDelta || evt?.delta || '');
        if (text) onContentDelta(text);
    });

    connection.on('ThinkingDelta', (evt) => {
        const sid = evt?.sessionId || storeManager.activeViewId;
        const ss = getStreamState(sid);
        const text = evt?.thinkingContent || evt?.delta || '';
        if (text) ss.thinkingBuffer += text;
        const { isActive } = storeManager.routeEvent(evt);
        if (!isActive) return;
        if (text) onThinkingDelta(text);
    });

    connection.on('ToolStart', (evt) => {
        const sid = evt?.sessionId || storeManager.activeViewId;
        const { isActive } = storeManager.routeEvent(evt);
        if (!isActive) {
            const ss = getStreamState(sid);
            const callId = evt.toolCallId || `tc-${Date.now()}`;
            ss.activeToolCount++;
            ss.toolStartTimes[callId] = Date.now();
            ss.activeToolCalls[callId] = {
                toolName: evt.toolName || 'unknown', args: evt.toolArgs || '',
                result: '', status: 'running', depth: evt.depth || ss.toolCallDepth
            };
            return;
        }
        onToolStart(evt);
    });

    connection.on('ToolEnd', (evt) => {
        const sid = evt?.sessionId || storeManager.activeViewId;
        const { isActive } = storeManager.routeEvent(evt);
        if (!isActive) {
            const ss = getStreamState(sid);
            const callId = evt.toolCallId || 'unknown';
            if (ss.activeToolCalls[callId]) {
                ss.activeToolCalls[callId].result = evt.toolResult || '';
                ss.activeToolCalls[callId].status = evt.toolIsError ? 'error' : 'complete';
            }
            delete ss.toolStartTimes[callId];
            return;
        }
        onToolEnd(evt);
    });

    connection.on('MessageEnd', (evt) => {
        const sid = evt?.sessionId || storeManager.activeViewId;
        const ss = getStreamState(sid);
        ss.isStreaming = false;
        ss.activeMessageId = null;
        const { isActive } = storeManager.routeEvent(evt);
        if (!isActive) {
            ss.activeToolCalls = {};
            ss.activeToolCount = 0;
            ss.toolCallDepth = 0;
            ss.toolStartTimes = {};
            ss.thinkingBuffer = '';
            ss.showStreamingIndicator = false;
            ss.processingVisible = false;
            ss.processingStage = '';
            ss.processingIcon = '⏳';
            return;
        }
        onMessageEnd(evt);
    });

    connection.on('Error', (evt) => {
        const sid = evt?.sessionId || storeManager.activeViewId;
        getStreamState(sid).isStreaming = false;
        const { isActive } = storeManager.routeEvent(evt);
        if (!isActive) return;
        onError(evt);
    });

    // ── Sub-agent lifecycle ─────────────────────────────────────────

    connection.on('SubAgentSpawned', (evt) => {
        const { isActive } = storeManager.routeEvent(evt);
        if (!evt?.subAgentId) return;
        activeSubAgents.set(evt.subAgentId, {
            subAgentId: evt.subAgentId,
            name: evt.name || evt.subAgentId,
            task: evt.task || '',
            model: evt.model || '',
            status: 'Running',
            startedAt: evt.startedAt || new Date().toISOString(),
            completedAt: null, turnsUsed: 0, resultSummary: null
        });
        if (!isActive) return;
        renderSubAgentPanel();
        trackActivity('tool', getCurrentAgentId(), `🚀 Sub-agent spawned: ${evt.name || evt.subAgentId}`);
    });

    connection.on('SubAgentCompleted', (evt) => {
        const { isActive } = storeManager.routeEvent(evt);
        if (!evt?.subAgentId) return;
        const sa = activeSubAgents.get(evt.subAgentId);
        if (sa) {
            sa.status = 'Completed';
            sa.completedAt = evt.completedAt || new Date().toISOString();
            sa.turnsUsed = evt.turnsUsed || sa.turnsUsed;
            sa.resultSummary = evt.resultSummary || null;
        }
        if (!isActive) return;
        renderSubAgentPanel();
        trackActivity('response', getCurrentAgentId(), `✅ Sub-agent completed: ${evt.name || evt.subAgentId}`);
    });

    connection.on('SubAgentFailed', (evt) => {
        const { isActive } = storeManager.routeEvent(evt);
        if (!evt?.subAgentId) return;
        const sa = activeSubAgents.get(evt.subAgentId);
        if (sa) {
            sa.status = evt.timedOut ? 'TimedOut' : 'Failed';
            sa.completedAt = evt.completedAt || new Date().toISOString();
            sa.resultSummary = evt.error || evt.resultSummary || null;
        }
        if (!isActive) return;
        renderSubAgentPanel();
        const icon = evt.timedOut ? '⏱' : '❌';
        trackActivity('error', getCurrentAgentId(), `${icon} Sub-agent failed: ${evt.name || evt.subAgentId}`);
    });

    connection.on('SubAgentKilled', (evt) => {
        const { isActive } = storeManager.routeEvent(evt);
        if (!evt?.subAgentId) return;
        const sa = activeSubAgents.get(evt.subAgentId);
        if (sa) {
            sa.status = 'Killed';
            sa.completedAt = evt.completedAt || new Date().toISOString();
        }
        if (!isActive) return;
        renderSubAgentPanel();
        trackActivity('tool', getCurrentAgentId(), `🛑 Sub-agent killed: ${evt.name || evt.subAgentId}`);
    });
}
