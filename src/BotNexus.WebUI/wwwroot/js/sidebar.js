// BotNexus WebUI — Sidebar rendering, agents, channels, extensions, config, activity, cron

import {
    API_BASE, fetchJson, normalizeChannelKey, channelDisplayName, channelEmoji,
    setChannelDisplayNames
} from './api.js';
import {
    dom, $, $$, escapeHtml, relativeTime, formatTime, showConfirm,
    showView, renderMarkdown
} from './ui.js';
import {
    channelManager, getCurrentSessionId, getCurrentAgentId, getStreamState,
    isCurrentSessionStreaming, getCurrentChannelType
} from './session-store.js';
import { hubInvoke, getConnection, getConnectionId } from './hub.js';
import { openAgentTimeline, startNewChat, appendSystemMessage } from './chat.js';
import { getCollapsedAgents, toggleAgentCollapsed, getCachedAgents, setCachedAgents, getCachedSessions, setCachedSessions } from './storage.js';
import { openLocationsView as openLocationsCanvasView } from './locations.js';

// ── Caches ──────────────────────────────────────────────────────────

let agentsCache = [];
let providersCache = [];
let modelsCache = [];
export function getAgentsCache() { return agentsCache; }
export function setAgentsCache(agents) { agentsCache = agents || []; }

// ── Sidebar Badge ───────────────────────────────────────────────────

export function updateSidebarBadge(sessionId, count) {
    const store = channelManager.getStore(sessionId);
    if (!store?.agentId) return;
    const channelKey = normalizeChannelKey(store.channelType || getCurrentChannelType() || 'web chat');
    const el = dom.sessionsList.querySelector(
        `.list-item[data-agent-id="${CSS.escape(store.agentId)}"][data-channel-type="${CSS.escape(channelKey)}"]`
    );
    if (!el) return;
    let badge = el.querySelector('.unread-badge');
    if (count > 0) {
        if (!badge) {
            badge = document.createElement('span');
            badge.className = 'unread-badge';
            el.querySelector('.list-item-row')?.appendChild(badge);
        }
        badge.textContent = count > 99 ? '99+' : count;
    } else if (badge) {
        badge.remove();
    }
}

// ── World Identity ──────────────────────────────────────────────────

export async function loadWorldIdentity() {
    const world = await fetchJson('/world');
    if (!world || !dom.worldIdentity) return;
    const emoji = world.emoji && world.emoji.trim() ? world.emoji.trim() : '🌍';
    const name = world.name && world.name.trim() ? world.name.trim() : 'BotNexus Gateway';
    dom.worldIdentity.textContent = `${emoji} ${name}`;
}

// ── Sessions List ───────────────────────────────────────────────────

let sessionsFingerprint = '';
let sessionsInitialLoad = true;

export async function loadSessions() {
    if (sessionsInitialLoad) {
        dom.sessionsList.innerHTML = '<div class="loading">Loading...</div>';
    }

    const [agents, sessions] = await Promise.all([
        fetchJson('/agents'),
        fetchJson('/sessions')
    ]);

    if (!agents || agents.length === 0) {
        dom.sessionsList.innerHTML = '<div class="empty-state">No agents configured</div>';
        sessionsFingerprint = '';
        sessionsInitialLoad = false;
        return;
    }

    const sessionsByAgent = {};
    if (sessions) {
        for (let s of sessions) {
            if (s.session && !s.agentId) s = { ...s.session, ...s };
            const agentId = s.agentId || s.agentName || 'unknown';
            if (!sessionsByAgent[agentId]) sessionsByAgent[agentId] = [];
            sessionsByAgent[agentId].push(s);
        }
    }

    const currentChannel = getCurrentChannelType();
    const newFingerprint = JSON.stringify({
        agents: agents.map(a => a.agentId || a.name),
        sessions: (sessions || []).map(s =>
            (s.agentId || s.agentName || 'unknown') + ':' + normalizeChannelKey(s.channelType) + ':' + (s.updatedAt || '')),
        active: getCurrentSessionId()
    });
    if (newFingerprint === sessionsFingerprint) return;
    sessionsFingerprint = newFingerprint;
    sessionsInitialLoad = false;

    const collapsedAgents = getCollapsedAgents();
    dom.sessionsList.querySelectorAll('.agent-group-header.collapsed').forEach(el => {
        collapsedAgents.add(el.textContent.replace('▼', '').trim());
    });

    dom.sessionsList.innerHTML = '';

    for (const agent of agents) {
        const agentId = agent.agentId || agent.name;
        const displayName = agent.displayName || agentId;

        const group = document.createElement('div');
        group.className = 'agent-group';

        const header = document.createElement('div');
        header.className = 'agent-group-header' + (collapsedAgents.has(displayName) ? ' collapsed' : '');
        header.innerHTML = `<span class="collapse-icon">▼</span> ${escapeHtml(displayName)}`;
        header.addEventListener('click', () => {
            header.classList.toggle('collapsed');
            toggleAgentCollapsed(displayName, header.classList.contains('collapsed'));
        });
        group.appendChild(header);

        const channelsDiv = document.createElement('div');
        channelsDiv.className = 'agent-group-channels';

        const agentSessions = (sessionsByAgent[agentId] || []).sort((a, b) =>
            new Date(b.updatedAt || b.createdAt || 0) - new Date(a.updatedAt || a.createdAt || 0)
        );
        const latestByChannel = new Map();
        for (const s of agentSessions) {
            const key = normalizeChannelKey(s.channelType);
            if (!latestByChannel.has(key)) latestByChannel.set(key, s);
        }

        if (latestByChannel.size === 0) {
            const emptyEl = document.createElement('div');
            emptyEl.className = 'list-item';
            emptyEl.dataset.agentId = agentId;
            emptyEl.dataset.channelType = 'web chat';
            emptyEl.innerHTML = `
                <div class="list-item-row"><span class="item-title">💬 Web Chat</span></div>
                <span class="item-meta">No sessions</span>`;
            emptyEl.addEventListener('click', () => {
                dom.agentSelect.value = agentId;
                channelManager.setSelectedAgent(agentId);
                startNewChat();
            });
            channelsDiv.appendChild(emptyEl);
        } else {
            for (const ls of latestByChannel.values()) {
                const ct = normalizeChannelKey(ls.channelType);
                const dn = channelDisplayName(ct);
                const isViewing = getCurrentAgentId() === agentId &&
                    currentChannel && normalizeChannelKey(currentChannel) === ct;

                const el = document.createElement('div');
                el.className = 'list-item' + (isViewing ? ' viewing' : '');
                el.dataset.agentId = agentId;
                el.dataset.channelType = ct;

                const timeStr = relativeTime(ls.updatedAt || ls.createdAt);
                const emoji = channelEmoji(ct);
                el.innerHTML = `
                    <div class="list-item-row">
                        <span class="item-title">${emoji} ${escapeHtml(dn)}</span>
                    </div>
                    <span class="item-meta">${timeStr}</span>`;
                el.addEventListener('click', () => openAgentTimeline(agentId, ct, ls.sessionId));
                channelsDiv.appendChild(el);
            }
        }

        group.appendChild(channelsDiv);
        dom.sessionsList.appendChild(group);
    }
    sessionsInitialLoad = false;
}

// ── Channels ────────────────────────────────────────────────────────

let channelsRefreshTimer = null;
const CHANNELS_REFRESH_MS = 30000;

function buildCapabilityIcons(ch) {
    const caps = [];
    if (ch.supportsStreaming) caps.push('<span class="channel-cap" title="Streaming">⚡</span>');
    if (ch.supportsSteering) caps.push('<span class="channel-cap" title="Steering">🎯</span>');
    if (ch.supportsFollowUp) caps.push('<span class="channel-cap" title="Follow-up">🔄</span>');
    if (ch.supportsThinkingDisplay) caps.push('<span class="channel-cap" title="Thinking">💭</span>');
    if (ch.supportsToolDisplay) caps.push('<span class="channel-cap" title="Tools">🔧</span>');
    return caps.join('');
}

export async function loadChannels() {
    const channels = await fetchJson('/channels');
    if (!channels || channels.length === 0) {
        dom.channelsList.innerHTML = '<div class="empty-state">No channels</div>';
        return;
    }

    const names = {};
    for (const ch of channels) {
        if (ch.name && ch.displayName) names[ch.name.toLowerCase()] = ch.displayName;
    }
    setChannelDisplayNames(names);

    const incomingNames = new Set(channels.map(ch => ch.name));
    for (const existing of [...dom.channelsList.querySelectorAll('.list-item[data-channel]')]) {
        if (!incomingNames.has(existing.dataset.channel)) existing.remove();
    }
    for (const ph of [...dom.channelsList.querySelectorAll('.loading, .empty-state')]) ph.remove();

    for (const ch of channels) {
        const dotClass = ch.isRunning ? 'running' : 'stopped';
        const statusText = ch.isRunning ? 'running' : 'stopped';
        const capsHtml = buildCapabilityIcons(ch);
        const titleHtml = `<span class="channel-status-dot ${dotClass}" aria-hidden="true"></span> ${channelEmoji(ch.name)} ${escapeHtml(ch.displayName || ch.name)}`;

        const existing = dom.channelsList.querySelector(`.list-item[data-channel="${CSS.escape(ch.name)}"]`);
        if (existing) {
            const titleEl = existing.querySelector('.item-title');
            if (titleEl && titleEl.innerHTML !== titleHtml) titleEl.innerHTML = titleHtml;
            const metaEl = existing.querySelector('.item-meta');
            if (metaEl && metaEl.textContent !== statusText) metaEl.textContent = statusText;
            const capsEl = existing.querySelector('.channel-caps');
            if (capsEl && capsEl.innerHTML !== capsHtml) capsEl.innerHTML = capsHtml;
        } else {
            const el = document.createElement('div');
            el.className = 'list-item';
            el.setAttribute('role', 'listitem');
            el.dataset.channel = ch.name;
            el.innerHTML = `
                <div class="list-item-row">
                    <span class="item-title">${titleHtml}</span>
                    <span class="item-meta" style="font-size:0.68rem;">${statusText}</span>
                </div>
                <div class="channel-caps">${capsHtml}</div>`;
            dom.channelsList.appendChild(el);
        }
    }
}

export function scheduleChannelsRefresh() {
    if (channelsRefreshTimer) clearInterval(channelsRefreshTimer);
    channelsRefreshTimer = setInterval(loadChannels, CHANNELS_REFRESH_MS);
}

// ── Extensions ──────────────────────────────────────────────────────

export async function loadExtensions() {
    dom.extensionsList.innerHTML = '<div class="loading">Loading...</div>';
    const extensions = await fetchJson('/extensions');
    if (!extensions || extensions.length === 0) {
        dom.extensionsList.innerHTML = '<div class="empty-state">No extensions loaded</div>';
        return;
    }
    const groups = {};
    for (const ext of extensions) {
        const key = ext.name || 'Unknown';
        if (!groups[key]) groups[key] = { version: ext.version, types: [] };
        groups[key].types.push(ext.type || 'unknown');
    }
    dom.extensionsList.innerHTML = '';
    for (const [name, info] of Object.entries(groups)) {
        const el = document.createElement('div');
        el.className = 'list-item';
        el.setAttribute('role', 'listitem');
        const typeBadges = info.types.map(t => `<span class="extension-type-badge">${escapeHtml(t)}</span>`).join(' ');
        el.innerHTML = `
            <div class="list-item-row">
                <span class="item-title">${escapeHtml(name)}</span>
                <span class="item-meta" style="font-size:0.68rem;">v${escapeHtml(info.version || '?')}</span>
            </div>
            <div style="margin-top:2px;">${typeBadges}</div>`;
        dom.extensionsList.appendChild(el);
    }
}

// ── Agents List ─────────────────────────────────────────────────────

export async function loadAgents() {
    dom.agentsList.innerHTML = '<div class="loading">Loading...</div>';
    const agents = await fetchJson('/agents');
    if (!agents || agents.length === 0) {
        dom.agentsList.innerHTML = '<div class="empty-state">No agents configured</div>';
        return;
    }
    agentsCache = agents;
    dom.agentsList.innerHTML = '';
    for (const a of agents) {
        const el = document.createElement('div');
        el.className = 'list-item';
        el.setAttribute('role', 'listitem');
        const name = a.name || a.agentId || a.id || 'unknown';
        const model = a.model || a.defaultModel || '';
        const statusClass = a.status === 'error' ? 'error' : (a.status === 'busy' ? 'warning' : 'success');
        const statusLabel = a.status || 'ready';
        el.innerHTML = `
            <div class="list-item-row">
                <span class="item-title">
                    <span class="agent-status-dot ${statusClass}" aria-hidden="true"></span>
                    ${escapeHtml(name)}
                </span>
                <span class="item-meta" style="font-size:0.68rem;">${escapeHtml(statusLabel)}</span>
            </div>
            <span class="item-meta">${model ? 'Model: ' + escapeHtml(model) : ''}</span>`;
        el.addEventListener('click', () => { channelManager.setSelectedAgent(name); openAgentConfig(name); });
        dom.agentsList.appendChild(el);
    }
    populateAgentSelect(agents);
    populateActivityAgentFilter();
}

export function populateAgentSelect(agents) {
    dom.agentSelect.innerHTML = '';
    for (const a of agents) {
        const opt = document.createElement('option');
        const name = a.name || a.agentId || a.id || 'unknown';
        opt.value = name;
        opt.textContent = name;
        dom.agentSelect.appendChild(opt);
    }
    if (!getCurrentAgentId() && agents.length > 0) {
        channelManager.setSelectedAgent(agents[0].name || agents[0].agentId || agents[0].id);
    }
    if (getCurrentAgentId() && !dom.agentSelect.value) {
        dom.agentSelect.value = getCurrentAgentId();
    }
}

// ── Agent Config View ───────────────────────────────────────────────

export async function openAgentConfig(agentId) {
    const agent = await fetchJson(`/agents/${encodeURIComponent(agentId)}`);
    if (!agent) { appendSystemMessage('Agent not found', 'error'); return; }

    showView('agent-config-view');
    $('#agent-config-title').textContent = agent.displayName || agent.name || agentId;

    const body = $('#agent-config-body');
    body.innerHTML = buildAgentConfigForm(agent, agentId);

    $('#btn-agent-save').onclick = () => saveAgentConfig(agentId);
    ['cfg-enabled', 'cfg-memoryEnabled', 'cfg-temporalDecayEnabled'].forEach(id => {
        const cb = $(`#${id}`);
        if (cb) cb.addEventListener('change', () => {
            const lbl = cb.parentElement.querySelector('label');
            if (lbl) lbl.textContent = cb.checked ? 'Active' : 'Disabled';
        });
    });
    $('#btn-agent-chat').onclick = () => {
        showView('chat-view');
        channelManager.setSelectedAgent(agentId);
        dom.agentSelect.value = agentId;
        startNewChat();
    };

    const provider = agent.apiProvider || agent.provider || '';
    if (provider) {
        const models = await fetchJson(`/models?provider=${encodeURIComponent(provider)}`);
        if (models) {
            const select = $('#cfg-model');
            if (select) {
                select.innerHTML = models.map(m =>
                    `<option value="${escapeHtml(m.modelId || m.id)}" ${(m.modelId || m.id) === agent.modelId ? 'selected' : ''}>${escapeHtml(m.name || m.modelId || m.id)}</option>`
                ).join('');
            }
        }
    }
    await loadAgentStatus(agentId);
}

function buildAgentConfigForm(agent, agentId) {
    const memoryEnabled = agent.memory?.enabled || agent.memoryEnabled || false;
    const memoryIndexing = agent.memory?.indexing || 'auto';
    const memoryTopK = agent.memory?.search?.defaultTopK ?? 10;
    const temporalEnabled = agent.memory?.search?.temporalDecay?.enabled ?? true;
    const temporalHalfLife = agent.memory?.search?.temporalDecay?.halfLifeDays ?? 30;
    const allowedModelIds = (agent.allowedModelIds || agent.allowedModels || []).join(', ');
    const subAgentIds = (agent.subAgentIds || agent.subAgents || []).join(', ');
    const agentEnabled = agent.enabled !== undefined ? agent.enabled : true;

    let metadataJson = '{}';
    try { metadataJson = JSON.stringify(agent.metadata || {}, null, 2); } catch { /* ignore */ }
    let isolationJson = '{}';
    try { isolationJson = JSON.stringify(agent.isolationOptions || {}, null, 2); } catch { /* ignore */ }

    return `
        <div class="config-section"><h3>Identity</h3><div class="config-grid">
            <div class="config-field"><label>Agent ID</label><input type="text" value="${escapeHtml(agentId || agent.agentId || '')}" disabled class="config-input"></div>
            <div class="config-field"><label>Display Name</label><input type="text" id="cfg-displayName" value="${escapeHtml(agent.displayName || agent.name || '')}" class="config-input"></div>
            <div class="config-field full-width"><label>Description</label><textarea id="cfg-description" class="config-input" rows="3">${escapeHtml(agent.description || '')}</textarea></div>
            <div class="config-field"><label>Enabled</label><div class="config-toggle"><input type="checkbox" id="cfg-enabled" ${agentEnabled ? 'checked' : ''}><label for="cfg-enabled">${agentEnabled ? 'Active' : 'Disabled'}</label></div></div>
        </div></div>
        <div class="config-section"><h3>Model</h3><div class="config-grid">
            <div class="config-field"><label>Provider</label><input type="text" value="${escapeHtml(agent.apiProvider || agent.provider || '')}" disabled class="config-input"></div>
            <div class="config-field"><label>Model</label><select id="cfg-model" class="config-input"><option value="${escapeHtml(agent.modelId || agent.model || agent.defaultModel || '')}" selected>${escapeHtml(agent.modelId || agent.model || agent.defaultModel || 'unknown')}</option></select></div>
            <div class="config-field"><label>Isolation Strategy</label><input type="text" value="${escapeHtml(agent.isolationStrategy || 'in-process')}" disabled class="config-input"></div>
            <div class="config-field"><label>Max Concurrent Sessions</label><input type="number" id="cfg-maxSessions" value="${agent.maxConcurrentSessions || 0}" class="config-input"></div>
            <div class="config-field full-width"><label>Allowed Model IDs <span class="config-muted">(comma-separated)</span></label><input type="text" id="cfg-allowedModelIds" value="${escapeHtml(allowedModelIds)}" class="config-input" placeholder="e.g. gpt-4.1, claude-sonnet-4-20250514"></div>
            <div class="config-field full-width"><label>Sub-Agent IDs <span class="config-muted">(comma-separated)</span></label><input type="text" id="cfg-subAgentIds" value="${escapeHtml(subAgentIds)}" class="config-input" placeholder="e.g. coding-agent, research-agent"></div>
        </div></div>
        <div class="config-section"><h3>System Prompt</h3>
            <div class="config-field full-width"><label>System Prompt Files (loaded in order)</label><div id="cfg-promptFiles" class="config-prompt-files">${(agent.systemPromptFiles || []).map(f => `<div class="prompt-file-item">${escapeHtml(f)}</div>`).join('') || '<div class="config-muted">Using default order (AGENTS.md → SOUL.md → TOOLS.md → BOOTSTRAP.md → IDENTITY.md → USER.md)</div>'}</div></div>
            <div class="config-field full-width" style="margin-top:12px;"><label>Inline System Prompt</label><textarea id="cfg-systemPrompt" class="config-input config-textarea" rows="6" placeholder="Optional inline system prompt...">${escapeHtml(agent.systemPrompt || '')}</textarea></div>
        </div>
        <div class="config-section"><h3>Memory</h3><div class="config-grid">
            <div class="config-field"><label>Memory Enabled</label><div class="config-toggle"><input type="checkbox" id="cfg-memoryEnabled" ${memoryEnabled ? 'checked' : ''}><label for="cfg-memoryEnabled">${memoryEnabled ? 'Active' : 'Disabled'}</label></div></div>
            <div class="config-field"><label>Indexing Mode</label><select id="cfg-memoryIndexing" class="config-input"><option value="auto" ${memoryIndexing === 'auto' ? 'selected' : ''}>Auto</option><option value="manual" ${memoryIndexing === 'manual' ? 'selected' : ''}>Manual</option><option value="off" ${memoryIndexing === 'off' ? 'selected' : ''}>Off</option></select></div>
            <div class="config-field"><label>Search Default Top-K</label><input type="number" id="cfg-memoryTopK" value="${memoryTopK}" min="1" max="100" class="config-input"></div>
            <div class="config-field"><label>Temporal Decay Enabled</label><div class="config-toggle"><input type="checkbox" id="cfg-temporalDecayEnabled" ${temporalEnabled ? 'checked' : ''}><label for="cfg-temporalDecayEnabled">${temporalEnabled ? 'Active' : 'Disabled'}</label></div></div>
            <div class="config-field"><label>Temporal Decay Half-Life (days)</label><input type="number" id="cfg-temporalHalfLife" value="${temporalHalfLife}" min="1" class="config-input"></div>
        </div></div>
        <div class="config-section"><h3>Tools</h3><div class="config-field full-width"><label>Tool IDs</label><div class="config-value">${(agent.toolIds || []).join(', ') || '<span class="config-muted">All tools available</span>'}</div></div></div>
        <div class="config-section"><h3>Metadata</h3><div class="config-field full-width"><label>Agent Metadata <span class="config-muted">(read-only)</span></label><pre class="config-json">${escapeHtml(metadataJson)}</pre></div></div>
        <div class="config-section"><h3>Isolation Options</h3><div class="config-field full-width"><label>Strategy-specific options <span class="config-muted">(read-only)</span></label><pre class="config-json">${escapeHtml(isolationJson)}</pre></div></div>
        <div class="config-section" id="agent-status-section"><h3>Runtime Status</h3><div id="agent-runtime-status">Loading...</div></div>`;
}

async function loadAgentStatus(agentId) {
    const statusEl = $('#agent-runtime-status');
    if (!statusEl) return;
    try {
        const instances = await fetchJson('/agents/instances') || [];
        const agentInstances = instances.filter(i => i.agentId === agentId);
        if (agentInstances.length === 0) {
            statusEl.innerHTML = '<div class="config-value config-muted">No active instances</div>';
            return;
        }
        statusEl.innerHTML = `<div class="config-value" style="margin-bottom:8px">${agentInstances.length} active instance(s)</div>` +
            agentInstances.map(inst => {
                const emoji = inst.status === 'Running' ? '🟢' : inst.status === 'Idle' ? '🟡' : '🔴';
                return `<div class="config-value">${emoji} ${escapeHtml(inst.status || 'unknown')} — <code style="font-size:0.78rem;background:rgba(0,0,0,0.25);padding:1px 5px;border-radius:3px">${escapeHtml((inst.sessionId || '?').substring(0, 12))}…</code></div>`;
            }).join('');
    } catch {
        statusEl.innerHTML = '<div class="config-value config-muted">Unable to load status</div>';
    }
}

export async function saveAgentConfig(agentId) {
    const agentData = await fetchJson(`/agents/${encodeURIComponent(agentId)}`);
    if (!agentData) { appendSystemMessage('Agent not found', 'error'); return; }

    const updated = { ...agentData };
    const v = (sel) => $(sel)?.value;
    const c = (sel) => $(sel)?.checked;
    if (v('#cfg-displayName') !== undefined) updated.displayName = v('#cfg-displayName');
    if (v('#cfg-description') !== undefined) updated.description = v('#cfg-description');
    if (v('#cfg-systemPrompt') !== undefined) updated.systemPrompt = v('#cfg-systemPrompt');
    if (v('#cfg-model')) updated.modelId = v('#cfg-model');
    const ms = parseInt(v('#cfg-maxSessions'), 10);
    if (!isNaN(ms)) updated.maxConcurrentSessions = ms;
    updated.enabled = c('#cfg-enabled') ?? true;
    const ami = v('#cfg-allowedModelIds') || '';
    updated.allowedModelIds = ami ? ami.split(',').map(s => s.trim()).filter(Boolean) : [];
    const sai = v('#cfg-subAgentIds') || '';
    updated.subAgentIds = sai ? sai.split(',').map(s => s.trim()).filter(Boolean) : [];
    updated.memory = {
        enabled: c('#cfg-memoryEnabled') ?? false,
        indexing: v('#cfg-memoryIndexing') || 'auto',
        search: {
            defaultTopK: parseInt(v('#cfg-memoryTopK'), 10) || 10,
            temporalDecay: {
                enabled: c('#cfg-temporalDecayEnabled') ?? true,
                halfLifeDays: parseInt(v('#cfg-temporalHalfLife'), 10) || 30
            }
        }
    };

    const res = await fetch(`${API_BASE}/agents/${encodeURIComponent(agentId)}`, {
        method: 'PUT', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(updated)
    });
    if (res.ok) { appendSystemMessage('Agent settings saved.'); loadAgents(); }
    else { appendSystemMessage(`Failed to save: ${res.status}`, 'error'); }
}

// ── Agent Debug Panel ───────────────────────────────────────────────

export async function showAgentDebugInfo(agentId) {
    const agent = await fetchJson(`/agents/${encodeURIComponent(agentId)}`);
    if (!agent) {
        const cached = agentsCache.find(a => (a.name || a.agentId || a.id) === agentId);
        if (!cached) { appendSystemMessage('Agent not found', 'error'); return; }
        return renderAgentDebugPanel(cached, agentId);
    }
    return renderAgentDebugPanel(agent, agentId);
}

async function renderAgentDebugPanel(agent, agentId) {
    const agentName = agent.displayName || agent.name || agentId;
    let agentInstances = [];
    try { const inst = await fetchJson('/agents/instances') || []; agentInstances = inst.filter(i => i.agentId === agentId); } catch { /* ignore */ }

    let html = `<div class="debug-panel"><h3>🔍 ${escapeHtml(agentName)}</h3>`;
    html += `<div class="debug-section"><h4>Configuration</h4><table class="debug-table">`;
    html += `<tr><td>Agent ID</td><td><code>${escapeHtml(agentId)}</code></td></tr>`;
    if (agent.displayName) html += `<tr><td>Display Name</td><td>${escapeHtml(agent.displayName)}</td></tr>`;
    if (agent.apiProvider || agent.provider) html += `<tr><td>Provider</td><td>${escapeHtml(agent.apiProvider || agent.provider || '')}</td></tr>`;
    if (agent.modelId || agent.model || agent.defaultModel) html += `<tr><td>Model</td><td>${escapeHtml(agent.modelId || agent.model || agent.defaultModel || '')}</td></tr>`;
    if (agent.isolationStrategy) html += `<tr><td>Isolation</td><td>${escapeHtml(agent.isolationStrategy)}</td></tr>`;
    html += `<tr><td>Memory</td><td>${agent.memoryEnabled ? '✅ Enabled' : '❌ Disabled'}</td></tr>`;
    if (agent.systemPromptFiles?.length) html += `<tr><td>Prompt Files</td><td>${agent.systemPromptFiles.map(f => `<code>${escapeHtml(f)}</code>`).join(', ')}</td></tr>`;
    if (agent.status) html += `<tr><td>Status</td><td>${escapeHtml(agent.status)}</td></tr>`;
    html += `</table></div>`;

    html += `<div class="debug-section"><h4>Active Instances (${agentInstances.length})</h4>`;
    if (agentInstances.length === 0) { html += `<p class="debug-muted">No active instances</p>`; }
    else {
        for (const inst of agentInstances) {
            const statusEmoji = inst.status === 'Running' ? '🟢' : inst.status === 'Idle' ? '🟡' : '🔴';
            const sid = escapeHtml(inst.sessionId || '');
            html += `<div class="debug-instance"><span>${statusEmoji} ${escapeHtml(inst.status || 'unknown')}</span><code>${sid}</code>`;
            if (inst.isolationStrategy) html += `<span style="font-size:0.75rem;color:var(--text-secondary)">${escapeHtml(inst.isolationStrategy)}</span>`;
            html += `<button class="btn-sm btn-danger-sm" data-stop-agent="${escapeHtml(agentId)}" data-stop-session="${sid}">Stop</button></div>`;
        }
    }
    html += `</div>`;

    const conn = getConnection();
    if (getCurrentSessionId() && getCurrentAgentId() === agentId) {
        html += `<div class="debug-section"><h4>Current Session</h4><table class="debug-table">`;
        html += `<tr><td>Session ID</td><td><code>${escapeHtml(getCurrentSessionId())}</code></td></tr>`;
        html += `<tr><td>Connection ID</td><td><code>${escapeHtml(getConnectionId() || 'none')}</code></td></tr>`;
        html += `<tr><td>SignalR</td><td>${conn?.state === signalR.HubConnectionState.Connected ? '🟢 Connected' : '🔴 Disconnected'}</td></tr>`;
        html += `<tr><td>Streaming</td><td>${isCurrentSessionStreaming() ? '⏳ Yes' : 'No'}</td></tr>`;
        html += `</table></div>`;
        try {
            const sessionStatus = await fetchJson(`/agents/${encodeURIComponent(agentId)}/sessions/${encodeURIComponent(getCurrentSessionId())}/status`);
            if (sessionStatus) {
                html += `<div class="debug-section"><h4>Session Status</h4><table class="debug-table">`;
                if (sessionStatus.status) html += `<tr><td>Status</td><td>${escapeHtml(sessionStatus.status)}</td></tr>`;
                if (sessionStatus.messageCount != null) html += `<tr><td>Messages</td><td>${sessionStatus.messageCount}</td></tr>`;
                if (sessionStatus.channelType) html += `<tr><td>Channel</td><td>${escapeHtml(sessionStatus.channelType)}</td></tr>`;
                if (sessionStatus.createdAt) html += `<tr><td>Created</td><td>${escapeHtml(new Date(sessionStatus.createdAt).toLocaleString())}</td></tr>`;
                if (sessionStatus.updatedAt) html += `<tr><td>Updated</td><td>${escapeHtml(new Date(sessionStatus.updatedAt).toLocaleString())}</td></tr>`;
                html += `</table></div>`;
            }
        } catch { /* ignore */ }
    }

    html += `<div class="debug-section"><h4>Quick Actions</h4><div class="debug-actions">`;
    if (getCurrentSessionId() && getCurrentAgentId() === agentId) {
        html += `<button class="btn-sm btn-danger-sm" id="debug-btn-stop">⏹ Stop Agent</button>`;
        html += `<button class="btn-sm" id="debug-btn-reset">🔄 Reset Session</button>`;
        html += `<button class="btn-sm" id="debug-btn-copy-sid">📋 Copy Session ID</button>`;
    }
    html += `<button class="btn-sm" id="debug-btn-refresh" data-debug-agent="${escapeHtml(agentId)}">↻ Refresh</button></div></div></div>`;

    showDebugModal(html, agentId);
}

function showDebugModal(html, agentId) {
    const body = $('#debug-modal-body');
    body.innerHTML = html;
    dom.debugModal.classList.remove('hidden');

    const btnStop = body.querySelector('#debug-btn-stop');
    if (btnStop) btnStop.addEventListener('click', async () => {
        if (getCurrentSessionId() && getCurrentAgentId()) {
            await fetch(`${API_BASE}/agents/${encodeURIComponent(getCurrentAgentId())}/sessions/${encodeURIComponent(getCurrentSessionId())}/stop`, { method: 'POST' });
            showAgentDebugInfo(agentId);
        }
    });
    const btnReset = body.querySelector('#debug-btn-reset');
    if (btnReset) btnReset.addEventListener('click', () => { closeDebugModal(); /* reset handled by chat */ });
    const btnCopy = body.querySelector('#debug-btn-copy-sid');
    if (btnCopy) btnCopy.addEventListener('click', () => {
        if (!getCurrentSessionId()) return;
        navigator.clipboard.writeText(getCurrentSessionId()).then(() => {
            btnCopy.textContent = '✅ Copied!';
            setTimeout(() => { btnCopy.textContent = '📋 Copy Session ID'; }, 1200);
        }).catch(() => {
            const ta = document.createElement('textarea'); ta.value = getCurrentSessionId();
            ta.style.cssText = 'position:fixed;opacity:0'; document.body.appendChild(ta);
            ta.select(); document.execCommand('copy'); document.body.removeChild(ta);
            btnCopy.textContent = '✅ Copied!';
            setTimeout(() => { btnCopy.textContent = '📋 Copy Session ID'; }, 1200);
        });
    });
    const btnRefresh = body.querySelector('#debug-btn-refresh');
    if (btnRefresh) btnRefresh.addEventListener('click', () => showAgentDebugInfo(btnRefresh.dataset.debugAgent));
    body.querySelectorAll('[data-stop-agent]').forEach(btn => {
        btn.addEventListener('click', async () => {
            btn.disabled = true; btn.textContent = '...';
            await fetch(`${API_BASE}/agents/${encodeURIComponent(btn.dataset.stopAgent)}/sessions/${encodeURIComponent(btn.dataset.stopSession)}/stop`, { method: 'POST' });
            showAgentDebugInfo(agentId);
        });
    });
}

export function closeDebugModal() { dom.debugModal.classList.add('hidden'); }

// ── Agent Form Modal ────────────────────────────────────────────────

export async function openAddAgentForm() {
    $('#agent-form-title').textContent = 'Add Agent';
    dom.agentForm.reset();
    $('#form-agent-name').disabled = false;
    $('#form-agent-temperature').disabled = true;
    $('#form-agent-max-tokens').disabled = true;
    $('#form-feedback').className = 'form-feedback hidden';

    const providerSelect = $('#form-agent-provider');
    providerSelect.innerHTML = '<option value="">Select provider...</option>';
    const providers = await fetchJson('/providers');
    if (providers?.length) {
        providersCache = providers;
        providers.sort((a, b) => (a.name || '').localeCompare(b.name || ''));
        for (const p of providers) {
            const opt = document.createElement('option');
            opt.value = p.providerId || p.id || p.name || 'unknown';
            opt.textContent = p.name || opt.value;
            providerSelect.appendChild(opt);
        }
    }
    dom.agentFormModal.classList.remove('hidden');
}

export function closeAgentForm() {
    dom.agentFormModal.classList.add('hidden');
    dom.agentForm.reset();
    $('#form-feedback').className = 'form-feedback hidden';
}

export async function loadModelsForProvider(providerName) {
    const modelSelect = $('#form-agent-model');
    modelSelect.innerHTML = '<option value="">Loading models...</option>';
    const models = await fetchJson('/models');
    modelSelect.innerHTML = '<option value="">Select model...</option>';
    if (models?.length) {
        modelsCache = models;
        const filtered = providerName
            ? models.filter(m => (m.provider || '').toLowerCase() === providerName.toLowerCase())
            : models;
        filtered.sort((a, b) => (a.name || a.modelId || a.id || '').localeCompare(b.name || b.modelId || b.id || ''));
        for (const m of filtered) {
            const opt = document.createElement('option');
            opt.value = m.modelId || m.id || 'unknown';
            opt.textContent = m.name || opt.value;
            modelSelect.appendChild(opt);
        }
    }
}

export async function saveAgent() {
    const name = $('#form-agent-name').value.trim();
    const provider = $('#form-agent-provider').value;
    const model = $('#form-agent-model').value;
    const systemPrompt = $('#form-agent-system-prompt').value.trim();
    const feedback = $('#form-feedback');

    if (!name || !provider || !model) {
        feedback.textContent = 'Name, provider, and model are required.';
        feedback.className = 'form-feedback form-error';
        return;
    }

    const body = { agentId: name, displayName: name, modelId: model, apiProvider: provider };
    if (systemPrompt) body.systemPrompt = systemPrompt;

    try {
        const res = await fetch(`${API_BASE}/agents`, {
            method: 'POST', headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(body)
        });
        if (res.ok || res.status === 201) {
            feedback.textContent = `Agent "${name}" created!`;
            feedback.className = 'form-feedback form-success';
            setTimeout(() => { closeAgentForm(); loadAgents(); }, 1000);
        } else {
            feedback.textContent = `Error: ${res.status} — ${await res.text()}`;
            feedback.className = 'form-feedback form-error';
        }
    } catch (e) {
        feedback.textContent = `Error: ${e.message}`;
        feedback.className = 'form-feedback form-error';
    }
}

// ── Activity Monitor ────────────────────────────────────────────────

const MAX_ACTIVITY_ITEMS = 100;
let isActivitySubscribed = false;
let activityWs = null;
let activityReconnectTimer = null;

export function handleActivityEvent(evt) {
    if (!isActivitySubscribed) return;
    const el = document.createElement('div');
    let cssClass = 'activity-item';
    const eventType = evt.eventType || evt.event || 'unknown';
    let filterType = '';
    let badgeClass = 'system';
    let icon = '📌';
    if (eventType.includes('Error') || eventType === 'error') { cssClass += ' error'; filterType = 'error'; badgeClass = 'error'; icon = '❌'; }
    else if (eventType.includes('Response') || eventType.includes('Sent')) { cssClass += ' response-sent'; filterType = 'response'; badgeClass = 'response'; icon = '✅'; }
    else if (eventType.includes('Tool')) { cssClass += ' msg-received'; filterType = 'tool'; badgeClass = 'tool'; icon = '🔧'; }
    else if (eventType.includes('Message') || eventType.includes('Received')) { cssClass += ' msg-received'; filterType = 'message'; badgeClass = 'message'; icon = '💬'; }
    el.className = cssClass;
    el.dataset.agent = evt.agentId || evt.agent || '';
    el.dataset.eventCategory = filterType;

    const time = formatTime(evt.timestamp || new Date().toISOString());
    const channel = evt.channel || evt.source || '';
    const preview = (evt.content || evt.message || '').substring(0, 80);
    el.innerHTML = `<span class="activity-time">${time}</span>${channel ? `<span class="activity-channel">[${escapeHtml(channel)}]</span>` : ''}<span class="activity-type-badge ${badgeClass}">${icon} ${escapeHtml(eventType)}</span>${preview ? escapeHtml(preview) : ''}${(evt.content || evt.message || '').length > 80 ? '...' : ''}`;
    dom.activityItems.insertBefore(el, dom.activityItems.firstChild);
    while (dom.activityItems.children.length > MAX_ACTIVITY_ITEMS) dom.activityItems.removeChild(dom.activityItems.lastChild);
    applyActivityFilters();
}

export function trackActivity(category, agentId, content) {
    if (!isActivitySubscribed) return;
    handleActivityEvent({
        eventType: category === 'message' ? 'MessageSent' : category === 'response' ? 'ResponseReceived' : category === 'tool' ? 'ToolCall' : 'Error',
        agentId: agentId || '', content: content || '',
        timestamp: new Date().toISOString(), channel: 'WebUI'
    });
}

export function applyActivityFilters() {
    const agentFilter = dom.activityFilterAgent.value;
    const typeFilter = dom.activityFilterType.value;
    dom.activityItems.querySelectorAll('.activity-item').forEach(el => {
        const matchAgent = !agentFilter || el.dataset.agent === agentFilter;
        const matchType = !typeFilter || el.dataset.eventCategory === typeFilter;
        el.classList.toggle('filtered-out', !(matchAgent && matchType));
    });
}

function populateActivityAgentFilter() {
    const current = dom.activityFilterAgent.value;
    dom.activityFilterAgent.innerHTML = '<option value="">All Agents</option>';
    for (const a of agentsCache) {
        const name = a.name || a.agentId || a.id || 'unknown';
        const opt = document.createElement('option');
        opt.value = name; opt.textContent = name;
        dom.activityFilterAgent.appendChild(opt);
    }
    if (current) dom.activityFilterAgent.value = current;
}

export function connectActivityWs() {
    if (activityWs && (activityWs.readyState === WebSocket.OPEN || activityWs.readyState === WebSocket.CONNECTING)) return;
    const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
    activityWs = new WebSocket(`${proto}//${location.host}/ws/activity`);
    activityWs.onopen = () => console.log('Activity WebSocket connected');
    activityWs.onmessage = (event) => {
        try { const msg = JSON.parse(event.data); if (msg.type === 'activity' || msg.eventType) handleActivityEvent(msg); }
        catch (e) { console.error('Activity WS parse error:', e); }
    };
    activityWs.onclose = () => { if (isActivitySubscribed) activityReconnectTimer = setTimeout(connectActivityWs, 5000); };
    activityWs.onerror = () => {};
}

export function disconnectActivityWs() {
    if (activityReconnectTimer) { clearTimeout(activityReconnectTimer); activityReconnectTimer = null; }
    if (activityWs) { activityWs.onclose = null; activityWs.close(); activityWs = null; }
}

export function toggleActivity() {
    isActivitySubscribed = dom.toggleActivity.checked;
    if (isActivitySubscribed) { connectActivityWs(); dom.activityFeed.classList.remove('collapsed'); }
    else { disconnectActivityWs(); dom.activityFeed.classList.add('collapsed'); }
}

// ── Cron View ───────────────────────────────────────────────────────

export function openCronView() { showView('cron-view'); loadCronJobs(); }
export function openLocationsView() { return openLocationsCanvasView(); }

export async function loadCronJobs() {
    const body = $('#cron-body');
    if (!body) return;
    const jobs = await fetchJson('/cron');
    if (!jobs || !Array.isArray(jobs) || jobs.length === 0) {
        body.innerHTML = '<div class="cron-empty"><p>No cron jobs configured. Add one to schedule agent tasks.</p></div>';
        return;
    }
    let html = `<table class="cron-table"><thead><tr><th>Name</th><th>Schedule</th><th>Agent</th><th>Last Run</th><th>Next Run</th><th>Status</th><th>Actions</th></tr></thead><tbody>`;
    for (const job of jobs) {
        html += `<tr><td>${escapeHtml(job.name || '')}</td><td><code>${escapeHtml(job.schedule || '')}</code></td><td>${escapeHtml(job.agentId || '')}</td><td>${job.lastRunAt ? escapeHtml(relativeTime(job.lastRunAt)) : '—'}</td><td>${job.nextRunAt ? escapeHtml(relativeTime(job.nextRunAt)) : '—'}</td><td>${job.enabled ? 'active' : 'paused'}</td><td><button class="btn btn-sm" onclick="runCronJob('${job.id}')">▶ Run</button> <button class="btn btn-sm btn-danger-sm" onclick="deleteCronJob('${job.id}')">🗑</button></td></tr>`;
    }
    html += '</tbody></table>';
    body.innerHTML = html;
}

// Expose cron actions on window for inline onclick handlers
window.runCronJob = async function(jobId) {
    try {
        const res = await fetch(`${API_BASE}/cron/${encodeURIComponent(jobId)}/run`, { method: 'POST' });
        if (res.ok) { appendSystemMessage('Cron job triggered.'); loadCronJobs(); loadSessions(); }
        else { appendSystemMessage(`Failed to run: ${res.status}`, 'error'); }
    } catch (e) { appendSystemMessage(`Error: ${e.message}`, 'error'); }
};

window.deleteCronJob = async function(jobId) {
    showConfirm('Delete this cron job?', 'Delete Cron Job', async () => {
        try {
            const res = await fetch(`${API_BASE}/cron/${encodeURIComponent(jobId)}`, { method: 'DELETE' });
            if (res.ok || res.status === 204) loadCronJobs();
            else appendSystemMessage(`Failed to delete: ${res.status}`, 'error');
        } catch (e) { appendSystemMessage(`Error: ${e.message}`, 'error'); }
    }, 'Delete');
};

export function showAddCronForm() {
    const body = $('#cron-body');
    if (!body || body.querySelector('.cron-form')) return;
    const formHtml = `<div class="cron-form"><h3>New Cron Job</h3><div class="config-grid">
        <div class="config-field"><label>Job Name</label><input type="text" id="cron-name" class="config-input" placeholder="e.g. daily-summary"></div>
        <div class="config-field"><label>Cron Expression</label><input type="text" id="cron-schedule" class="config-input" placeholder="e.g. 0 9 * * *"></div>
        <div class="config-field"><label>Target Agent</label><select id="cron-agent" class="config-input">${agentsCache.map(a => { const n = a.name || a.agentId || a.id || 'unknown'; return `<option value="${escapeHtml(n)}">${escapeHtml(n)}</option>`; }).join('')}</select></div>
        <div class="config-field"><label>Enabled</label><div class="config-toggle"><input type="checkbox" id="cron-enabled" checked><label for="cron-enabled">Active</label></div></div>
        <div class="config-field full-width"><label>Message / Task</label><textarea id="cron-message" class="config-input" rows="3" placeholder="Message to send to the agent on each run..."></textarea></div>
    </div><div class="form-actions"><button class="btn btn-secondary" id="btn-cancel-cron">Cancel</button><button class="btn btn-primary" id="btn-submit-cron">Create Job</button></div></div>`;
    body.insertAdjacentHTML('afterbegin', formHtml);
    $('#btn-cancel-cron').addEventListener('click', () => body.querySelector('.cron-form')?.remove());
    $('#btn-submit-cron').addEventListener('click', () => appendSystemMessage('Cron API not yet available — job was not created.', 'error'));
    const cb = $('#cron-enabled');
    if (cb) cb.addEventListener('change', () => { const l = cb.parentElement.querySelector('label'); if (l) l.textContent = cb.checked ? 'Active' : 'Disabled'; });
}

// ── Init ─────────────────────────────────────────────────────────────

export function initSidebar() {
    loadWorldIdentity();
    loadSessions();
    loadChannels();
    loadExtensions();
    loadAgents();
    scheduleChannelsRefresh();
}
