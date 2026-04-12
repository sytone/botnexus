// BotNexus WebUI — Shared UI utilities

// ── DOM Selectors ───────────────────────────────────────────────────

export const $ = (sel) => document.querySelector(sel);
export const $$ = (sel) => document.querySelectorAll(sel);

// DOM element cache — safe at module level because type="module" is deferred.
export const dom = {
    sessionsList:        $('#sessions-list'),
    agentsList:          $('#agents-list'),
    connectionStatus:    $('#connection-status'),
    statusText:          $('#connection-status')?.querySelector('.status-text'),
    worldIdentity:       $('#world-identity'),
    connectionBanner:    $('#connection-banner'),
    connectionBannerText: $('#connection-banner-text'),
    btnReconnect:        $('#btn-reconnect'),
    welcome:             $('#welcome-screen'),
    chatView:            $('#chat-view'),
    chatTitle:           $('#chat-title'),
    chatMeta:            $('#chat-meta'),
    chatMessages:        $('#chat-messages'),
    chatInput:           $('#chat-input'),
    btnSend:             $('#btn-send'),
    btnAbort:            $('#btn-abort'),
    agentSelect:         $('#agent-select'),
    modelSelect:         $('#model-select'),
    toggleTools:         $('#toggle-tools'),
    toggleThinking:      $('#toggle-thinking'),
    toggleActivity:      $('#toggle-activity'),
    activityFeed:        $('#activity-feed'),
    steerIndicator:      $('#steer-indicator'),
    followUpIndicator:   $('#followup-indicator'),
    toolModal:           $('#tool-modal'),
    modalClose:          $('#tool-modal')?.querySelector('.modal-close'),
    modalOverlay:        $('#tool-modal')?.querySelector('.modal-overlay'),
    agentFormModal:      $('#agent-form-modal'),
    agentForm:           $('#agent-form'),
    debugModal:          $('#debug-modal'),
    agentConfigView:     $('#agent-config-view'),
    cronView:            $('#cron-view'),
    confirmDialog:       $('#confirm-dialog'),
    sessionIdDisplay:    $('#session-id-display'),
    sessionIdText:       $('#session-id-text'),
    queueStatus:         $('#queue-status'),
    queueCount:          $('#queue-count'),
    activityItems:       $('#activity-items'),
    activityFilterAgent: $('#activity-filter-agent'),
    activityFilterType:  $('#activity-filter-type'),
    scrollBottom:        $('#btn-scroll-bottom'),
    newMessages:         $('#btn-new-messages'),
    sidebarToggle:       $('#btn-sidebar-toggle'),
    sidebarOverlay:      $('#sidebar-overlay'),
    sidebar:             $('#sidebar'),
    channelsList:        $('#channels-list'),
    extensionsList:      $('#extensions-list'),
    btnSendMode:         $('#btn-send-mode'),
    processingStatus:    $('#processing-status'),
    commandPalette:      $('#command-palette'),
    subAgentPanel:       $('#subagent-panel'),
    subAgentList:        $('#subagent-list'),
    subAgentCountBadge:  $('#subagent-count-badge'),
};

// ── HTML / Time Utilities ───────────────────────────────────────────

export function escapeHtml(str) {
    if (!str) return '';
    const div = document.createElement('div');
    div.textContent = str;
    return div.innerHTML;
}

export function formatTime(iso) {
    if (!iso) return '';
    try { return new Date(iso).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }); }
    catch { return ''; }
}

export function relativeTime(iso) {
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

// ── Markdown Rendering ──────────────────────────────────────────────

export function initMarkdown() {
    if (typeof marked !== 'undefined') {
        marked.setOptions({ breaks: false, gfm: true, headerIds: false, mangle: false });
    }
}

export function renderMarkdown(text) {
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

// ── Scroll Management ───────────────────────────────────────────────

let userScrolledUp = false;
let newMessageCount = 0;

export function scrollToBottom(force) {
    if (force || !userScrolledUp) {
        requestAnimationFrame(() => { dom.chatMessages.scrollTop = dom.chatMessages.scrollHeight; });
    }
    updateScrollButton();
}

export function updateScrollButton() {
    if (!dom.scrollBottom) return;
    const threshold = 80;
    const atBottom = dom.chatMessages.scrollHeight - dom.chatMessages.scrollTop - dom.chatMessages.clientHeight < threshold;
    userScrolledUp = !atBottom;
    dom.scrollBottom.classList.toggle('hidden', atBottom);
    if (atBottom) resetNewMessageCount();
}

export function incrementNewMessageCount() {
    if (!userScrolledUp) return;
    newMessageCount++;
    if (dom.newMessages) {
        const label = newMessageCount === 1 ? '↓ 1 new message' : `↓ ${newMessageCount} new messages`;
        dom.newMessages.textContent = label;
        dom.newMessages.classList.remove('hidden');
    }
}

export function resetNewMessageCount() {
    newMessageCount = 0;
    if (dom.newMessages) dom.newMessages.classList.add('hidden');
}

export function autoResize(el) {
    el.style.height = 'auto';
    el.style.height = Math.min(el.scrollHeight, 200) + 'px';
}

// ── Connection Status ───────────────────────────────────────────────

export function setStatus(state) {
    dom.connectionStatus.className = `status ${state}`;
    const labels = {
        connected: 'Connected', disconnected: 'Disconnected',
        connecting: 'Connecting...', reconnecting: 'Reconnecting...',
        online: 'Gateway Online'
    };
    dom.statusText.textContent = labels[state] || state;
}

export function showConnectionBanner(text, level = 'warning', showReconnectBtn = false) {
    dom.connectionBanner.className = `connection-banner ${level}`;
    dom.connectionBannerText.textContent = text;
    dom.btnReconnect.classList.toggle('hidden', !showReconnectBtn);
}

export function hideConnectionBanner() {
    dom.connectionBanner.className = 'connection-banner hidden';
    dom.connectionBannerText.textContent = '';
    dom.btnReconnect.classList.add('hidden');
}

// ── Processing Status Bar ───────────────────────────────────────────

export function renderProcessingStatus(isVisible, stage = '', icon = '⏳') {
    dom.processingStatus.classList.toggle('hidden', !isVisible);
    const label = $('#processing-label');
    if (!label) return;
    if (isVisible) {
        label.textContent = `${icon || '⏳'} ${stage || 'Processing...'}`;
        label.classList.remove('hidden');
        return;
    }
    label.textContent = '';
    label.classList.add('hidden');
}

// ── Steer / Follow-up Indicators ────────────────────────────────────

let steerTimer = null;

export function showSteerIndicator() {
    if (steerTimer) clearTimeout(steerTimer);
    dom.steerIndicator.classList.remove('hidden');
    steerTimer = setTimeout(() => {
        dom.steerIndicator.classList.add('hidden');
        steerTimer = null;
    }, 1500);
}

export function showFollowUpIndicator() {
    dom.followUpIndicator.classList.remove('hidden');
    setTimeout(() => dom.followUpIndicator.classList.add('hidden'), 1500);
}

// ── Confirm Dialog ──────────────────────────────────────────────────

let confirmCallback = null;

export function showConfirm(message, title, onConfirm, confirmLabel) {
    $('#confirm-title').textContent = title || 'Confirm';
    $('#confirm-message').textContent = message;
    $('#btn-confirm-ok').textContent = confirmLabel || 'OK';
    confirmCallback = onConfirm;
    dom.confirmDialog.classList.remove('hidden');
}

export function closeConfirm() {
    dom.confirmDialog.classList.add('hidden');
    confirmCallback = null;
}

export function getConfirmCallback() { return confirmCallback; }

// ── View Switching ──────────────────────────────────────────────────

export function showView(viewId) {
    ['welcome-screen', 'chat-view', 'agent-config-view', 'cron-view'].forEach(id => {
        const el = document.getElementById(id);
        if (el) el.classList.toggle('hidden', id !== viewId);
    });
}

// ── Copy Message Content ────────────────────────────────────────────

export function copyMessageContent(msgEl, btn) {
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

// ── Mobile Sidebar ──────────────────────────────────────────────────

export function toggleSidebar() {
    const isCollapsed = dom.sidebar.classList.toggle('collapsed');
    dom.sidebarOverlay.classList.toggle('hidden', isCollapsed);
    dom.sidebarToggle.setAttribute('aria-expanded', !isCollapsed);
}

export function closeSidebar() {
    dom.sidebar.classList.add('collapsed');
    dom.sidebarOverlay.classList.add('hidden');
    dom.sidebarToggle.setAttribute('aria-expanded', 'false');
}

// ── Section Toggles ─────────────────────────────────────────────────

export function initSectionToggles(onCronView) {
    $$('.section-header[data-toggle]').forEach(header => {
        header.addEventListener('click', (e) => {
            if (e.target.closest('.btn-icon') || e.target.closest('.toggle-switch')) return;
            const target = document.getElementById(header.dataset.toggle);
            if (target) target.classList.toggle('collapsed');
        });
    });
    $$('.section-header[data-view]').forEach(header => {
        header.addEventListener('click', () => {
            if (header.dataset.view === 'cron' && onCronView) onCronView();
        });
    });
}
