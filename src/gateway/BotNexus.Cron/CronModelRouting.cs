using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Cron;

/// <summary>Immutable model selection for one scheduled occurrence; never a conversation edit.</summary>
/// <param name="RequestedModel">Winning configured selection before registry canonicalization.</param>
/// <param name="Provider">Registered provider.</param>
/// <param name="ModelId">Registered model identifier.</param>
/// <param name="Source">Winning layer: job, global, conversation, or agent.</param>
public sealed record CronModelSelection(string RequestedModel, string Provider, string ModelId, string Source)
{
    /// <summary>Qualified selection passed through the existing per-session override path.</summary>
    public string QualifiedModel => $"{Provider}/{ModelId}";
}

/// <summary>Single dispatch-time authority for cron model precedence and validation.</summary>
public static class CronModelRouting
{
    /// <summary>Whether the action runs a model rather than deterministic work.</summary>
    public static bool UsesModel(string actionType) => actionType.ToLowerInvariant() is
        "agent-prompt" or "heartbeat" or "skill-review" or "memory-dreaming" or "agent-converse";

    /// <summary>Projects the current default without persisting it into the authored job.</summary>
    public static CronJob Project(CronJob job, string? defaultModel)
        => job with { ConfiguredDefaultModel = string.IsNullOrWhiteSpace(defaultModel) ? null : defaultModel.Trim() };

    /// <summary>Resolves one occurrence against the shared registry and agent permission authority.</summary>
    public static CronModelSelection? Resolve(string actionType, string? jobModel, string? defaultModel,
        string? conversationModel, AgentDescriptor descriptor, ModelRegistry? registry)
    {
        if (!UsesModel(actionType)) return null;
        var (requested, source) = !string.IsNullOrWhiteSpace(jobModel) ? (jobModel.Trim(), "job")
            : !string.IsNullOrWhiteSpace(defaultModel) ? (defaultModel.Trim(), "global")
            : !string.IsNullOrWhiteSpace(conversationModel) ? (conversationModel.Trim(), "conversation")
            : ($"{descriptor.ApiProvider}/{descriptor.ModelId}", "agent");
        // Standing conversation IDs are scoped to the agent's provider by the existing
        // isolation resolver; bare authored job/global IDs retain cross-provider lookup.
        var lookup = source == "conversation" && !requested.Contains('/')
            ? $"{descriptor.ApiProvider}/{requested}" : requested;
        var result = CronModelPreflight.Resolve(registry, lookup);
        // Legacy hosts may dispatch inherited models without a registry. Do not invent an
        // effective audit or pin a fallback that the downstream runtime still owns.
        if (result.Kind == CronModelPreflightKind.RegistryUnavailable && source != "global")
            return null;
        if (result.Kind != CronModelPreflightKind.Resolved || result.Provider is null || result.ModelId is null)
            throw new InvalidOperationException(result.Reason ?? "Cron model cannot be resolved: model registry is unavailable.");
        if (!AgentModelPermission.IsPermitted(descriptor, result.ModelId))
        {
            // Match the existing isolation authority: a stale forbidden standing override is
            // ignored, while authored job/global pins must fail closed.
            if (source == "conversation")
                return Resolve(actionType, null, null, null, descriptor, registry);
            throw new InvalidOperationException(AgentModelPermission.FormatRejection(descriptor, result.ModelId));
        }
        return new CronModelSelection(requested, result.Provider, result.ModelId, source);
    }
}
