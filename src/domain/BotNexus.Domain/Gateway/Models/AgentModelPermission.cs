namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Central authority for an agent's model allow-list.
/// </summary>
public static class AgentModelPermission
{
    /// <summary>
    /// Returns whether <paramref name="modelId"/> is permitted for <paramref name="descriptor"/>.
    /// An empty allow-list means unrestricted.
    /// </summary>
    public static bool IsPermitted(AgentDescriptor descriptor, string modelId)
        => descriptor.AllowedModelIds.Count == 0
            || descriptor.AllowedModelIds.Contains(modelId, StringComparer.OrdinalIgnoreCase);

    /// <summary>Builds operator-facing text naming the configured permitted set.</summary>
    public static string FormatRejection(AgentDescriptor descriptor, string modelId)
        => $"Model '{modelId}' is not permitted for agent '{descriptor.AgentId}'. " +
           $"Permitted models: {string.Join(", ", descriptor.AllowedModelIds)}.";
}
