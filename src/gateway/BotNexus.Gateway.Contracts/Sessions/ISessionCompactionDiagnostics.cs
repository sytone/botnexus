using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Abstractions.Sessions;

/// <summary>Read-only, fresh policy diagnostics for a session owned by the local conversation/world.</summary>
public interface ISessionCompactionDiagnostics
{
    /// <summary>
    /// Resolves current policy and persisted token measurements. Null means ownership/state is
    /// unavailable; policy read failures propagate rather than publishing relaxed or cached policy.
    /// </summary>
    Task<CompactionPolicyDiagnostics?> ResolveAsync(AgentId agentId, SessionId sessionId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Safe projection of current token trigger policy. No prompts, configuration objects or metadata
/// bags are exposed. Counts describe token pressure, not the separate oversized-entry trigger.
/// </summary>
public sealed record CompactionPolicyDiagnostics
{
    /// <summary>Current scoped working window; null when the resolver cannot establish one.</summary>
    public int? ScopedContextWindowTokens { get; init; }
    /// <summary>Runtime policy window, including the configured fallback when scope is unknown.</summary>
    public int ContextWindowTokens { get; init; }
    /// <summary>Configured fraction used for the ratio safety threshold.</summary>
    public double TokenThresholdRatio { get; init; }
    /// <summary>Ratio-only threshold from the shared runtime policy.</summary>
    public int RatioSafetyThresholdTokens { get; init; }
    /// <summary>Scoped positive ceiling, or null when no ceiling applies.</summary>
    public int? CompactionTokenCeiling { get; init; }
    /// <summary>Conversation, agent or world; null when no scoped ceiling applies.</summary>
    public string? CompactionTokenCeilingSource { get; init; }
    /// <summary>Shared runtime threshold after capping the ratio threshold by the ceiling.</summary>
    public int EffectiveThresholdTokens { get; init; }
    /// <summary>Local estimate over persisted LLM-visible history; excludes prompt and tools.</summary>
    public int LocalEstimatedTokens { get; init; }
    /// <summary>Last completed provider request count; null when missing, invalid or cleared.</summary>
    public int? ProviderPromptTokens { get; init; }
    /// <summary>Producer metadata key; null when no valid provider measurement is available.</summary>
    public string? ProviderMeasurementSource { get; init; }
    /// <summary>Provider count / local estimate; null when either cannot support the ratio.</summary>
    public double? ProviderToEstimateRatio { get; init; }
}
