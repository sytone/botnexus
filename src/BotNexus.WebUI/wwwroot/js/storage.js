// ── storage.js — Centralized localStorage management ────────────────
// Single source of truth for all persisted UI state.
// All keys namespaced under 'botnexus:' to avoid collisions.

const PREFIX = 'botnexus:';

const KEYS = {
    collapsedAgents: `${PREFIX}collapsed-agents`,
    showTools: `${PREFIX}show-tools`,
    showThinking: `${PREFIX}show-thinking`,
    lastAgent: `${PREFIX}last-agent`,
    lastChannel: `${PREFIX}last-channel`,
    lastSessionId: `${PREFIX}last-session-id`,
    sendMode: `${PREFIX}send-mode`,
    selectedModel: `${PREFIX}selected-model`,
    agentCache: `${PREFIX}agent-cache`,
    sessionCache: `${PREFIX}session-cache`,
};

// ── Primitive Helpers ──────────────────────────────────────────────

function getJson(key, fallback = null) {
    try {
        const raw = localStorage.getItem(key);
        return raw !== null ? JSON.parse(raw) : fallback;
    } catch { return fallback; }
}

function setJson(key, value) {
    localStorage.setItem(key, JSON.stringify(value));
}

function getBool(key, fallback = true) {
    const raw = localStorage.getItem(key);
    if (raw === null) return fallback;
    return raw !== 'false';
}

function setBool(key, value) {
    localStorage.setItem(key, String(!!value));
}

function getString(key, fallback = null) {
    return localStorage.getItem(key) || fallback;
}

function setString(key, value) {
    if (value == null) localStorage.removeItem(key);
    else localStorage.setItem(key, value);
}

// ── Sidebar Collapse State ─────────────────────────────────────────

export function getCollapsedAgents() {
    return new Set(getJson(KEYS.collapsedAgents, []));
}

export function setCollapsedAgents(agentSet) {
    setJson(KEYS.collapsedAgents, [...agentSet]);
}

export function toggleAgentCollapsed(displayName, isCollapsed) {
    const set = getCollapsedAgents();
    if (isCollapsed) set.add(displayName);
    else set.delete(displayName);
    setCollapsedAgents(set);
}

// ── Toggle State ───────────────────────────────────────────────────

export function getShowTools() { return getBool(KEYS.showTools, true); }
export function setShowTools(v) { setBool(KEYS.showTools, v); }

export function getShowThinking() { return getBool(KEYS.showThinking, true); }
export function setShowThinking(v) { setBool(KEYS.showThinking, v); }

// ── Last Active Context (resume on reload) ─────────────────────────

export function getLastContext() {
    return {
        agentId: getString(KEYS.lastAgent),
        channelType: getString(KEYS.lastChannel),
        sessionId: getString(KEYS.lastSessionId),
    };
}

export function setLastContext(agentId, channelType, sessionId) {
    setString(KEYS.lastAgent, agentId);
    setString(KEYS.lastChannel, channelType);
    setString(KEYS.lastSessionId, sessionId);
}

export function clearLastContext() {
    localStorage.removeItem(KEYS.lastAgent);
    localStorage.removeItem(KEYS.lastChannel);
    localStorage.removeItem(KEYS.lastSessionId);
}

// ── Send Mode ──────────────────────────────────────────────────────

export function getSendMode() { return getString(KEYS.sendMode, 'chat'); }
export function setSendMode(mode) { setString(KEYS.sendMode, mode); }

// ── Model Selection ────────────────────────────────────────────────

export function getSelectedModel() { return getString(KEYS.selectedModel); }
export function setSelectedModel(modelId) { setString(KEYS.selectedModel, modelId); }

// ── API Response Caching ───────────────────────────────────────────
// Cached data for instant initial render; refreshed in background.

const CACHE_TTL_MS = 5 * 60 * 1000; // 5 minutes

function getCached(key) {
    const entry = getJson(key);
    if (!entry || !entry.data) return null;
    if (Date.now() - entry.ts > CACHE_TTL_MS) return null;
    return entry.data;
}

function setCached(key, data) {
    setJson(key, { data, ts: Date.now() });
}

export function getCachedAgents() { return getCached(KEYS.agentCache); }
export function setCachedAgents(agents) { setCached(KEYS.agentCache, agents); }

export function getCachedSessions() { return getCached(KEYS.sessionCache); }
export function setCachedSessions(sessions) { setCached(KEYS.sessionCache, sessions); }

// ── Cleanup ────────────────────────────────────────────────────────

export function clearAll() {
    Object.values(KEYS).forEach(k => localStorage.removeItem(k));
}
