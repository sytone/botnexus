// BotNexus WebUI — SignalR connection management

import { debugLog, serverLog, getClientVersion } from './api.js';
import { setStatus, showConnectionBanner, hideConnectionBanner } from './ui.js';
import { channelManager } from './session-store.js';

let connection = null;
let connectionId = null;
const _reconnectCallbacks = [];

export function getConnection() { return connection; }
export function getConnectionId() { return connectionId; }
export function setConnectionId(id) { connectionId = id; }

/** Register a callback to run on SignalR reconnect. */
export function onHubReconnected(fn) { _reconnectCallbacks.push(fn); }

/** Safe invoke — guards against null / disconnected connection. */
export async function hubInvoke(method, ...args) {
    if (!connection || connection.state !== signalR.HubConnectionState.Connected) {
        debugLog('hub', `SKIP ${method} — not connected (state: ${connection?.state || 'null'})`);
        serverLog('warn', `Hub invoke skipped: ${method}`, { method, state: connection?.state || 'null' });
        return null;
    }
    debugLog('hub', `→ ${method}`, ...args);
    serverLog('info', `Hub invoke start: ${method}`, { method, args });
    try {
        const result = await connection.invoke(method, ...args);
        debugLog('hub', `← ${method} OK`, result);
        serverLog('info', `Hub invoke success: ${method}`, { method, result });
        return result;
    } catch (err) {
        debugLog('hub', `← ${method} ERROR`, err.message);
        serverLog('error', `Hub invoke failure: ${method}`, { method, error: err?.message || String(err) });
        throw err;
    }
}

/**
 * Build, wire, and start the SignalR connection.
 * @param {function} registerEvents — called with (connection) to bind hub event handlers.
 */
export function initHub(registerEvents) {
    const version = getClientVersion();

    connection = new signalR.HubConnectionBuilder()
        .withUrl(`/hub/gateway?clientVersion=${version}`)
        .withAutomaticReconnect([0, 1000, 2000, 5000, 10000, 30000])
        .configureLogging(signalR.LogLevel.Warning)
        .build();

    debugLog('init', `SignalR hub builder created (client v${version})`);

    // Delegate event registration to events.js
    registerEvents(connection);

    // Connection lifecycle
    connection.onreconnecting(() => {
        setStatus('reconnecting');
        showConnectionBanner('⚠️ Connection lost. Reconnecting...', 'warning');
        serverLog('warn', 'SignalR reconnecting', { state: connection?.state || 'unknown' });
    });

    connection.onreconnected(() => {
        setStatus('connected');
        hideConnectionBanner();
        debugLog('lifecycle', 'Reconnected');
        serverLog('info', 'SignalR reconnected', { state: connection?.state || 'unknown' });
        hubInvoke('SubscribeAll').then(result => {
            if (result?.sessions) {
                channelManager.subscribe(result.sessions);
                debugLog('lifecycle', `Reconnect SubscribeAll: ${result.sessions.length} sessions`);
                serverLog('info', 'Reconnect SubscribeAll result', { count: result.sessions.length });
            }
        }).catch(err => {
            debugLog('lifecycle', 'Reconnect SubscribeAll failed:', err.message);
            serverLog('error', 'Reconnect SubscribeAll failed', { error: err?.message || String(err) });
        });
        _reconnectCallbacks.forEach(fn => { try { fn(); } catch (e) { console.error('Reconnect callback error:', e); } });
    });

    connection.onclose(() => {
        debugLog('lifecycle', 'Connection closed');
        serverLog('warn', 'SignalR connection closed', { state: connection?.state || 'unknown' });
        setStatus('disconnected');
        connectionId = null;
        showConnectionBanner('❌ Connection closed. Click Reconnect to retry.', 'error', true);
    });

    startConnection();
}

export async function startConnection() {
    try {
        serverLog('info', 'SignalR connecting', getClientVersion());
        setStatus('connecting');
        showConnectionBanner('Connecting...', 'warning');
        await connection.start();
        debugLog('lifecycle', 'Connected! State:', connection.state);
        serverLog('info', 'SignalR connected', { state: connection.state, connectionId });
    } catch (err) {
        debugLog('lifecycle', 'Connection FAILED:', err.message);
        console.error('SignalR connection error:', err);
        serverLog('error', 'SignalR connection failed', { error: err?.message || String(err) });
        setStatus('disconnected');
        showConnectionBanner('❌ Cannot connect to Gateway. Check that the server is running.', 'error', true);
        setTimeout(startConnection, 5000);
    }
}

export function manualReconnect() {
    hideConnectionBanner();
    serverLog('info', 'Manual reconnect requested');
    if (connection) {
        connection.stop().then(() => startConnection()).catch(() => startConnection());
    } else {
        // Connection was never created — should not happen in normal flow
        console.warn('manualReconnect called with no connection object');
        serverLog('warn', 'manualReconnect called without connection');
    }
}
