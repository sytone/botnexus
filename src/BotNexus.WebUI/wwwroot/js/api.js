// BotNexus WebUI — API client and channel type helpers

export const API_BASE = '/api';

// ── Channel Type Resolution ─────────────────────────────────────────

const CHANNEL_ALIASES = {
    'web chat': 'signalr', 'web-chat': 'signalr', 'webchat': 'signalr'
};

let _channelDisplayNames = {};

export function setChannelDisplayNames(names) { _channelDisplayNames = names; }

/** Normalize raw channel type to canonical key for grouping / filtering. */
export function normalizeChannelKey(raw) {
    const n = (raw || '').toLowerCase();
    if (!n || n === 'signalr' || n === 'web-chat') return 'web chat';
    return n;
}

/** Convert display / raw channel type → hub type for API calls. */
export function toHubChannelType(raw) {
    const normalized = normalizeChannelKey(raw);
    return normalized === 'web chat' ? 'signalr' : normalized;
}

/** Convert hub type → user-facing display name. */
export function channelDisplayName(name) {
    const n = (name || '').toLowerCase();
    if (_channelDisplayNames[n]) return _channelDisplayNames[n];
    if (n === 'signalr' || n === 'web-chat') return 'Web Chat';
    return name || 'Web Chat';
}

/** Emoji for a given channel type. */
export function channelEmoji(name) {
    const map = {
        websocket: '🌐', signalr: '🌐', 'web-chat': '💬', 'web chat': '💬',
        telegram: '✈️', discord: '🎮', slack: '💼', tui: '🖥️'
    };
    return map[(name || '').toLowerCase()] || '📡';
}

// ── REST Fetch ──────────────────────────────────────────────────────

/** Centralized JSON fetch with error handling. Returns null on failure. */
export async function fetchJson(path) {
    try {
        const res = await fetch(`${API_BASE}${path}`);
        if (!res.ok) return null;
        return await res.json();
    } catch (e) {
        console.error(`API error (${path}):`, e);
        return null;
    }
}

// ── Debug Logging ───────────────────────────────────────────────────

const DEBUG = true;

export function debugLog(category, ...args) {
    if (DEBUG) console.log(`[BotNexus:${category}]`, ...args);
}

// ── Version Management ──────────────────────────────────────────────

let CLIENT_VERSION = 'loading';

export function getClientVersion() { return CLIENT_VERSION; }

export function initVersionCheck() {
    fetch('/api/version')
        .then(r => r.json())
        .then(d => {
            CLIENT_VERSION = d.version || 'unknown';
            debugLog('init', `Client version: ${CLIENT_VERSION}`);
            const meta = document.querySelector('meta[name="botnexus-version"]');
            if (meta) meta.content = CLIENT_VERSION;
            _scheduleVersionPoll();
        })
        .catch(() => { CLIENT_VERSION = 'unknown'; });
}

function _scheduleVersionPoll() {
    setInterval(async () => {
        try {
            const res = await fetch('/api/version');
            if (!res.ok) return;
            const data = await res.json();
            const sv = data.version || 'unknown';
            if (CLIENT_VERSION !== 'loading' && CLIENT_VERSION !== 'unknown' && sv !== CLIENT_VERSION) {
                console.log(`Version changed: ${CLIENT_VERSION} → ${sv}. Reloading...`);
                location.reload();
            }
        } catch { /* server may be restarting */ }
    }, 10000);
}

/** Post client logs to server for unified debugging. */
export function serverLog(level, message, data) {
    debugLog(level, message, data);
    fetch('/api/log', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ level, message, data, version: CLIENT_VERSION, timestamp: new Date().toISOString() })
    }).catch(() => {});
}
