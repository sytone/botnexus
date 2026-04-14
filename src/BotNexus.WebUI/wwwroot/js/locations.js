import { API_BASE } from './api.js';
import { $, escapeHtml, showView } from './ui.js';

const typeEmoji = {
    filesystem: '📁',
    api: '🌐',
    'mcp-server': '🔌',
    database: '🗄️',
    'remote-node': '📡'
};

let locationsCache = [];
let toolbarBound = false;

function statusBadge(status) {
    const normalized = (status || 'unknown').toLowerCase();
    if (normalized === 'healthy') return '<span class="location-status healthy">✅ healthy</span>';
    if (normalized === 'unhealthy') return '<span class="location-status unhealthy">❌ unhealthy</span>';
    return '<span class="location-status unknown">⚪ unknown</span>';
}

function locationTypeEmoji(type) {
    return typeEmoji[(type || '').toLowerCase()] || '📍';
}

function normalizeLocationPayload(inputName, inputType, inputPath, inputDescription) {
    return {
        name: (inputName || '').trim(),
        type: (inputType || 'filesystem').trim().toLowerCase(),
        value: (inputPath || '').trim(),
        description: (inputDescription || '').trim() || null
    };
}

async function requestJson(url, options) {
    const res = await fetch(url, options);
    let body = null;
    try { body = await res.json(); } catch {}
    return { res, body };
}

function findLocation(name) {
    return locationsCache.find(x => x.name.toLowerCase() === name.toLowerCase()) || null;
}

function setCheckResult(text) {
    const result = $('#locations-check-result');
    if (!result) return;
    result.textContent = text || '';
}

function wireActionButtons() {
    const body = $('#locations-body');
    if (!body) return;

    body.querySelectorAll('[data-action="edit"]').forEach(btn => {
        btn.addEventListener('click', () => updateLocation(btn.dataset.name || ''));
    });
    body.querySelectorAll('[data-action="delete"]').forEach(btn => {
        btn.addEventListener('click', () => deleteLocation(btn.dataset.name || ''));
    });
    body.querySelectorAll('[data-action="check"]').forEach(btn => {
        btn.addEventListener('click', () => checkLocation(btn.dataset.name || ''));
    });
}

export async function loadLocations() {
    const body = $('#locations-body');
    if (!body) return;

    body.innerHTML = '<tr><td colspan="6">Loading locations...</td></tr>';
    try {
        const res = await fetch(`${API_BASE}/locations`);
        if (!res.ok) {
            body.innerHTML = `<tr><td colspan="6">Failed to load locations (${res.status}).</td></tr>`;
            return;
        }

        const locations = await res.json();
        locationsCache = Array.isArray(locations) ? locations : [];
        if (locationsCache.length === 0) {
            body.innerHTML = '<tr><td colspan="6">No locations defined.</td></tr>';
            return;
        }

        body.innerHTML = locationsCache.map(location => {
            const canEdit = !!location.isUserDefined;
            const actionButtons = [
                `<button class="btn-sm" data-action="check" data-name="${escapeHtml(location.name)}" title="Health check">🔍</button>`
            ];
            if (canEdit) {
                actionButtons.push(`<button class="btn-sm" data-action="edit" data-name="${escapeHtml(location.name)}" title="Edit">✏️</button>`);
                actionButtons.push(`<button class="btn-sm btn-danger-sm" data-action="delete" data-name="${escapeHtml(location.name)}" title="Delete">🗑️</button>`);
            }

            return `<tr>
                <td>${escapeHtml(location.name)}${canEdit ? '' : ' <span class="location-auto-badge">(auto)</span>'}</td>
                <td>${locationTypeEmoji(location.type)} ${escapeHtml(location.type || '')}</td>
                <td><code>${escapeHtml(location.pathOrEndpoint || '')}</code></td>
                <td>${escapeHtml(location.description || '')}</td>
                <td>${statusBadge(location.status)}</td>
                <td class="location-actions">${actionButtons.join(' ')}</td>
            </tr>`;
        }).join('');

        wireActionButtons();
    } catch (err) {
        body.innerHTML = `<tr><td colspan="6">Error loading locations: ${escapeHtml(err.message || String(err))}</td></tr>`;
    }
}

export async function addLocation() {
    const name = window.prompt('Location name (example: repo-botnexus):');
    if (!name) return;
    const type = window.prompt('Type (filesystem, api, mcp-server, database, remote-node):', 'filesystem');
    if (!type) return;
    const value = window.prompt('Path / Endpoint / Connection String:');
    if (!value) return;
    const description = window.prompt('Description (optional):', '') || '';

    const payload = normalizeLocationPayload(name, type, value, description);
    const { res, body } = await requestJson(`${API_BASE}/locations`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload)
    });

    if (!res.ok) {
        window.alert(body?.error || `Failed to add location (${res.status}).`);
        return;
    }

    await loadLocations();
}

export async function updateLocation(name) {
    const current = findLocation(name);
    if (!current) return;

    const newType = window.prompt('Type (filesystem, api, mcp-server, database, remote-node):', current.type || 'filesystem');
    if (!newType) return;
    const newValue = window.prompt('Path / Endpoint / Connection String:', current.pathOrEndpoint || '');
    if (!newValue) return;
    const newDescription = window.prompt('Description (optional):', current.description || '') || '';

    const payload = normalizeLocationPayload(current.name, newType, newValue, newDescription);
    const { res, body } = await requestJson(`${API_BASE}/locations/${encodeURIComponent(current.name)}`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload)
    });

    if (!res.ok) {
        window.alert(body?.error || `Failed to update location (${res.status}).`);
        return;
    }

    await loadLocations();
}

export async function deleteLocation(name) {
    const current = findLocation(name);
    if (!current) return;
    if (!window.confirm(`Delete location "${current.name}"?`)) return;

    const res = await fetch(`${API_BASE}/locations/${encodeURIComponent(current.name)}`, { method: 'DELETE' });
    if (!res.ok) {
        let message = `Failed to delete location (${res.status}).`;
        try {
            const body = await res.json();
            message = body?.error || message;
        } catch {}
        window.alert(message);
        return;
    }

    await loadLocations();
}

export async function checkLocation(name) {
    const current = findLocation(name);
    if (!current) return;

    const { res, body } = await requestJson(`${API_BASE}/locations/${encodeURIComponent(current.name)}/check`, {
        method: 'POST'
    });

    if (!res.ok) {
        const errorText = body?.error || `Health check failed (${res.status}).`;
        setCheckResult(`❌ ${current.name}: ${errorText}`);
        window.alert(errorText);
        return;
    }

    const status = (body?.status || 'unknown').toLowerCase();
    const icon = status === 'healthy' ? '✅' : status === 'unhealthy' ? '❌' : '⚪';
    setCheckResult(`${icon} ${current.name}: ${body?.message || status}`);
    window.alert(`${current.name}: ${body?.status || 'unknown'}${body?.message ? `\n${body.message}` : ''}`);
    await loadLocations();
}

export async function openLocationsView() {
    if (!toolbarBound) {
        $('#btn-locations-add')?.addEventListener('click', addLocation);
        $('#btn-locations-refresh')?.addEventListener('click', loadLocations);
        toolbarBound = true;
    }
    showView('locations-view');
    await loadLocations();
}
