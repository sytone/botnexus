// BotNexus WebUI — Session state management

import { normalizeChannelKey } from './api.js';
import { dom } from './ui.js';
import { updateSessionIdDisplay, syncLoadingUiForActiveSession, updateSendButtonState } from './chat.js';
import { updateSidebarBadge } from './sidebar.js';
import { getShowTools, getShowThinking } from './storage.js';

// ── Shared channel state ────────────────────────────────────────────

let currentChannelType = null;
export function getCurrentChannelType() { return currentChannelType; }
export function setCurrentChannelType(ct) { currentChannelType = ct; }

// ── SessionStore ────────────────────────────────────────────────────

export class SessionStore {
    constructor(sessionId, info = {}) {
        this.sessionId = sessionId;
        this.agentId = info.agentId || null;
        this.channelType = info.channelType || null;
        this.streamState = SessionStore.createStreamState();
        this.cachedDom = null;
        this.timelineMeta = null;
        this.lastViewed = null;
        this.unreadCount = 0;
        // Per-channel toggle state — loaded from storage, falls back to true
        this.showTools = getShowTools(this.agentId, this.channelType);
        this.showThinking = getShowThinking(this.agentId, this.channelType);
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

    resetStreamState() { this.streamState = SessionStore.createStreamState(); }
    get isStreaming() { return this.streamState.isStreaming; }
}

// ── SessionStoreManager ─────────────────────────────────────────────

export class SessionStoreManager {
    #stores = new Map();
    #maxStores = 20;
    #activeViewId = null;
    #selectedAgentId = null;
    #isRestRequestInFlight = false;
    #isSwitchingView = false;

    get activeViewId() { return this.#activeViewId; }
    get activeStore() { return this.#activeViewId ? this.#stores.get(this.#activeViewId) || null : null; }
    get activeAgentId() { return this.activeStore?.agentId || this.#selectedAgentId || null; }
    get isRestRequestInFlight() { return this.#isRestRequestInFlight; }
    get isSwitchingView() { return this.#isSwitchingView; }

    setRestRequestInFlight(v) { this.#isRestRequestInFlight = !!v; }
    setSwitchingView(v) { this.#isSwitchingView = !!v; }
    setSelectedAgent(agentId) { this.#selectedAgentId = agentId || null; }

    subscribe(sessions) {
        for (const info of sessions) this.getOrCreateStore(info.sessionId, info);
    }

    switchView(sessionId) {
        if (this.#activeViewId && this.#activeViewId !== sessionId) {
            const oldStore = this.#stores.get(this.#activeViewId);
            if (oldStore && dom.chatMessages.children.length > 0) {
                oldStore.cachedDom = document.createDocumentFragment();
                while (dom.chatMessages.firstChild) {
                    oldStore.cachedDom.appendChild(dom.chatMessages.firstChild);
                }
                oldStore.timelineMeta = dom.chatMeta.textContent;
            }
        }

        this.#activeViewId = sessionId;
        const store = this.#stores.get(sessionId);
        if (!store) return false;
        store.unreadCount = 0;
        store.lastViewed = new Date();
        if (store.agentId) this.#selectedAgentId = store.agentId;
        currentChannelType = store.channelType;

        if (store.cachedDom) {
            dom.chatMessages.innerHTML = '';
            dom.chatMessages.appendChild(store.cachedDom);
            store.cachedDom = null;
            if (store.timelineMeta) dom.chatMeta.textContent = store.timelineMeta;
            updateSessionIdDisplay();
            updateSidebarBadge(sessionId, 0);
            syncLoadingUiForActiveSession();
            updateSendButtonState();
            return true;
        }

        updateSessionIdDisplay();
        syncLoadingUiForActiveSession();
        updateSendButtonState();
        return false;
    }

    routeEvent(evt) {
        const sessionId = evt?.sessionId;
        if (!sessionId) {
            console.warn('routeEvent: event missing sessionId, dropping', evt);
            return { isActive: false };
        }
        const store = this.getOrCreateStore(sessionId, {
            agentId: evt?.agentId || evt?.targetAgentId || this.#selectedAgentId,
            channelType: evt?.channelType || currentChannelType
        });
        if (evt?.agentId && !store.agentId) store.agentId = evt.agentId;
        if (evt?.channelType && !store.channelType) store.channelType = evt.channelType;

        if (!this.#activeViewId) {
            this.#activeViewId = sessionId;
            if (store.agentId) this.#selectedAgentId = store.agentId;
            if (store.channelType) currentChannelType = store.channelType;
            updateSessionIdDisplay();
            return { isActive: true };
        }

        const isActive = sessionId === this.#activeViewId;
        if (!isActive) {
            store.unreadCount++;
            updateSidebarBadge(sessionId, store.unreadCount);
        }
        return { isActive };
    }

    setActiveView(sessionId, agentId, channelType) {
        this.#activeViewId = sessionId || null;
        if (sessionId) {
            const store = this.getOrCreateStore(sessionId, { agentId, channelType });
            if (agentId) store.agentId = agentId;
            if (channelType) store.channelType = channelType;
            if (store.agentId) this.#selectedAgentId = store.agentId;
        } else if (agentId) {
            this.#selectedAgentId = agentId;
        }
        if (channelType) currentChannelType = channelType;
        syncLoadingUiForActiveSession();
    }

    getOrCreateStore(sessionId, info = {}) {
        if (!sessionId) return null;
        if (this.#stores.has(sessionId)) {
            const s = this.#stores.get(sessionId);
            this.#stores.delete(sessionId);
            this.#stores.set(sessionId, s);
            if (info.agentId && !s.agentId) s.agentId = info.agentId;
            if (info.channelType && !s.channelType) s.channelType = info.channelType;
            return s;
        }
        const store = new SessionStore(sessionId, info);
        this.#stores.set(sessionId, store);
        if (this.#stores.size > this.#maxStores) {
            for (const [id, s] of this.#stores) {
                if (!s.isStreaming && id !== this.#activeViewId) {
                    this.#stores.delete(id);
                    break;
                }
            }
        }
        return store;
    }

    findStoreForAgent(agentId, channelType) {
        const normalized = normalizeChannelKey(channelType);
        let latest = null;
        for (const store of this.#stores.values()) {
            if (store.agentId === agentId && normalizeChannelKey(store.channelType) === normalized) {
                latest = store;
            }
        }
        return latest;
    }

    getStore(sessionId) { return this.#stores.get(sessionId) || null; }
    clearStore(sessionId) { this.#stores.delete(sessionId); }
}

// ── Singleton ───────────────────────────────────────────────────────

export const storeManager = new SessionStoreManager();

// ── Convenience accessors ───────────────────────────────────────────

export function getCurrentSessionId() { return storeManager.activeViewId; }
export function getCurrentAgentId() { return storeManager.activeAgentId; }

export function getStreamState(sessionId) {
    const sid = sessionId ?? storeManager.activeViewId;
    if (!sid) return SessionStore.createStreamState();
    return storeManager.getOrCreateStore(sid).streamState;
}

export function isCurrentSessionStreaming() {
    return !!storeManager.activeStore?.streamState?.isStreaming;
}

export function cleanupSessionState(sessionId) {
    if (!sessionId) return;
    const store = storeManager.getStore(sessionId);
    if (store) store.resetStreamState();
}
