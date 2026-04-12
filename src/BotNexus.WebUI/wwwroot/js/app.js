// BotNexus WebUI — Entry point (ES module)
// Wires all modules together: hub, events, sidebar, chat, DOM listeners.

import { API_BASE, initVersionCheck } from './api.js';
import {
    dom, $, initMarkdown, scrollToBottom, updateScrollButton, resetNewMessageCount,
    autoResize, setStatus, showConnectionBanner, hideConnectionBanner,
    showConfirm, closeConfirm, getConfirmCallback, showView, copyMessageContent,
    toggleSidebar, closeSidebar, initSectionToggles
} from './ui.js';
import {
    storeManager, getCurrentSessionId, getCurrentAgentId, isCurrentSessionStreaming
} from './session-store.js';
import { initHub, getConnection, manualReconnect } from './hub.js';
import { registerEventHandlers } from './events.js';
import {
    initSidebar, loadSessions, loadChannels, loadExtensions, loadAgents,
    openAddAgentForm, closeAgentForm, saveAgent, loadModelsForProvider,
    showAddCronForm, applyActivityFilters, toggleActivity,
    closeDebugModal, openCronView
} from './sidebar.js';
import {
    sendMessage, abortRequest, startNewChat, updateSendButtonState,
    toggleSendMode, updateSessionIdDisplay, copySessionId,
    showCommandPalette, hideCommandPalette, isCommandPaletteVisible,
    navigateCommandPalette, acceptCommandPalette, executeReset,
    toggleToolVisibility, toggleThinkingVisibility,
    openToolModal, closeToolModal, handleModelChange,
    appendSystemMessage, initSubAgentPanel, openAgentTimeline
} from './chat.js';
import { getShowTools, getShowThinking, getLastContext } from './storage.js';

// ── Gateway health check ────────────────────────────────────────────

let gatewayHealthy = false;
let healthCheckInterval = null;

async function checkGatewayHealth() {
    try {
        const response = await fetch('/health');
        const wasHealthy = gatewayHealthy;
        gatewayHealthy = response.ok;
        const connection = getConnection();
        if (!connection || connection.state !== signalR.HubConnectionState.Connected) {
            setStatus(gatewayHealthy ? 'online' : 'disconnected');
        }
        if (wasHealthy !== gatewayHealthy) {
            if (!gatewayHealthy) {
                showConnectionBanner('⚠️ Gateway offline', 'warning');
            } else if (!getCurrentAgentId()) {
                hideConnectionBanner();
            }
        }
    } catch {
        gatewayHealthy = false;
        const connection = getConnection();
        if (!connection || connection.state !== signalR.HubConnectionState.Connected) {
            setStatus('disconnected');
        }
    }
}

function startHealthCheck() {
    if (healthCheckInterval) return;
    checkGatewayHealth();
    healthCheckInterval = setInterval(checkGatewayHealth, 15000);
}

// ── Event listeners ─────────────────────────────────────────────────

function initEventListeners() {
    dom.btnSend.addEventListener('click', sendMessage);
    dom.btnAbort.addEventListener('click', abortRequest);

    dom.chatInput.addEventListener('keydown', (e) => {
        if (isCommandPaletteVisible()) {
            if (e.key === 'ArrowDown') { e.preventDefault(); navigateCommandPalette(1); return; }
            if (e.key === 'ArrowUp') { e.preventDefault(); navigateCommandPalette(-1); return; }
            if (e.key === 'Tab' || (e.key === 'Enter' && !e.shiftKey)) { e.preventDefault(); acceptCommandPalette(); return; }
            if (e.key === 'Escape') { e.preventDefault(); hideCommandPalette(); return; }
        }
        if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); sendMessage(); }
    });

    dom.chatInput.addEventListener('input', () => {
        autoResize(dom.chatInput);
        updateSendButtonState();
        const text = dom.chatInput.value;
        if (text.startsWith('/') && !text.includes(' ')) {
            showCommandPalette(text);
        } else {
            hideCommandPalette();
        }
    });

    dom.agentSelect.addEventListener('change', () => {
        const newAgent = dom.agentSelect.value;
        const hasMessages = dom.chatMessages.children.length > 0;
        if (hasMessages && getCurrentSessionId() && getCurrentAgentId() && newAgent !== getCurrentAgentId()) {
            showConfirm(
                `Switch to agent "${newAgent}"? This will start a new session.`,
                'Switch Agent',
                () => { dom.agentSelect.value = newAgent; startNewChat(); },
                'Switch'
            );
            dom.agentSelect.value = getCurrentAgentId();
            return;
        }
        startNewChat();
    });

    dom.toggleTools.addEventListener('change', toggleToolVisibility);
    dom.toggleThinking.addEventListener('change', toggleThinkingVisibility);
    // Restore toggle state from storage and sync checkboxes
    dom.toggleTools.checked = getShowTools();
    dom.toggleThinking.checked = getShowThinking();
    toggleToolVisibility();
    toggleThinkingVisibility();
    dom.toggleActivity.addEventListener('change', toggleActivity);

    $('#btn-refresh-sessions').addEventListener('click', (e) => { e.stopPropagation(); loadSessions(); });
    $('#btn-refresh-channels').addEventListener('click', (e) => { e.stopPropagation(); loadChannels(); });
    $('#btn-refresh-extensions').addEventListener('click', (e) => { e.stopPropagation(); loadExtensions(); });
    $('#btn-refresh-agents').addEventListener('click', (e) => { e.stopPropagation(); loadAgents(); });

    $('#btn-stop-gateway').addEventListener('click', () => {
        showConfirm(
            'Restart the gateway? Active sessions will be interrupted.',
            'Restart Gateway',
            async () => {
                try { await fetch(`${API_BASE}/gateway/shutdown`, { method: 'POST' }); } catch {}
                appendSystemMessage('Gateway restart initiated.');
            }
        );
    });

    // Delegated click handlers for dynamic chat content
    dom.chatMessages.addEventListener('click', (e) => {
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
        if (toolCall) { toolCall.classList.toggle('expanded'); return; }
    });

    dom.scrollBottom.addEventListener('click', () => scrollToBottom(true));
    dom.chatMessages.addEventListener('scroll', updateScrollButton);
    dom.newMessages.addEventListener('click', () => { scrollToBottom(true); resetNewMessageCount(); });

    dom.btnReconnect.addEventListener('click', manualReconnect);
    $('#btn-copy-session-id').addEventListener('click', copySessionId);

    dom.activityFilterAgent.addEventListener('change', applyActivityFilters);
    dom.activityFilterType.addEventListener('change', applyActivityFilters);

    dom.modalClose.addEventListener('click', closeToolModal);
    dom.modalOverlay.addEventListener('click', closeToolModal);

    $('#btn-add-agent').addEventListener('click', (e) => { e.stopPropagation(); openAddAgentForm(); });
    dom.agentFormModal.querySelector('.agent-form-close').addEventListener('click', closeAgentForm);
    dom.agentFormModal.querySelector('.agent-form-overlay').addEventListener('click', closeAgentForm);

    dom.debugModal.querySelector('.debug-modal-close').addEventListener('click', closeDebugModal);
    dom.debugModal.querySelector('.debug-modal-overlay').addEventListener('click', closeDebugModal);

    $('#btn-cancel-agent').addEventListener('click', closeAgentForm);
    $('#btn-save-agent').addEventListener('click', saveAgent);

    $('#btn-add-cron').addEventListener('click', showAddCronForm);

    $('#form-agent-provider').addEventListener('change', () => { loadModelsForProvider($('#form-agent-provider').value); });
    $('#form-agent-temperature-enabled').addEventListener('change', (e) => { $('#form-agent-temperature').disabled = !e.target.checked; });
    $('#form-agent-max-tokens-enabled').addEventListener('change', (e) => { $('#form-agent-max-tokens').disabled = !e.target.checked; });

    dom.modelSelect.addEventListener('change', handleModelChange);

    $('#btn-confirm-ok').addEventListener('click', () => { const cb = getConfirmCallback(); if (cb) cb(); closeConfirm(); });
    $('#btn-confirm-cancel').addEventListener('click', closeConfirm);
    dom.confirmDialog.querySelector('.confirm-overlay').addEventListener('click', closeConfirm);

    dom.btnSendMode.addEventListener('click', toggleSendMode);

    dom.sidebarToggle.addEventListener('click', toggleSidebar);
    dom.sidebarOverlay.addEventListener('click', closeSidebar);
    window.addEventListener('popstate', () => { void handleHistoryNavigation(); });

    // Global keyboard shortcuts
    document.addEventListener('keydown', (e) => {
        if ((e.ctrlKey || e.metaKey) && e.key === 'k') {
            e.preventDefault();
            if (!dom.chatView.classList.contains('hidden')) {
                dom.chatInput.focus();
                dom.chatInput.value = '/';
                showCommandPalette('/');
            }
            return;
        }
        if (e.key === 'Escape') {
            if (isCommandPaletteVisible()) { hideCommandPalette(); return; }
            if (!dom.agentConfigView.classList.contains('hidden')) { showView('welcome-screen'); return; }
            if (!dom.cronView.classList.contains('hidden')) { showView('welcome-screen'); return; }
            if (!dom.toolModal.classList.contains('hidden')) { closeToolModal(); return; }
            if (!dom.debugModal.classList.contains('hidden')) { closeDebugModal(); return; }
            if (!dom.agentFormModal.classList.contains('hidden')) { closeAgentForm(); return; }
            if (!dom.confirmDialog.classList.contains('hidden')) { closeConfirm(); return; }
            if (isCurrentSessionStreaming()) { abortRequest(); return; }
        }
    });
}

function hasAgentOption(agentId) {
    return !!agentId && Array.from(dom.agentSelect.options).some(o => o.value === agentId);
}

async function waitForSidebarLoad(agentId = null) {
    for (let i = 0; i < 30; i++) {
        const hasAnyAgents = dom.agentSelect.options.length > 0;
        if (hasAnyAgents && (!agentId || hasAgentOption(agentId))) return;
        await new Promise(resolve => setTimeout(resolve, 100));
    }
}

async function handleHashRoute() {
    const match = location.hash.match(/^#\/agents\/([^/]+)\/channels\/([^/]+)$/);
    if (!match) return false;

    const agentId = decodeURIComponent(match[1]);
    const channelType = decodeURIComponent(match[2]);
    await waitForSidebarLoad(agentId);
    await openAgentTimeline(agentId, channelType);
    return true;
}

async function handleHistoryNavigation() {
    const routedFromHash = await handleHashRoute();
    if (!routedFromHash) showView('welcome-screen');
}

async function restoreInitialView() {
    const routedFromHash = await handleHashRoute();
    if (routedFromHash) return;

    const last = getLastContext();
    if (!last.agentId || !last.channelType) return;

    await waitForSidebarLoad(last.agentId);
    await openAgentTimeline(last.agentId, last.channelType, last.sessionId);
}

// ── Init ─────────────────────────────────────────────────────────────

function init() {
    initMarkdown();
    initSectionToggles(openCronView);
    initEventListeners();
    initSidebar();
    startHealthCheck();
    setStatus('online');
    updateSendButtonState();
    initHub(registerEventHandlers);
    initSubAgentPanel();
    initVersionCheck();
    void restoreInitialView();

    if (window.innerWidth <= 768) {
        dom.sidebar.classList.add('collapsed');
        dom.sidebarOverlay.classList.add('hidden');
    }
}

if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
} else {
    init();
}
