using BotNexus.Gateway.Abstractions.Satellites;
using BotNexus.Gateway.Abstractions.Security;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace BotNexus.Extensions.Channels.SignalR;

/// <summary>
/// Authenticated SignalR data plane for configured satellite nodes. Satellite identity is derived
/// exclusively from the gateway-authenticated caller; hub arguments never select a satellite or owner.
/// </summary>
public sealed class SatelliteHub : Hub
{
    private const string SatelliteCallerPrefix = "satellite:";
    private const string SatelliteIdItemKey = "BotNexus.Satellite.Id";
    private const string ConnectPermission = "satellite:connect";
    private const string HeartbeatPermission = "satellite:heartbeat";

    private readonly ISatelliteRegistry _registry;
    private readonly ILogger<SatelliteHub> _logger;

    /// <summary>Creates the satellite connection hub.</summary>
    public SatelliteHub(ISatelliteRegistry registry, ILogger<SatelliteHub> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    /// <inheritdoc />
    public override async Task OnConnectedAsync()
    {
        var identity = GetAuthenticatedIdentity();
        RequirePermission(identity, ConnectPermission);
        var satelliteId = GetSatelliteId(identity);

        if (_registry.GetById(satelliteId) is null ||
            !_registry.MarkOnline(satelliteId, Context.ConnectionId))
        {
            throw new HubException("The authenticated satellite is not enabled or configured.");
        }

        Context.Items[SatelliteIdItemKey] = satelliteId;
        _logger.LogInformation(
            "Satellite {SatelliteId} connected (connection={ConnectionId})",
            satelliteId,
            Context.ConnectionId);

        await base.OnConnectedAsync();
    }

    /// <summary>Records a heartbeat for this authenticated connection.</summary>
    public Task Heartbeat()
    {
        var identity = GetAuthenticatedIdentity();
        RequirePermission(identity, HeartbeatPermission);
        var satelliteId = GetBoundSatelliteId(identity);

        if (!_registry.RecordHeartbeat(satelliteId, Context.ConnectionId))
            throw new HubException("This satellite connection is no longer active.");

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.Items.TryGetValue(SatelliteIdItemKey, out var value) &&
            value is string satelliteId)
        {
            _registry.MarkOffline(satelliteId, Context.ConnectionId);
        }

        await base.OnDisconnectedAsync(exception);
    }

    private GatewayCallerIdentity GetAuthenticatedIdentity()
    {
        var httpContext = Context.GetHttpContext();
        if (httpContext?.Items.TryGetValue(GatewayAuthHttpContext.CallerIdentityItemKey, out var value) != true ||
            value is not GatewayCallerIdentity identity)
        {
            throw new HubException("Satellite authentication is required.");
        }

        return identity;
    }

    private string GetBoundSatelliteId(GatewayCallerIdentity identity)
    {
        var authenticatedSatelliteId = GetSatelliteId(identity);
        if (!Context.Items.TryGetValue(SatelliteIdItemKey, out var value) ||
            value is not string boundSatelliteId ||
            !string.Equals(boundSatelliteId, authenticatedSatelliteId, StringComparison.OrdinalIgnoreCase))
        {
            throw new HubException("This connection is not registered to an authenticated satellite.");
        }

        return boundSatelliteId;
    }

    private static string GetSatelliteId(GatewayCallerIdentity identity)
    {
        if (!identity.CallerId.StartsWith(SatelliteCallerPrefix, StringComparison.Ordinal) ||
            identity.CallerId.Length == SatelliteCallerPrefix.Length)
        {
            throw new HubException("The authenticated caller is not a satellite.");
        }

        return identity.CallerId[SatelliteCallerPrefix.Length..];
    }

    private static void RequirePermission(GatewayCallerIdentity identity, string permission)
    {
        if (!identity.Permissions.Contains(permission, StringComparer.Ordinal))
            throw new HubException($"The authenticated satellite lacks the required '{permission}' permission.");
    }
}
