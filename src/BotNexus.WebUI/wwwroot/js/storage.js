// ═══════════════════════════════════════════════════════════════════
// storage.js — Centralized localStorage management
// ═══════════════════════════════════════════════════════════════════
//
// Two categories of settings:
//   1. GENERAL — applies globally across all channels/tabs
//   2. CHANNEL — per agent+channel, so each conversation keeps its own prefs
//
// Multi-tab safety: general settings are shared across tabs (same prefs
// everywhere). Channel settings are keyed by agent+channel so tabs
// viewing different agents don't interfere. All values are read once
// at init or view-switch — no live polling between tabs.
//
// All keys namespaced under 'botnexus:' to avoid collisions.

const PREFIX = 'botnexus:';

// ── Primitive Helpers (private) ────────────────────────────────────

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

// ═══════════════════════════════════════════════════════════════════
// GENERAL SETTINGS — shared across all channels and tabs
// ═══════════════════════════════════════════════════════════════════

const GENERAL = {
    sidebarCollapsed:  `${PREFIX}sidebar-collapsed`,
    collapsedAgents:   `${PREFIX}collapsed-agents`,
    collapsedSections: `${PREFIX}collapsed-sections`,
    sendMode:          `${PREFIX}send-mode`,
    selectedModel:     `${PREFIX}selected-model`,
    lastAgent:         `${PREFIX}last-agent`,
    lastChannel:       `${PREFIX}last-channel`,
    lastSessionId:     `${PREFIX}last-session-id`,
    agentCache:        `${PREFIX}agent-cache`,
    sessionCache:      `${PREFIX}session-cache`,
};

// ── Sidebar ────────────────────────────────────────────────────────

export function getSidebarCollapsed() { return getBool(GENERAL.sidebarCollapsed, false); }
export function setSidebarCollapsed(v) { setBool(GENERAL.sidebarCollapsed, v); }

export function getCollapsedAgents() {
    return new Set(getJson(GENERAL.collapsedAgents, []));
}

export function setCollapsedAgents(agentSet) {
    setJson(GENERAL.collapsedAgents, [...agentSet]);
}

export function toggleAgentCollapsed(displayName, isCollapsed) {
    const set = getCollapsedAgents();
    if (isCollapsed) set.add(displayName);
    else set.delete(displayName);
    setCollapsedAgents(set);
}

// ── Section Collapse ────────────────────────────────────────────────

export function getCollapsedSections() {
    return new Set(getJson(GENERAL.collapsedSections, []));
}

export function setCollapsedSections(sectionSet) {
    setJson(GENERAL.collapsedSections, [...sectionSet]);
}

export function toggleSectionCollapsed(sectionId, isCollapsed) {
    const set = getCollapsedSections();
    if (isCollapsed) set.add(sectionId);
    else set.delete(sectionId);
    setCollapsedSections(set);
}

// ── Send Mode ──────────────────────────────────────────────────────

export function getSendMode() { return getString(GENERAL.sendMode, 'chat'); }
export function setSendMode(mode) { setString(GENERAL.sendMode, mode); }

// ── Model Selection ────────────────────────────────────────────────

export function getSelectedModel() { return getString(GENERAL.selectedModel); }
export function setSelectedModel(modelId) { setString(GENERAL.selectedModel, modelId); }

// ── Last Active Context ────────────────────────────────────────────
// Read once at startup as fallback when no hash route is present.
// Each tab's runtime state is in-memory (channelManager).

export function getLastContext() {
    return {
        agentId: getString(GENERAL.lastAgent),
        channelType: getString(GENERAL.lastChannel),
        sessionId: getString(GENERAL.lastSessionId),
    };
}

export function setLastContext(agentId, channelType, sessionId) {
    setString(GENERAL.lastAgent, agentId);
    setString(GENERAL.lastChannel, channelType);
    setString(GENERAL.lastSessionId, sessionId);
}

export function clearLastContext() {
    localStorage.removeItem(GENERAL.lastAgent);
    localStorage.removeItem(GENERAL.lastChannel);
    localStorage.removeItem(GENERAL.lastSessionId);
}

// ── API Response Caching ───────────────────────────────────────────
// Instant initial render; refreshed in background. 5-minute TTL.

const CACHE_TTL_MS = 5 * 60 * 1000;

function getCached(key) {
    const entry = getJson(key);
    if (!entry || !entry.data) return null;
    if (Date.now() - entry.ts > CACHE_TTL_MS) return null;
    return entry.data;
}

function setCached(key, data) {
    setJson(key, { data, ts: Date.now() });
}

export function getCachedAgents() { return getCached(GENERAL.agentCache); }
export function setCachedAgents(agents) { setCached(GENERAL.agentCache, agents); }

export function getCachedSessions() { return getCached(GENERAL.sessionCache); }
export function setCachedSessions(sessions) { setCached(GENERAL.sessionCache, sessions); }


// ═══════════════════════════════════════════════════════════════════
// CHANNEL SETTINGS — per agent+channel, independent across tabs
// ═══════════════════════════════════════════════════════════════════
//
// Each channel (e.g., nova/signalr, aurum/signalr) stores its own
// preferences. Tabs viewing different channels read different values.
// Format: botnexus:ch:{agentId}:{channelType}:{setting}

function channelKey(agentId, channelType, setting) {
    const a = (agentId || '_').toLowerCase();
    const c = (channelType || '_').toLowerCase();
    return `${PREFIX}ch:${a}:${c}:${setting}`;
}

// ── Tool & Thinking Visibility ─────────────────────────────────────

export function getShowTools(agentId, channelType) {
    return getBool(channelKey(agentId, channelType, 'show-tools'), true);
}

export function setShowTools(agentId, channelType, v) {
    setBool(channelKey(agentId, channelType, 'show-tools'), v);
}

export function getShowThinking(agentId, channelType) {
    return getBool(channelKey(agentId, channelType, 'show-thinking'), true);
}

export function setShowThinking(agentId, channelType, v) {
    setBool(channelKey(agentId, channelType, 'show-thinking'), v);
}

// ── Future per-channel settings ────────────────────────────────────
// Add here as needed: scroll position, font size, collapsed sections, etc.
// All use channelKey(agentId, channelType, 'setting-name').


// ═══════════════════════════════════════════════════════════════════
// Cleanup
// ═══════════════════════════════════════════════════════════════════

export function clearAll() {
    const keys = Object.keys(localStorage).filter(k => k.startsWith(PREFIX));
    keys.forEach(k => localStorage.removeItem(k));
}
