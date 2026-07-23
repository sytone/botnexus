using BotNexus.Gateway.Abstractions.Sessions;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Diagnostics;
using BotNexus.Gateway.Security;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Resolution;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Agents;

/// <summary>
/// Default in-memory orchestrator for background sub-agent sessions.
/// </summary>
public sealed class DefaultSubAgentManager : ISubAgentManager
{
    private readonly IAgentSupervisor _supervisor;
    private readonly IAgentRegistry _registry;
    private readonly IActivityBroadcaster _activity;
    private readonly IChannelDispatcher _dispatcher;
    private readonly IAgentWorkspaceManager? _workspaceManager;
    private readonly IOptionsMonitor<GatewayOptions> _options;
    private readonly ILogger<DefaultSubAgentManager> _logger;
    private readonly DefaultToolPolicyProvider? _policyProvider;
    private readonly TimeProvider _timeProvider;
    private readonly ISessionStore? _sessionStore;
    // Trusted-only security-event sink (#1647). Optional: null behaves exactly as before (no
    // emission). Spawn and kill each emit one SecurityEvent so the sandbox boundary is recorded
    // to the trusted sink and never the public activity/diagnostic stream.
    private readonly ISecurityEventSink? _securityEvents;
    // Single source of truth per sub-agent. Folds the previously-separate parent/child agent
    // id, timeout CTS, and the completion/cleanup once-only gates into one record so an add or
    // teardown is a single atomic dictionary operation. The old design spread these across five
    // parallel dictionaries keyed by the same subAgentId that had to be kept mutually consistent
    // by hand — the synthetic child-AgentId fallback in OnCompletedAsync existed precisely
    // because _childAgentIds could drift out of sync with _subAgents (#1385).
    private readonly ConcurrentDictionary<string, SubAgentRecord> _records = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<SessionId, ConcurrentBag<string>> _parentChildren = [];

    /// <summary>
    /// Platform-wide count of sub-agents currently in the <see cref="SubAgentStatus.Running"/>
    /// state across all parent sessions. Mirrors the per-session running tally used by
    /// <c>EnforceSpawnLimits</c> but without the parent filter, so the portal stats overview can
    /// show a single live "active sub-agents" figure. Reading the concurrent dictionary's values is
    /// a lock-free snapshot, which is sufficient for a monitoring counter that the stats panel polls.
    /// </summary>
    public int ActiveSubAgentCount =>
        _records.Values.Count(record => record.Info.Status == SubAgentStatus.Running);

    public DefaultSubAgentManager(
        IAgentSupervisor supervisor,
        IAgentRegistry registry,
        IActivityBroadcaster activity,
        IChannelDispatcher dispatcher,
        IOptionsMonitor<GatewayOptions> options,
        ILogger<DefaultSubAgentManager> logger,
        IAgentWorkspaceManager? workspaceManager = null,
        DefaultToolPolicyProvider? policyProvider = null,
        ISessionStore? sessionStore = null,
        TimeProvider? timeProvider = null,
        ISecurityEventSink? securityEvents = null)
    {
        _supervisor = supervisor;
        _registry = registry;
        _activity = activity;
        _dispatcher = dispatcher;
        _workspaceManager = workspaceManager;
        _options = options;
        _logger = logger;
        _policyProvider = policyProvider;
        _sessionStore = sessionStore;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _securityEvents = securityEvents;
    }

    /// <summary>
    /// Eagerly pins the freshly-created child session to the parent's conversation BEFORE
    /// <see cref="SpawnAsync"/> returns, so concurrent lookups
    /// (<see cref="ISessionStore.ListByConversationAsync"/>, /api/conversations/{id}/history,
    /// canvas resolvers) never observe the child as an orphan. Prior to this the pin ran inside the
    /// fire-and-forget <c>Task.Run</c> at the end of <c>SpawnAsync</c>, opening an orphan window
    /// between the method returning and the background task being scheduled (Phase 4 / F-6).
    /// </summary>
    /// <remarks>
    /// No-op when no <see cref="ISessionStore"/> is wired. If the child row does not exist yet,
    /// this method creates and persists it before handle creation can start background execution.
    /// Extracted from <see cref="SpawnAsync"/> so the eager-pin step is a
    /// named, awaited unit on the orchestration path (#1630); the eager (not lazy) ordering is
    /// guarded by the architecture/behaviour tests. Declared BEFORE <c>SpawnAsync</c> so the
    /// <c>.ConversationId =</c> mutation lexically precedes the fire-and-forget <c>Task.Run</c> in
    /// the orchestrator -- the F-6 architecture fence
    /// (SubAgentEagerPinArchitectureTests.NoConversationIdMutation_InsideTaskRun) is a source-position
    /// check, so the eager-pin helper must sit above the queue point even though it is awaited first.
    /// </remarks>
    /// <param name="request">The spawn request carrying the inherited conversation id.</param>
    /// <param name="childSessionId">The minted child session to bind to the parent conversation.</param>
    /// <param name="childAgentId">The ephemeral child agent that owns the new session.</param>
    /// <param name="ct">Cancellation token for the store reads/writes.</param>
    private async Task PinChildConversationAsync(
        SubAgentSpawnRequest request,
        SessionId childSessionId,
        AgentId childAgentId,
        CancellationToken ct)
    {
        if (_sessionStore is null)
            return;

        var childSession = await _sessionStore.GetAsync(childSessionId, ct).ConfigureAwait(false)
            ?? await _sessionStore.GetOrCreateAsync(childSessionId, childAgentId, ct).ConfigureAwait(false);
        childSession.ConversationId = request.InheritedConversationId;
        childSession.SessionType = SessionType.AgentSubAgent;
        await _sessionStore.SaveAsync(childSession, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<SubAgentInfo> SpawnAsync(SubAgentSpawnRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Defence in depth: `required` is a compile-time guarantee only —
        // callers using `null!` (or producing a null Mode via reflection / JSON
        // deserialization quirks) would otherwise reach the default arm and
        // hit a NullReferenceException dereferencing request.Mode.GetType().
        ArgumentNullException.ThrowIfNull(request.Mode);

        var parentDescriptor = _registry.Get(request.ParentAgentId)
            ?? throw new KeyNotFoundException($"Parent agent '{request.ParentAgentId}' is not registered.");

        EnforceSpawnLimits(request);

        var uniqueId = Guid.NewGuid().ToString("N");
        var subAgentId = uniqueId;
        var childSessionId = SessionId.ForSubAgent(request.ParentSessionId, uniqueId);

        // Resolve the Embody | Mirror discriminated union into a side-effect-free plan
        // (descriptor + minted child id + customisation overrides). See ResolveSpawnPlan.
        var plan = ResolveSpawnPlan(request, parentDescriptor, uniqueId);
        var archetype = plan.Archetype;
        var baseDescriptor = plan.BaseDescriptor;
        var childAgentId = plan.ChildAgentId;
        var name = plan.Name;
        var modelOverride = plan.ModelOverride;
        var toolIds = plan.ToolIds;

        // Validate tool grants against parent's deny-list (privilege escalation
        // prevention). Runs AFTER plan resolution so it reads the typed `toolIds`
        // resolved from Mode, not a deleted top-level request field. See ValidateToolGrants.
        ValidateToolGrants(request, toolIds);

        // Build file access policy for workspace isolation. Null means "fully isolated" -
        // the child falls back to the base descriptor's FileAccess below. See
        // BuildChildFileAccessPolicy.
        var childFileAccess = BuildChildFileAccessPolicy(request);

        if (!_registry.Contains(childAgentId))
        {
            _registry.Register(baseDescriptor with
            {
                AgentId = childAgentId,
                DisplayName = $"{baseDescriptor.DisplayName} ({archetype.Value})",
                Kind = AgentKind.SubAgent,
                // #2136: apply the archetype/customisation tool restriction and any system-prompt
                // override onto the parent clone. A null toolIds means "inherit the parent's tools".
                ToolIds = toolIds is { Count: > 0 } ? toolIds : baseDescriptor.ToolIds,
                SystemPrompt = string.IsNullOrWhiteSpace(plan.SystemPromptOverride)
                    ? baseDescriptor.SystemPrompt
                    : plan.SystemPromptOverride,
                FileAccess = childFileAccess ?? baseDescriptor.FileAccess
            });
        }

        // Materialize and persist the child session before handle creation. Handle creation can
        // reach the model immediately, so this is the last safe point to guarantee that every
        // later tool write-ahead has a durable parent row (#2113).
        await PinChildConversationAsync(request, childSessionId, childAgentId, ct).ConfigureAwait(false);

        var handle = await _supervisor.GetOrCreateAsync(childAgentId, childSessionId, ct);

        var configuredDefaultModel = string.IsNullOrWhiteSpace(_options.CurrentValue.SubAgents.DefaultModel)
            ? null
            : _options.CurrentValue.SubAgents.DefaultModel;

        var info = new SubAgentInfo
        {
            SubAgentId = subAgentId,
            ParentSessionId = request.ParentSessionId,
            ChildSessionId = childSessionId,
            Name = name,
            ParentAgentId = request.ParentAgentId.Value,
            ChildAgentId = childAgentId.Value,
            Task = request.Task,
            Model = ModelOverrideResolver.Resolve(
                modelDefaults: new ModelOverrideLayer(Model: baseDescriptor.ModelId),
                agent: new ModelOverrideLayer(Model: configuredDefaultModel),
                conversation: new ModelOverrideLayer(Model: modelOverride)).Model,
            Archetype = archetype,
            Status = SubAgentStatus.Running,
            StartedAt = DateTimeOffset.UtcNow,
            TurnsUsed = 0
        };

        var record = new SubAgentRecord(info, request.ParentAgentId, childAgentId);
        if (!_records.TryAdd(subAgentId, record))
            throw new InvalidOperationException($"Sub-agent '{subAgentId}' already exists.");

        // Persist the sub-agent session row to sessions.db (best-effort; non-SQLite stores no-op).
        if (_sessionStore is not null)
        {
            try { await _sessionStore.SaveSubAgentSessionAsync(info, ct).ConfigureAwait(false); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist sub-agent session row for '{SubAgentId}'.", subAgentId);
            }
        }

        _parentChildren.GetOrAdd(request.ParentSessionId, _ => []).Add(subAgentId);

        // Register inherited deny-list so the child agent can't invoke parent-denied tools
        if (_policyProvider is not null)
        {
            var effectiveDenyList = _policyProvider.GetEffectiveDenyList(request.ParentAgentId.Value);
            if (effectiveDenyList.Count > 0)
                _policyProvider.SetDynamicDenyList(childAgentId, effectiveDenyList);
        }

        var subAgentOptions = _options.CurrentValue.SubAgents;

        // Clamp the agent-supplied timeout to the configured ceiling. Depth and concurrency are
        // already bounded above; without this the timeout (the only budget wired to a real
        // cancellation token) could be set arbitrarily high, letting a background sub-agent run
        // effectively forever. Mirrors the runaway-cost guard the agent_converse tool applies.
        var timeoutSeconds = subAgentOptions.ResolveTimeoutSeconds(request.TimeoutSeconds);

        // Clamp the requested turn budget too. It is not yet wired to a live per-turn counter, but
        // bounding it here keeps the request shape honest and prevents a latent runaway when it is.
        var maxTurns = subAgentOptions.ResolveMaxTurns(request.MaxTurns);
        if (request.TimeoutSeconds > timeoutSeconds || request.MaxTurns > maxTurns)
        {
            _logger.LogWarning(
                "Sub-agent '{SubAgentId}' spawn budget clamped: timeoutSeconds {RequestedTimeout}->{ClampedTimeout}, maxTurns {RequestedMaxTurns}->{ClampedMaxTurns}.",
                subAgentId,
                request.TimeoutSeconds,
                timeoutSeconds,
                request.MaxTurns,
                maxTurns);
        }

        var timeoutCts = new CancellationTokenSource();
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        record.TimeoutCts = timeoutCts;

        _ = Task.Run(() => RunSubAgentAsync(subAgentId, handle, request.Task, timeoutSeconds), CancellationToken.None);

        _logger.LogInformation(
            "Spawned sub-agent '{SubAgentId}' for parent session '{ParentSessionId}' in child session '{ChildSessionId}'.",
            subAgentId,
            request.ParentSessionId,
            childSessionId);

        await PublishLifecycleActivityAsync(
            GatewayActivityType.SubAgentSpawned,
            "subagent_spawned",
            info,
            request.ParentAgentId.Value,
            $"Sub-agent '{subAgentId}' spawned.");

        // Trusted security-event emit (#1647): the sandbox boundary just spawned a sub-agent.
        // Best-effort and side-effect-free - it never alters the spawn outcome.
        EmitSubAgentEvent("subagent.spawned", request.ParentSessionId, subAgentId);

        // Opportunistically age out finished records so the registry stays bounded without a timer.
        ReapCompletedRecords();

        return info;
    }

    /// <summary>
    /// Enforces the configured spawn depth and per-session concurrency ceilings for
    /// <paramref name="request"/>. Pure guard — throws <see cref="InvalidOperationException"/>
    /// when a limit is breached and is otherwise a no-op. Extracted from
    /// <see cref="SpawnAsync"/> so the "depth limit rejects at N" / "concurrency rejects at N"
    /// boundaries are independently unit-testable (#1565).
    /// </summary>
    /// <param name="request">The spawn request whose parent session is being bounded.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the parent session is already at the maximum spawn depth or already has the
    /// maximum number of running sub-agents.
    /// </exception>
    internal void EnforceSpawnLimits(SubAgentSpawnRequest request)
    {
        var maxDepth = _options.CurrentValue.SubAgents.MaxDepth;
        if (maxDepth > 0)
        {
            var depth = CountSubAgentDepth(request.ParentSessionId);
            if (depth >= maxDepth)
                throw new InvalidOperationException(
                    $"Cannot spawn sub-agent: parent session depth {depth} has reached the maximum depth of {maxDepth}.");
        }

        var maxConcurrent = _options.CurrentValue.SubAgents.MaxConcurrentPerSession;
        if (maxConcurrent > 0)
        {
            var activeCount = _records.Values.Count(record =>
                record.Info.ParentSessionId == request.ParentSessionId &&
                record.Info.Status == SubAgentStatus.Running);

            if (activeCount >= maxConcurrent)
                throw new InvalidOperationException(
                    $"Parent session '{request.ParentSessionId}' already has {activeCount} running sub-agents; maximum is {maxConcurrent}.");
        }
    }

    /// <summary>
    /// Resolves the required <see cref="SubAgentSpawnRequest.Mode"/> discriminated union
    /// (Embody | Mirror) into a side-effect-free <see cref="SubAgentSpawnPlan"/> — the descriptor
    /// to clone, the minted child agent id, and any Embody customisation overrides.
    /// </summary>
    /// <remarks>
    /// Phase 5 / F-6 (#562): Mode is <c>required</c> on the request, so the null case is
    /// structurally unreachable here (<see cref="SpawnAsync"/> already null-guards Mode) — the
    /// default arm exists only to catch a future third <c>SubAgentSpawnMode</c> subclass being
    /// added without updating this switch. Extracted as a pure method so each mode's resolution
    /// (e.g. Mirror's model fallback deriving from the <i>target</i> descriptor, not the parent)
    /// is unit-testable without the full manager (#1565).
    /// </remarks>
    /// <param name="request">The spawn request carrying the Mode.</param>
    /// <param name="parentDescriptor">The already-resolved parent descriptor (Embody base).</param>
    /// <param name="uniqueId">The minted unique id used to form the child agent id.</param>
    /// <returns>The resolved plan.</returns>
    /// <exception cref="KeyNotFoundException">Thrown when a Mirror target agent is not registered.</exception>
    /// <exception cref="InvalidOperationException">Thrown for an unknown Mode subclass.</exception>
    internal SubAgentSpawnPlan ResolveSpawnPlan(
        SubAgentSpawnRequest request,
        AgentDescriptor parentDescriptor,
        string uniqueId)
    {
        switch (request.Mode)
        {
            case Embody embody:
                // #2136: worker archetypes are resolved from the built-in catalog rather than a
                // registered named agent. When the caller supplies no explicit tool allowlist, apply
                // the archetype's tool restriction so the role's tool boundary is preserved; the
                // model/provider/system prompt are still inherited from the parent descriptor.
                var archetypeProfile = BuiltInArchetypes.GetProfile(embody.Role);
                var resolvedToolIds = embody.Customizations.ToolIds is { Count: > 0 }
                    ? embody.Customizations.ToolIds
                    : archetypeProfile?.ToolIds;
                return new SubAgentSpawnPlan(
                    Archetype: embody.Role,
                    BaseDescriptor: parentDescriptor,
                    ChildAgentId: AgentId.From($"{request.ParentAgentId}--subagent--{embody.Role.Value}--{uniqueId}"),
                    Name: embody.Customizations.Name,
                    ModelOverride: embody.Customizations.ModelOverride,
                    ApiProviderOverride: embody.Customizations.ApiProviderOverride,
                    ToolIds: resolvedToolIds,
                    SystemPromptOverride: embody.Customizations.SystemPromptOverride);

            case Mirror mirror:
                // Mirror is strict pass-through of the target's descriptor. The archetype slot in
                // the child agent id is filled with the target id (locked design #562) so the child
                // surfaces the mirrored identity rather than a generic role label.
                var targetDescriptor = _registry.Get(mirror.TargetAgentId)
                    ?? throw new KeyNotFoundException($"Target agent '{mirror.TargetAgentId}' is not registered.");
                return new SubAgentSpawnPlan(
                    Archetype: SubAgentArchetype.General,
                    BaseDescriptor: targetDescriptor,
                    ChildAgentId: AgentId.From($"{request.ParentAgentId}--subagent--{mirror.TargetAgentId.Value}--{uniqueId}"),
                    Name: null,
                    ModelOverride: null,
                    ApiProviderOverride: null,
                    ToolIds: null,
                    SystemPromptOverride: null);

            default:
                throw new InvalidOperationException(
                    $"Unknown SubAgentSpawnMode subclass '{request.Mode.GetType().FullName}'. "
                    + "Embody and Mirror are the only legal modes — see SubAgentSpawnMode.");
        }
    }

    /// <summary>
    /// Rejects a spawn whose child would be granted a tool the parent is denied - the
    /// privilege-escalation guard on the security-critical spawn path. A sub-agent must never
    /// hold authority the parent itself lacks, so any requested tool that intersects the parent's
    /// effective deny-list aborts the spawn before any session, descriptor, or record is created.
    /// </summary>
    /// <remarks>
    /// No-op when there is no policy provider, the child requests no tools, or the parent's
    /// effective deny-list is empty - none of those can escalate. The deny match is
    /// <see cref="StringComparer.OrdinalIgnoreCase"/> so casing cannot defeat the check. Must run
    /// AFTER <see cref="ResolveSpawnPlan"/> because <paramref name="toolIds"/> is the typed list
    /// resolved from <see cref="SubAgentSpawnRequest.Mode"/>. Extracted from <see cref="SpawnAsync"/>
    /// so the escalation-rejection boundary is independently unit-testable (#1630).
    /// </remarks>
    /// <param name="request">The spawn request whose parent supplies the deny-list.</param>
    /// <param name="toolIds">The resolved tools the child would be granted, or <c>null</c>/empty.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when one or more requested tools appear in the parent's effective deny-list.
    /// </exception>
    internal void ValidateToolGrants(SubAgentSpawnRequest request, IReadOnlyList<string>? toolIds)
    {
        if (_policyProvider is null || toolIds is not { Count: > 0 })
            return;

        var parentDenyList = _policyProvider.GetEffectiveDenyList(request.ParentAgentId.Value);
        if (parentDenyList.Count == 0)
            return;

        var denied = toolIds
            .Where(t => parentDenyList.Contains(t, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (denied.Count > 0)
            throw new InvalidOperationException(
                $"Sub-agent cannot be granted tools denied to the parent: {string.Join(", ", denied)}");
    }

    /// <summary>
    /// Composes the child's <see cref="FileAccessPolicy"/> for workspace isolation, or returns
    /// <c>null</c> when the child should stay fully isolated (the caller then falls back to the
    /// base descriptor's <see cref="AgentDescriptor.FileAccess"/>). By default a sub-agent can only
    /// reach its own temporary workspace; <see cref="SubAgentSpawnRequest.ShareWorkspace"/> adds
    /// read+write access to the parent's workspace, and <see cref="SubAgentSpawnRequest.GrantedPaths"/>
    /// adds read-only access to specific directories.
    /// </summary>
    /// <remarks>
    /// Returns <c>null</c> (not an empty policy) when neither <c>ShareWorkspace</c> nor any granted
    /// path is requested, preserving the descriptor-inheritance fallback at the call site. The
    /// parent-workspace grant is skipped when no <see cref="IAgentWorkspaceManager"/> is wired.
    /// Blank/whitespace granted paths are filtered so they cannot silently widen access, and each
    /// kept path is resolved via <see cref="Path.GetFullPath(string)"/>. Extracted from
    /// <see cref="SpawnAsync"/> so the policy composition (read/write split, blank filtering, the
    /// isolated -> null contract) is independently unit-testable (#1630).
    /// </remarks>
    /// <param name="request">The spawn request carrying the share-workspace flag and granted paths.</param>
    /// <returns>The composed policy, or <c>null</c> when the child stays fully isolated.</returns>
    internal FileAccessPolicy? BuildChildFileAccessPolicy(SubAgentSpawnRequest request)
    {
        if (!request.ShareWorkspace && request.GrantedPaths is not { Count: > 0 })
            return null;

        var allowedRead = new List<string>();
        var allowedWrite = new List<string>();

        if (request.ShareWorkspace && _workspaceManager is not null)
        {
            var parentWorkspace = _workspaceManager.GetWorkspacePath(request.ParentAgentId.Value);
            allowedRead.Add(parentWorkspace);
            allowedWrite.Add(parentWorkspace);
        }

        if (request.GrantedPaths is { Count: > 0 })
        {
            foreach (var grantedPath in request.GrantedPaths)
            {
                if (!string.IsNullOrWhiteSpace(grantedPath))
                    allowedRead.Add(Path.GetFullPath(grantedPath));
            }
        }

        return new FileAccessPolicy
        {
            AllowedReadPaths = allowedRead,
            AllowedWritePaths = allowedWrite
        };
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SubAgentInfo>> ListAsync(SessionId parentSessionId, CancellationToken ct = default)
    {
        // Reap on read too, so list_subagents never surfaces (or retains) an unbounded backlog.
        ReapCompletedRecords();

        if (!_parentChildren.TryGetValue(parentSessionId, out var subAgentIds))
            return Task.FromResult<IReadOnlyList<SubAgentInfo>>([]);

        var results = subAgentIds
            .Select(id => _records.TryGetValue(id, out var record) ? record.Info : null)
            .Where(info => info is not null)
            .Select(info => info!)
            .OrderBy(info => info.StartedAt)
            .ToArray();

        return Task.FromResult<IReadOnlyList<SubAgentInfo>>(results);
    }

    /// <inheritdoc />
    public Task<SubAgentInfo?> GetAsync(string subAgentId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subAgentId);
        ReapCompletedRecords();
        return Task.FromResult(_records.TryGetValue(subAgentId, out var record) ? record.Info : null);
    }

    /// <summary>
    /// Test-only hook: whether the record for <paramref name="subAgentId"/> has finished its child
    /// cleanup and stamped its retention clock (<see cref="SubAgentRecord.RetiredAt"/> is set).
    /// <para>
    /// The retention eviction is driven by <c>RetiredAt</c>, which is stamped in the completion
    /// <c>finally</c> (via <see cref="CleanupChildAgentAsync"/>) AFTER the record's status has
    /// already flipped to <see cref="SubAgentStatus.Completed"/> and after two awaited dispatch
    /// steps. A test that advances a virtual <see cref="TimeProvider"/> the moment it observes
    /// <c>Completed</c> can therefore race the retirement stamp: the record is retired at the
    /// (already-advanced) virtual instant, so its window never elapses relative to the assertion
    /// and the eviction silently no-ops. Tests poll this to await the real retirement before
    /// advancing time, making the eviction assertions deterministic (#1769).
    /// </para>
    /// This is a diagnostic accessor only — it is not part of <see cref="ISubAgentManager"/> and has
    /// no effect on runtime behaviour.
    /// </summary>
    internal bool IsRetiredForTest(string subAgentId)
        => _records.TryGetValue(subAgentId, out var record) && record.RetiredAt is not null;

    /// <inheritdoc />
    public async Task<bool> KillAsync(string subAgentId, SessionId requestingSessionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subAgentId);

        if (!_records.TryGetValue(subAgentId, out var record))
            return false;

        var info = record.Info;

        if (info.ParentSessionId != requestingSessionId)
            return false;

        if (info.Status is SubAgentStatus.Completed or SubAgentStatus.Failed or SubAgentStatus.Killed or SubAgentStatus.TimedOut)
            return false;

        record.CancelTimeout();

        await CleanupChildAgentAsync(subAgentId, info.ChildSessionId, ct);

        if (!TryUpdateSubAgent(
            subAgentId,
            current => current with
            {
                Status = SubAgentStatus.Killed,
                CompletedAt = DateTimeOffset.UtcNow,
                ResultSummary = "Sub-agent was killed by parent session."
            },
            out var updatedInfo))
        {
            return false;
        }

        _logger.LogInformation(
            "Killed sub-agent '{SubAgentId}' for parent session '{ParentSessionId}'.",
            subAgentId,
            requestingSessionId);

        // Update the sub-agent session row with Killed status (best-effort).
        if (_sessionStore is not null && updatedInfo.CompletedAt.HasValue)
        {
            try
            {
                await _sessionStore.UpdateSubAgentSessionAsync(
                    subAgentId,
                    updatedInfo.CompletedAt.Value,
                    SubAgentStatus.Killed.ToString(),
                    ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update sub-agent session row for '{SubAgentId}'.", subAgentId);
            }
        }

        await PublishLifecycleActivityAsync(
            GatewayActivityType.SubAgentKilled,
            "subagent_killed",
            updatedInfo,
            record.ParentAgentId.Value,
            $"Sub-agent '{subAgentId}' was killed.");

        // Trusted security-event emit (#1647): only the successful-kill path reaches here - the
        // wrong-requester and already-terminal guards above early-return false before this point.
        EmitSubAgentEvent("subagent.killed", info.ParentSessionId, subAgentId);

        return true;
    }

    /// <inheritdoc />
    public async Task OnCompletedAsync(string subAgentId, string resultSummary, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subAgentId);

        if (!_records.TryGetValue(subAgentId, out var record))
            return;

        if (!record.TryBeginCompletion())
        {
            _logger.LogDebug("Skipping duplicate completion for sub-agent '{SubAgentId}'.", subAgentId);
            return;
        }

        const string emptyResponseDiagnostic = "Sub-agent failed because it returned an empty final response.";
        var hasFinalResponse = !string.IsNullOrWhiteSpace(resultSummary);

        if (!TryUpdateSubAgent(
                subAgentId,
                current => current.Status == SubAgentStatus.Running
                    ? current with
                    {
                        Status = hasFinalResponse ? SubAgentStatus.Completed : SubAgentStatus.Failed,
                        CompletedAt = DateTimeOffset.UtcNow,
                        ResultSummary = hasFinalResponse ? resultSummary : emptyResponseDiagnostic
                    }
                    : current,
                out var updated) || updated.Status == SubAgentStatus.Killed)
        {
            return;
        }

        // A timeout/failure may set the terminal record before entering the shared completion path.
        // Always publish and dispatch the record's winning terminal reason, never a late prompt result.
        var normalizedSummary = updated.ResultSummary ?? emptyResponseDiagnostic;

        // Update the sub-agent session row with the final status (best-effort).
        if (_sessionStore is not null && updated.CompletedAt.HasValue)
        {
            try
            {
                await _sessionStore.UpdateSubAgentSessionAsync(
                    subAgentId,
                    updated.CompletedAt.Value,
                    updated.Status.ToString(),
                    ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update sub-agent session row for '{SubAgentId}'.", subAgentId);
            }
        }

        var parentAgentId = record.ParentAgentId;
        // Producer-side species: this wake-up is FROM the child sub-agent TO the parent.
        // Sender must carry the child's AgentId, not the parent's, so participant
        // tracking and downstream conversation attribution see the correct originator.
        // (Fix #526 / sub-agent misclassification.) The child AgentId now lives on the
        // record alongside the rest of the sub-agent state, so it can never drift out of
        // sync with the live entry the way the old separate _childAgentIds map could —
        // the synthetic-fallback workaround that drift required is no longer needed (#1385).
        var childAgentId = record.ChildAgentId;
        if (updated.Status == SubAgentStatus.Completed)
        {
            await PublishLifecycleActivityAsync(
                GatewayActivityType.SubAgentCompleted,
                "subagent_completed",
                updated,
                parentAgentId.Value,
                $"Sub-agent '{subAgentId}' completed.");
        }
        else if (updated.Status is SubAgentStatus.Failed or SubAgentStatus.TimedOut)
        {
            await PublishLifecycleActivityAsync(
                GatewayActivityType.SubAgentFailed,
                "subagent_failed",
                updated,
                parentAgentId.Value,
                $"Sub-agent '{subAgentId}' failed.");
        }

        if (string.IsNullOrWhiteSpace(parentAgentId.Value))
            return;

        try
        {
            // Dispatch half: deliver the completion follow-up to the parent session.
            await DispatchCompletionFollowUpAsync(subAgentId, normalizedSummary, updated, parentAgentId, childAgentId, ct);
        }
        finally
        {
            // Teardown half: always release the child agent/session, even if dispatch threw.
            await CleanupChildAgentAsync(subAgentId, updated.ChildSessionId, CancellationToken.None);
        }
    }

    /// <summary>
    /// Dispatch half of completion handling: builds the completion follow-up message and delivers
    /// it to the parent session via <see cref="_dispatcher"/>, recording wake telemetry. Delivery
    /// failures are logged and swallowed (the record-teardown in <see cref="OnCompletedAsync"/>
    /// still runs). Separated from the record-teardown so the two concerns are independently
    /// readable (#1565).
    /// </summary>
    /// <param name="subAgentId">The completing sub-agent's id.</param>
    /// <param name="normalizedSummary">The normalized result summary.</param>
    /// <param name="updated">The updated sub-agent info (final status/timestamps).</param>
    /// <param name="parentAgentId">The parent agent to wake (already non-empty).</param>
    /// <param name="childAgentId">The child agent id used as the wake sender (#526).</param>
    /// <param name="ct">Cancellation token for the dispatch.</param>
    private async Task DispatchCompletionFollowUpAsync(
        string subAgentId,
        string normalizedSummary,
        SubAgentInfo updated,
        AgentId parentAgentId,
        AgentId childAgentId,
        CancellationToken ct)
    {
        var completionMessage = new SubAgentCompletionMessage
        {
            SubAgentId = subAgentId,
            Status = DescribeStatus(updated.Status),
            Summary = normalizedSummary,
            CompletedAt = updated.CompletedAt ?? DateTimeOffset.UtcNow
        };

        var followUp = completionMessage.Content;

        try
        {
            _logger.LogInformation(
                "Dispatching sub-agent completion for parent agent '{ParentAgentId}' session '{ParentSessionId}' from sub-agent '{SubAgentId}'.",
                parentAgentId,
                updated.ParentSessionId,
                subAgentId);

            GatewayTelemetry.SubAgentParentWakeups.Add(1,
                new KeyValuePair<string, object?>("botnexus.parent.agent.id", parentAgentId),
                new KeyValuePair<string, object?>("botnexus.parent.session.id", updated.ParentSessionId),
                new KeyValuePair<string, object?>("botnexus.subagent.id", subAgentId));
            GatewayTelemetry.SubAgentWakeDispatched.Add(1,
                new KeyValuePair<string, object?>("botnexus.parent.agent.id", parentAgentId),
                new KeyValuePair<string, object?>("botnexus.parent.session.id", updated.ParentSessionId),
                new KeyValuePair<string, object?>("botnexus.subagent.id", subAgentId));

            await _dispatcher.DispatchAsync(new InboundMessage
            {
                ChannelType = ChannelKey.From("internal"),
                SenderId = $"subagent:{subAgentId}",
                Sender = CitizenId.Of(childAgentId),
                ChannelAddress = ChannelAddress.From(updated.ParentSessionId.Value),
                RoutingHints = new InboundMessageRoutingHints(
                    RequestedAgentId: parentAgentId,
                    RequestedSessionId: updated.ParentSessionId,
                    RequestedConversationId: null),
                Content = followUp,
                Metadata = new Dictionary<string, object?>
                {
                    ["messageType"] = "subagent-completion",
                    ["subAgentId"] = subAgentId
                }
            }, ct);
        }
        catch (Exception ex)
        {
            GatewayTelemetry.SubAgentWakeDeliveryFailed.Add(1,
                new KeyValuePair<string, object?>("botnexus.parent.agent.id", parentAgentId),
                new KeyValuePair<string, object?>("botnexus.parent.session.id", updated.ParentSessionId),
                new KeyValuePair<string, object?>("botnexus.subagent.id", subAgentId));
            _logger.LogWarning(
                ex,
                "Failed delivering completion follow-up for sub-agent '{SubAgentId}' to parent session '{ParentSessionId}'.",
                subAgentId,
                updated.ParentSessionId);
        }
    }

    private async Task RunSubAgentAsync(string subAgentId, IAgentHandle handle, string task, int timeoutSeconds)
    {
        if (!_records.TryGetValue(subAgentId, out var record) || record.TimeoutCts is not { } timeoutCts)
            return;

        try
        {
            var response = await handle.PromptAsync(task, timeoutCts.Token);
            if (timeoutCts.IsCancellationRequested)
            {
                await CompleteTimedOutAsync(subAgentId, timeoutSeconds);
            }
            else if (string.IsNullOrWhiteSpace(response.Content))
            {
                await CompleteFailedAsync(
                    subAgentId,
                    "Sub-agent failed because it returned an empty final response.");
            }
            else
            {
                await OnCompletedAsync(subAgentId, response.Content);
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            await CompleteTimedOutAsync(subAgentId, timeoutSeconds);
        }
        catch (Exception) when (timeoutCts.IsCancellationRequested)
        {
            // Some providers translate cancellation into a different exception. Once the
            // deadline has fired, timeout remains the authoritative terminal reason.
            await CompleteTimedOutAsync(subAgentId, timeoutSeconds);
        }
        catch (Exception ex)
        {
            await CompleteFailedAsync(subAgentId, $"Sub-agent failed: {ex.Message}");
        }
        finally
        {
            record.DisposeTimeout();
        }
    }

    private Task CompleteTimedOutAsync(string subAgentId, int timeoutSeconds)
        => CompleteTerminalAsync(
            subAgentId,
            SubAgentStatus.TimedOut,
            $"Sub-agent timed out after {timeoutSeconds} {(timeoutSeconds == 1 ? "second" : "seconds")}.");

    private Task CompleteFailedAsync(string subAgentId, string diagnostic)
        => CompleteTerminalAsync(subAgentId, SubAgentStatus.Failed, diagnostic);

    private async Task CompleteTerminalAsync(
        string subAgentId,
        SubAgentStatus status,
        string diagnostic)
    {
        if (TryUpdateSubAgent(
                subAgentId,
                current => current.Status == SubAgentStatus.Running
                    ? current with
                    {
                        Status = status,
                        CompletedAt = DateTimeOffset.UtcNow,
                        ResultSummary = diagnostic
                    }
                    : current,
                out var updated) && updated.Status == status)
        {
            await OnCompletedAsync(subAgentId, diagnostic);
        }
    }

    private static string DescribeStatus(SubAgentStatus status)
        => status switch
        {
            SubAgentStatus.Completed => "completed",
            SubAgentStatus.Failed => "failed",
            SubAgentStatus.TimedOut => "timed out",
            SubAgentStatus.Killed => "was killed",
            _ => "updated"
        };

    private bool TryUpdateSubAgent(string subAgentId, Func<SubAgentInfo, SubAgentInfo> updateFactory)
        => TryUpdateSubAgent(subAgentId, updateFactory, out _);

    private bool TryUpdateSubAgent(
        string subAgentId,
        Func<SubAgentInfo, SubAgentInfo> updateFactory,
        out SubAgentInfo updatedInfo)
    {
        if (_records.TryGetValue(subAgentId, out var record))
            return record.TryUpdateInfo(updateFactory, out updatedInfo);

        updatedInfo = default!;
        return false;
    }

    private async Task PublishLifecycleActivityAsync(
        GatewayActivityType type,
        string eventName,
        SubAgentInfo info,
        string? parentAgentId,
        string message)
    {
        try
        {
            await _activity.PublishAsync(new GatewayActivity
            {
                Type = type,
                AgentId = parentAgentId,
                SessionId = info.ParentSessionId.Value,
                Message = message,
                Data = new Dictionary<string, object?>
                {
                    ["event"] = eventName,
                    ["subAgent"] = info
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to publish sub-agent lifecycle event '{EventName}' for sub-agent '{SubAgentId}'.",
                eventName,
                info.SubAgentId);
        }
    }

    private async Task CleanupChildAgentAsync(string subAgentId, SessionId childSessionId, CancellationToken ct)
    {
        // The cleanup body must run at most once per sub-agent: it stops the child agent,
        // removes the dynamic deny-list, unregisters the descriptor and reclaims the
        // workspace. KillAsync and the OnCompletedAsync `finally` can both reach here, so
        // the once-only gate lives on the record (previously this role was implicitly served
        // by _childAgentIds.TryRemove succeeding exactly once).
        if (!_records.TryGetValue(subAgentId, out var record) || !record.TryBeginCleanup())
            return;

        // Start the record's retention clock and release the timeout source now that the sub-agent
        // has finished. The record itself stays in _records (for list_subagents / status queries)
        // until ReapCompletedRecords ages it out, but its CancellationTokenSource is an IDisposable
        // that must not be held for the process lifetime. DisposeTimeout is idempotent and a no-op
        // when an explicit kill already disposed it via CancelTimeout.
        record.MarkRetired(_timeProvider.GetUtcNow());
        record.DisposeTimeout();

        var childAgentId = record.ChildAgentId;

        // Remove the dynamic deny-list registered for this ephemeral sub-agent
        _policyProvider?.RemoveDynamicDenyList(childAgentId);

        try
        {
            await _supervisor.StopAsync(childAgentId, childSessionId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed stopping child agent '{ChildAgentId}' for sub-agent '{SubAgentId}'.",
                childAgentId,
                subAgentId);
        }
        finally
        {
            _registry.Unregister(childAgentId);
        }

        if (_workspaceManager is null)
            return;

        try
        {
            if (_workspaceManager.TryCleanupWorkspace(childAgentId.Value))
            {
                _logger.LogDebug(
                    "Cleaned up temporary workspace for child agent '{ChildAgentId}'.",
                    childAgentId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed cleaning temporary workspace for child agent '{ChildAgentId}'.",
                childAgentId);
        }
    }

    /// <summary>
    /// Evicts retired (finished) sub-agent records from the in-memory registry so it stays bounded
    /// on a long-lived gateway that spawns many sub-agents. A record is eligible once its child
    /// cleanup has run (<see cref="SubAgentRecord.RetiredAt"/> is set). Eligible records are evicted
    /// when they are older than the configured retention window, and — as a burst-spawn backstop —
    /// the oldest retired records beyond the configured count cap are evicted regardless of age.
    /// Running (not-yet-retired) records are never evicted. Each evicted record's timeout source is
    /// disposed (idempotent). Called opportunistically from spawn and the list/get read paths, so no
    /// background timer or hosted service is required (mirrors the <c>ProcessManager</c> reap, #1333).
    /// </summary>
    private void ReapCompletedRecords()
    {
        var options = _options.CurrentValue.SubAgents;
        var retentionMinutes = options.CompletedRecordRetentionMinutes;
        var maxRetained = options.MaxRetainedCompletedRecords;

        // Snapshot retired records once. ConcurrentDictionary enumeration is safe under concurrent
        // mutation; a record that retires between the snapshot and a TryRemove just survives to the
        // next reap.
        var retired = _records
            .Where(kvp => kvp.Value.RetiredAt is not null)
            .Select(kvp => (Id: kvp.Key, Record: kvp.Value, RetiredAt: kvp.Value.RetiredAt!.Value))
            .ToList();

        if (retired.Count == 0)
            return;

        var now = _timeProvider.GetUtcNow();
        var toEvict = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // (1) Time-based: anything older than the retention window.
        if (retentionMinutes > 0)
        {
            var cutoff = now - TimeSpan.FromMinutes(retentionMinutes);
            foreach (var entry in retired)
            {
                if (entry.RetiredAt <= cutoff)
                    toEvict.Add(entry.Id);
            }
        }

        // (2) Count-cap backstop: oldest retired records beyond the cap, regardless of age.
        if (maxRetained > 0 && retired.Count > maxRetained)
        {
            // Order by (RetiredAt, Sequence) so the eviction is a *total* order: when two records
            // share an identical RetiredAt (same-tick completions under TimeProvider.System), the
            // strictly-increasing spawn Sequence breaks the tie deterministically and the
            // oldest-spawned record is evicted first -- never a coin-flip on enumeration order (#1654).
            var overflow = retired
                .OrderBy(entry => entry.RetiredAt)
                .ThenBy(entry => entry.Record.Sequence)
                .Take(retired.Count - maxRetained)
                .Select(entry => entry.Id);
            foreach (var id in overflow)
                toEvict.Add(id);
        }

        foreach (var id in toEvict)
        {
            if (_records.TryRemove(id, out var removed))
            {
                // Defensive: cleanup already disposed it, but DisposeTimeout is idempotent.
                removed.DisposeTimeout();
            }
        }
    }

    /// <summary>
    /// Emits one sub-agent lifecycle security event to the trusted sink. The actor id is a hash of
    /// the parent session id so the trusted record carries a stable pseudonym instead of the raw id,
    /// and the target is the (already non-sensitive) sub-agent id. Best-effort: a null sink is a
    /// no-op and any sink fault is swallowed/logged so spawn/kill behaviour is never altered. These
    /// events go only to the trusted sink, never the public activity/diagnostic stream.
    /// </summary>
    private void EmitSubAgentEvent(string action, SessionId parentSessionId, string subAgentId)
    {
        if (_securityEvents is null)
            return;

        try
        {
            var evt = new SecurityEvent(
                SecurityEventCategory.Tool,
                action,
                SecurityEventOutcome.Success,
                SecurityEventSeverity.Info,
                Actor: new SecurityEventActor(SecurityActorKind.Agent, HashActor(parentSessionId.Value)),
                Target: new SecurityEventTarget(SecurityTargetKind.Tool, subAgentId),
                Control: SecurityControlFamily.Sandbox);
            _securityEvents.Record(evt);
        }
        catch (Exception ex)
        {
            // Observability must never break the spawn/kill path; swallow and log.
            _logger.LogWarning(ex, "Failed to record sub-agent security event for action {Action}.", action);
        }
    }

    /// <summary>
    /// Hashes a session/agent id to a short, opaque hex token so security events carry a stable
    /// pseudonym instead of the raw id. SHA-256 with a fixed prefix is sufficient for correlation;
    /// it is not reversible and never stores the plaintext.
    /// </summary>
    private static string HashActor(string id)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(id ?? string.Empty));
        var sb = new StringBuilder(16);
        for (var i = 0; i < 8; i++)
            sb.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    /// <summary>
    /// Counts the sub-agent nesting depth encoded in a session ID.
    /// A top-level session has depth 0; each <c>::subagent::</c> separator adds one level.
    /// </summary>
    private static int CountSubAgentDepth(SessionId sessionId)
    {
        const string separator = "::subagent::";
        var value = sessionId.Value;
        var depth = 0;
        var searchFrom = 0;
        while ((searchFrom = value.IndexOf(separator, searchFrom, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            depth++;
            searchFrom += separator.Length;
        }
        return depth;
    }

    /// <summary>
    /// Single source of truth for one sub-agent's mutable runtime state. Consolidates what were
    /// previously five parallel dictionaries keyed by the same sub-agent id (#1385): the live
    /// <see cref="SubAgentInfo"/>, the parent/child <see cref="AgentId"/> values, the timeout
    /// <see cref="CancellationTokenSource"/>, and the completion/cleanup once-only gates. Folding
    /// them into one record means an add or teardown is a single atomic dictionary operation and
    /// the maps can no longer drift apart — the drift between <c>_childAgentIds</c> and the live
    /// entry is exactly what forced the old synthetic-child-id fallback in <c>OnCompletedAsync</c>.
    /// </summary>
    private sealed class SubAgentRecord(SubAgentInfo info, AgentId parentAgentId, AgentId childAgentId)
    {
        // Process-wide monotonic spawn counter. Assigned once per record at construction so every
        // record carries a strictly-increasing "spawn age" that is independent of wall-clock
        // resolution. Used as the deterministic tie-break in the count-cap eviction so that two
        // records sharing an identical RetiredAt (a real possibility under TimeProvider.System when
        // sub-agents complete within the same tick) evict the oldest-spawned one first, instead of
        // letting ConcurrentDictionary enumeration order pick a non-deterministic victim (#1654).
        private static long _spawnSequenceCounter;

        private SubAgentInfo _info = info;
        private int _completionProcessed;
        private int _cleanupStarted;
        private long _retiredAtTicks;

        /// <summary>
        /// Strictly-increasing spawn-order sequence, assigned once at construction. Provides a total,
        /// deterministic order for retired records when their <see cref="RetiredAt"/> timestamps tie
        /// (the count-cap eviction orders by <c>(RetiredAt, Sequence)</c> so the oldest-spawned record
        /// is always the one evicted). Lower means spawned earlier.
        /// </summary>
        public long Sequence { get; } = Interlocked.Increment(ref _spawnSequenceCounter);

        /// <summary>Parent agent id captured at spawn. Immutable for the record's lifetime.</summary>
        public AgentId ParentAgentId { get; } = parentAgentId;

        /// <summary>Child agent id captured at spawn. Immutable for the record's lifetime.</summary>
        public AgentId ChildAgentId { get; } = childAgentId;

        /// <summary>
        /// The timeout cancellation source. Set once after the spawn budget is resolved and the
        /// record is already registered, so the background run loop can read it. Cleared (set to
        /// null) atomically on cancel/dispose so a late kill cannot double-dispose.
        /// </summary>
        public CancellationTokenSource? TimeoutCts
        {
            get => Volatile.Read(ref _timeoutCts);
            set => Volatile.Write(ref _timeoutCts, value);
        }

        /// <summary>The current immutable runtime snapshot. Swapped atomically via <see cref="TryUpdateInfo"/>.</summary>
        public SubAgentInfo Info => Volatile.Read(ref _info);

        /// <summary>
        /// Atomically applies <paramref name="updateFactory"/> to the current snapshot using a
        /// compare-and-swap loop — the same lock-free update semantics the old
        /// <c>ConcurrentDictionary.TryUpdate</c> loop provided.
        /// </summary>
        public bool TryUpdateInfo(Func<SubAgentInfo, SubAgentInfo> updateFactory, out SubAgentInfo updatedInfo)
        {
            while (true)
            {
                var current = Volatile.Read(ref _info);
                var updated = updateFactory(current);
                if (ReferenceEquals(Interlocked.CompareExchange(ref _info, updated, current), current))
                {
                    updatedInfo = updated;
                    return true;
                }
            }
        }

        /// <summary>Returns true exactly once — the first caller wins the completion gate.</summary>
        public bool TryBeginCompletion() => Interlocked.CompareExchange(ref _completionProcessed, 1, 0) == 0;

        /// <summary>Returns true exactly once — the first caller wins the child-cleanup gate.</summary>
        public bool TryBeginCleanup() => Interlocked.CompareExchange(ref _cleanupStarted, 1, 0) == 0;

        /// <summary>
        /// The instant this record's child cleanup ran (i.e. the sub-agent finished and its resources
        /// were reclaimed), or <see langword="null"/> while the sub-agent is still live. Used to age
        /// the record out of the in-memory registry after the configured retention window. Stored as
        /// ticks so the read/write is a single atomic operation.
        /// </summary>
        public DateTimeOffset? RetiredAt
        {
            get
            {
                var ticks = Interlocked.Read(ref _retiredAtTicks);
                return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
            }
        }

        /// <summary>
        /// Stamps <see cref="RetiredAt"/> exactly once (the first caller wins) so a record's retention
        /// clock starts when its cleanup runs, not on a later re-entrant call.
        /// </summary>
        public void MarkRetired(DateTimeOffset retiredAt)
        {
            Interlocked.CompareExchange(ref _retiredAtTicks, retiredAt.UtcTicks, 0);
        }

        /// <summary>Cancels and disposes the timeout source (used by an explicit kill). Idempotent.</summary>
        public void CancelTimeout()
        {
            var cts = Interlocked.Exchange(ref _timeoutCts, null);
            if (cts is null)
                return;
            cts.Cancel();
            cts.Dispose();
        }

        /// <summary>Disposes the timeout source without cancelling (used by the run loop's finally). Idempotent.</summary>
        public void DisposeTimeout()
        {
            Interlocked.Exchange(ref _timeoutCts, null)?.Dispose();
        }

        // Backing field for TimeoutCts so set / read / Cancel / Dispose all touch one field and
        // Cancel/Dispose can clear it atomically, avoiding a double-dispose race between an
        // in-flight kill and the run loop's finally block.
        private CancellationTokenSource? _timeoutCts;
    }
}
