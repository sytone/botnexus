using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Federation;

public sealed class CrossWorldInboundAuthService(IOptionsMonitor<PlatformConfig> platformConfig)
{
    private readonly IOptionsMonitor<PlatformConfig> _platformConfig = platformConfig;

    public bool TryAuthorize(
        string sourceWorldId,
        AgentId targetAgentId,
        string? presentedApiKey,
        out string error)
    {
        var inbound = _platformConfig.CurrentValue.Gateway?.CrossWorld?.Inbound;
        if (inbound is null || !inbound.Enabled)
        {
            error = "Cross-world inbound federation is disabled.";
            return false;
        }

        if (inbound.AllowedWorlds is null ||
            !inbound.AllowedWorlds.Any(world => string.Equals(world, sourceWorldId, StringComparison.OrdinalIgnoreCase)))
        {
            error = $"Source world '{sourceWorldId}' is not allowed.";
            return false;
        }

        if (inbound.ApiKeys is null ||
            !TryGetApiKey(inbound.ApiKeys, sourceWorldId, out var expectedApiKey) ||
            string.IsNullOrWhiteSpace(expectedApiKey))
        {
            error = $"No inbound API key configured for source world '{sourceWorldId}'.";
            return false;
        }

        if (!string.Equals(expectedApiKey, presentedApiKey, StringComparison.Ordinal))
        {
            error = $"Invalid cross-world API key for source world '{sourceWorldId}'.";
            return false;
        }

        var permission = ResolvePermission(sourceWorldId);
        if (permission is null || !permission.AllowInbound)
        {
            error = $"Inbound cross-world permission denied for world '{sourceWorldId}'.";
            return false;
        }

        if (permission.AllowedAgents is not { Count: > 0 })
        {
            error = string.Empty;
            return true;
        }

        if (permission.AllowedAgents.Any(agent => string.Equals(agent, targetAgentId.Value, StringComparison.OrdinalIgnoreCase)))
        {
            error = string.Empty;
            return true;
        }

        error = $"Target agent '{targetAgentId}' is not allowed for inbound world '{sourceWorldId}'.";
        return false;
    }

    private CrossWorldPermissionConfig? ResolvePermission(string sourceWorldId)
        => _platformConfig.CurrentValue.Gateway?.CrossWorldPermissions?
            .FirstOrDefault(permission => string.Equals(permission.TargetWorldId, sourceWorldId, StringComparison.OrdinalIgnoreCase));

    private static bool TryGetApiKey(IReadOnlyDictionary<string, string> keys, string sourceWorldId, out string? apiKey)
    {
        if (keys.TryGetValue(sourceWorldId, out apiKey))
            return true;

        var match = keys.FirstOrDefault(entry => string.Equals(entry.Key, sourceWorldId, StringComparison.OrdinalIgnoreCase));
        apiKey = string.IsNullOrWhiteSpace(match.Key) ? null : match.Value;
        return !string.IsNullOrWhiteSpace(apiKey);
    }
}
