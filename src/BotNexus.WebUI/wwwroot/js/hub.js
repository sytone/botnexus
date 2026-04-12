// BotNexus WebUI — SignalR connection management

import { debugLog, serverLog, getClientVersion } from './api.js';
import { setStatus, showConnectionBanner, hideConnectionBanner } from './ui.js';
import { storeManager } from './session-store.js';

let connection = null;
let connectionId = null;

export function getConnection() { return connection; }
export function getConnectionId() { return connectionId; }
export function setConnectionId(id) { connectionId = id; }

/** Safe invoke — guards against null / disconnected connection. */
export async function hubInvoke(method, ...args) {
    if (!connection || connection.state !== signalR.HubConnectionState.Connected) {
        debugLog('hub', `SKIP ${method} — not connected (state: ${connection?.state || 'null'})`);
        return null;
    }
    debugLog('hub', `→ ${method}`, ...args);
    try {
        const result = await connection.invoke(method, ...args);
        debugLog('hub', `← ${method} OK`, result);
        return result;
    } catch (err) {
        debugLog('hub', `← ${method} ERROR`, err.message);
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
    });

    connection.onreconnected(() => {
        setStatus('connected');
        hideConnectionBanner();
        debugLog('lifecycle', 'Reconnected');
        hubInvoke('SubscribeAll').then(result => {
            if (result?.sessions) {
                storeManager.subscribe(result.sessions);
                debugLog('lifecycle', `Reconnect SubscribeAll: ${result.sessions.length} sessions`);
            }
        }).catch(err => {
            debugLog('lifecycle', 'Reconnect SubscribeAll failed:', err.message);
        });
    });

    connection.onclose(() => {
        debugLog('lifecycle', 'Connection closed');
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
    } catch (err) {
        debugLog('lifecycle', 'Connection FAILED:', err.message);
        console.error('SignalR connection error:', err);
        setStatus('disconnected');
        showConnectionBanner('❌ Cannot connect to Gateway. Check that the server is running.', 'error', true);
        setTimeout(startConnection, 5000);
    }
}

export function manualReconnect() {
    hideConnectionBanner();
    if (connection) {
        connection.stop().then(() => startConnection()).catch(() => startConnection());
    } else {
        // Connection was never created — should not happen in normal flow
        console.warn('manualReconnect called with no connection object');
    }
}
