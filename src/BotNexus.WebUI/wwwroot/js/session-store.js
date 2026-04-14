// BotNexus WebUI — Channel-based state management
// Each agent+channelType pair gets a permanent ChannelContext with its own DOM.

import { normalizeChannelKey, channelDisplayName, serverLog } from './api.js';
import { dom } from './ui.js';
import { updateSessionIdDisplay, syncLoadingUiForActiveSession, updateSendButtonState } from './chat.js';
import { updateSidebarBadge } from './sidebar.js';
import { getShowTools, getShowThinking } from './storage.js';

// ── ChannelContext (replaces SessionStore) ───────────────────────────

export class ChannelContext {
    constructor(agentId, channelType) {
        const normalized = normalizeChannelKey(channelType);
        this.key = `${agentId}:${normalized}`;
        this.agentId = agentId;
        this.channelType = normalized;
        this.sessionId = null;
        this.containerEl = null;
        this.messagesEl = null;
        this.streamState = ChannelContext.createStreamState();
        this.showTools = getShowTools(agentId, channelType);
        this.showThinking = getShowThinking(agentId, channelType);
        this.unreadCount = 0;
        this.historyLoaded = false;
        this.lastViewed = null;
        this.timelineMeta = null;
    }

    static createStreamState() {
        return {
            isStreaming: false,
            showStreamingIndicator: false,
            activeMessageId: null,
            activeToolCalls: {},
            activeToolCount: 0,
            thinkingBuffer: '',
            toolCallDepth: 0,
            toolStartTimes: {},
            processingVisible: false,
            processingStage: '',
            processingIcon: '⏳',
        };
    }

    resetStreamState() { this.streamState = ChannelContext.createStreamState(); }
    get isStreaming() { return this.streamState.isStreaming; }
}

// Compat alias — callers that reference SessionStore still work
export const SessionStore = ChannelContext;

// ── ChannelManager (replaces SessionStoreManager) ────────────────────

export class ChannelManager {
    #contexts = new Map();      // key → ChannelContext
    #sessionIndex = new Map();  // sessionId → ChannelContext
    #activeKey = null;
    #selectedAgentId = null;
    #isRestRequestInFlight = false;
    #isSwitchingView = false;

    // --- Getters (compat + new) ---

    /** Compat: returns the active session id (matches old activeViewId). */
    get activeViewId() { return this.active?.sessionId || null; }
    /** Returns the active ChannelContext (compat alias: activeStore). */
    get activeStore() { return this.active; }
    get activeAgentId() { return this.active?.agentId || this.#selectedAgentId || null; }
    get activeKey() { return this.#activeKey; }
    get active() { return this.#activeKey ? this.#contexts.get(this.#activeKey) || null : null; }
    get isRestRequestInFlight() { return this.#isRestRequestInFlight; }
    get isSwitchingView() { return this.#isSwitchingView; }

    // --- Setters ---

    setRestRequestInFlight(v) { this.#isRestRequestInFlight = !!v; }
    setSwitchingView(v) { this.#isSwitchingView = !!v; }
    setSelectedAgent(agentId) { this.#selectedAgentId = agentId || null; }

    // --- Core methods ---

    /**
     * Return existing or create new ChannelContext for the agent+channel pair.
     * Creates the DOM container on first call.
     */
    getOrCreate(agentId, channelType) {
        const normalized = normalizeChannelKey(channelType);
        const key = `${agentId}:${normalized}`;
        if (this.#contexts.has(key)) return this.#contexts.get(key);

        const ctx = new ChannelContext(agentId, channelType);

        // Build DOM
        const viewsContainer = document.getElementById('channel-views');
        const container = document.createElement('div');
        container.className = 'channel-view';
        container.dataset.agentId = agentId;
        container.dataset.channelType = normalized;

        const messages = document.createElement('div');
        messages.className = 'channel-messages';
        messages.setAttribute('role', 'log');
        messages.setAttribute('aria-live', 'polite');
        messages.setAttribute('aria-label', 'Chat messages');
        container.appendChild(messages);

        viewsContainer.appendChild(container);
        ctx.containerEl = container;
        ctx.messagesEl = messages;

        this.#contexts.set(key, ctx);
        return ctx;
    }

    /** Map a sessionId to an existing ChannelContext. */
    registerSession(sessionId, ctx) {
        if (!sessionId || !ctx) return;
        this.#sessionIndex.set(sessionId, ctx);
        ctx.sessionId = sessionId;
    }

    /** Look up a context by sessionId. */
    getBySessionId(sessionId) {
        return this.#sessionIndex.get(sessionId) || null;
    }

    /** Activate the channel identified by key, toggling visibility. */
    activate(key) {
        this.#activeKey = key;
        const activeCtx = this.#contexts.get(key) || null;
        for (const ctx of this.#contexts.values()) {
            ctx.containerEl?.classList.toggle('active', ctx.key === key);
        }
        if (activeCtx) {
            this.#selectedAgentId = activeCtx.agentId;
            activeCtx.unreadCount = 0;
            activeCtx.lastViewed = new Date();
        }
    }

    /**
     * Route an incoming event to its ChannelContext.
     * Returns { ctx, isActive }. Does NOT modify unread count.
     */
    routeEvent(evt) {
        const sessionId = evt?.sessionId;
        if (!sessionId) {
            console.warn('routeEvent: event missing sessionId, dropping', evt);
            return { ctx: null, isActive: false };
        }

        // Try session index first
        let ctx = this.#sessionIndex.get(sessionId);
        if (!ctx) {
            const agentId = evt?.agentId || evt?.targetAgentId || this.#selectedAgentId;
            const channelType = evt?.channelType || this.active?.channelType;
            if (agentId && channelType) {
                ctx = this.getOrCreate(agentId, channelType);
                this.registerSession(sessionId, ctx);
            } else {
                console.warn('routeEvent: unknown session and no agent context to create one', sessionId, evt);
                serverLog('warn', 'routeEvent dropped — unknown session', { sessionId, eventType: evt?.type });
                return { ctx: null, isActive: false };
            }
        }

        const isActive = this.#activeKey === ctx.key;
        return { ctx, isActive };
    }

    // --- Compat methods for existing callers ---

    /** Compat: no-op (channels own their DOM permanently). */
    snapshotActiveView() { /* no-op */ }

    /** Compat: subscribe to a list of session infos. */
    subscribe(sessions) {
        for (const info of sessions) {
            if (!info.agentId) continue;
            const ctx = this.getOrCreate(info.agentId, info.channelType || 'web');
            if (info.sessionId) this.registerSession(info.sessionId, ctx);
        }
    }

    /**
     * Compat: get or create a context by sessionId + info.
     * Old callers provide sessionId as the primary key; we translate to
     * agentId+channelType keying and register the session mapping.
     */
    getOrCreateStore(sessionId, info = {}) {
        if (!sessionId) return null;

        // If already indexed, return existing context
        const existing = this.#sessionIndex.get(sessionId);
        if (existing) {
            if (info.agentId && !existing.agentId) existing.agentId = info.agentId;
            if (info.channelType && !existing.channelType) existing.channelType = normalizeChannelKey(info.channelType);
            return existing;
        }

        const agentId = info.agentId || this.#selectedAgentId || '_default';
        const channelType = info.channelType || 'web';
        const ctx = this.getOrCreate(agentId, channelType);
        this.registerSession(sessionId, ctx);
        return ctx;
    }

    /** Compat: find the context for a given agent + channel combination. */
    findStoreForAgent(agentId, channelType) {
        const normalized = normalizeChannelKey(channelType);
        const key = `${agentId}:${normalized}`;
        return this.#contexts.get(key) || null;
    }

    /** Compat: look up context by sessionId. */
    getStore(sessionId) { return this.getBySessionId(sessionId); }

    /** Compat: no-op (channels are permanent). */
    clearStore(_sessionId) { /* no-op — channels are permanent */ }

    /**
     * Compat: switchView activates the context associated with the sessionId.
     * Returns true if DOM was already present (always true now since channels
     * own their DOM), false if the sessionId is unknown.
     */
    switchView(sessionId) {
        const ctx = this.#sessionIndex.get(sessionId);
        if (!ctx) return false;
        this.activate(ctx.key);
        updateSessionIdDisplay();
        syncLoadingUiForActiveSession();
        updateSendButtonState();
        return true;
    }

    /**
     * Compat: set the active view by sessionId and/or agent+channel.
     */
    setActiveView(sessionId, agentId, channelType) {
        if (agentId && channelType) {
            const ctx = this.getOrCreate(agentId, channelType);
            if (sessionId) this.registerSession(sessionId, ctx);
            else if (sessionId === null) ctx.sessionId = null;
            this.activate(ctx.key);
        } else if (sessionId) {
            let ctx = this.#sessionIndex.get(sessionId);
            if (!ctx && agentId) {
                ctx = this.getOrCreate(agentId, channelType || 'web');
                this.registerSession(sessionId, ctx);
            }
            if (ctx) this.activate(ctx.key);
        }
        if (agentId) this.#selectedAgentId = agentId;
        syncLoadingUiForActiveSession();
    }
}

// Compat alias
export const SessionStoreManager = ChannelManager;

// ── Singletons ──────────────────────────────────────────────────────

export const channelManager = new ChannelManager();

// ── Shared channel state (compat) ───────────────────────────────────

export function getCurrentChannelType() { return channelManager.active?.channelType || null; }
export function setCurrentChannelType(_ct) { /* no-op — channel type is owned by ChannelContext */ }

// ── Convenience accessors ───────────────────────────────────────────

export function getCurrentSessionId() { return channelManager.active?.sessionId || null; }
export function getCurrentAgentId() { return channelManager.activeAgentId; }

export function getStreamState(sessionId) {
    if (sessionId) {
        const ctx = channelManager.getBySessionId(sessionId);
        if (ctx) return ctx.streamState;
    }
    const active = channelManager.active;
    if (active) return active.streamState;
    return ChannelContext.createStreamState();
}

export function isCurrentSessionStreaming() {
    return !!channelManager.active?.streamState?.isStreaming;
}

export function cleanupSessionState(sessionId) {
    if (!sessionId) return;
    const ctx = channelManager.getBySessionId(sessionId);
    if (ctx) ctx.resetStreamState();
}
