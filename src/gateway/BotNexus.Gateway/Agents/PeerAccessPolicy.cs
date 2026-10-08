using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Gateway.Agents;

/// <summary>
/// Keeps registered-target delegation, conversation admission and discovery on the same peer grants.
/// </summary>
internal static class PeerAccessPolicy
{
    internal static bool IsAllowed(
        AgentExchangeOptions options,
        AgentDescriptor? initiator,
        AgentId targetId,
        AgentDescriptor? target)
    {
        if (options.IsOpen)
            return true;
        if (initiator is null)
            return false;
        if (initiator.SubAgentIds.Contains(targetId.Value, StringComparer.OrdinalIgnoreCase))
            return true;
        if (initiator.SubAgentRoles.Count == 0 || target is null
            || !target.Metadata.TryGetValue("role", out var rawRole))
            return false;

        // Metadata is untyped. Only an actual string is a role; ToString coercion can turn
        // unrelated scalar/object values into a configured grant, and GetString throws on nonstrings.
        var role = rawRole switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => null
        };
        return !string.IsNullOrWhiteSpace(role)
            && initiator.SubAgentRoles.Contains(role, StringComparer.OrdinalIgnoreCase);
    }
}
