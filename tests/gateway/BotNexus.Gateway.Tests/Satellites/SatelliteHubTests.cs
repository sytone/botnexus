using BotNexus.Domain.World;
using BotNexus.Extensions.Channels.SignalR;
using BotNexus.Gateway.Abstractions.Satellites;
using BotNexus.Gateway.Abstractions.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests.Satellites;

public sealed class SatelliteHubTests
{
    [Fact]
    public async Task OnConnectedAsync_AuthenticatedSatellite_BecomesOnlineWithoutCallerSuppliedIdentity()
    {
        var registry = new RecordingRegistry(Satellite("desktop-01", "jon"));
        var hub = CreateHub(registry, "connection-1", SatelliteIdentity("desktop-01"));

        await hub.OnConnectedAsync();

        registry.OnlineCalls.ShouldBe([("desktop-01", "connection-1")]);
        registry.GetById("desktop-01")!.OwnerUserId.ShouldBe("jon");
    }

    [Theory]
    [MemberData(nameof(RejectedIdentities))]
    public async Task OnConnectedAsync_RejectedIdentity_FailsClosed(GatewayCallerIdentity? identity)
    {
        var registry = new RecordingRegistry(Satellite("desktop-01", "jon"));
        var hub = CreateHub(registry, "connection-1", identity);

        await Should.ThrowAsync<HubException>(() => hub.OnConnectedAsync());

        registry.OnlineCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task OnConnectedAsync_UnknownSatellite_FailsClosed()
    {
        var registry = new RecordingRegistry(Satellite("desktop-01", "jon"));
        var hub = CreateHub(registry, "connection-1", SatelliteIdentity("unknown"));

        await Should.ThrowAsync<HubException>(() => hub.OnConnectedAsync());

        registry.OnlineCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Heartbeat_ActiveConnection_UpdatesRegistry()
    {
        var registry = new RecordingRegistry(Satellite("desktop-01", "jon"));
        var hub = CreateHub(registry, "connection-1", SatelliteIdentity("desktop-01"));
        await hub.OnConnectedAsync();

        await hub.Heartbeat();

        registry.HeartbeatCalls.ShouldBe([("desktop-01", "connection-1")]);
    }

    [Fact]
    public async Task Heartbeat_SupersededConnection_FailsClosed()
    {
        var registry = new RecordingRegistry(Satellite("desktop-01", "jon"));
        var older = CreateHub(registry, "connection-1", SatelliteIdentity("desktop-01"));
        var replacement = CreateHub(registry, "connection-2", SatelliteIdentity("desktop-01"));
        await older.OnConnectedAsync();
        await replacement.OnConnectedAsync();

        await Should.ThrowAsync<HubException>(() => older.Heartbeat());

        registry.GetById("desktop-01")!.ConnectionId.ShouldBe("connection-2");
    }

    [Fact]
    public async Task OnDisconnectedAsync_SupersededConnection_DoesNotTakeReplacementOffline()
    {
        var registry = new RecordingRegistry(Satellite("desktop-01", "jon"));
        var older = CreateHub(registry, "connection-1", SatelliteIdentity("desktop-01"));
        var replacement = CreateHub(registry, "connection-2", SatelliteIdentity("desktop-01"));
        await older.OnConnectedAsync();
        await replacement.OnConnectedAsync();

        await older.OnDisconnectedAsync(null);

        registry.GetById("desktop-01")!.Status.ShouldBe(SatelliteStatus.Online);
        registry.GetById("desktop-01")!.ConnectionId.ShouldBe("connection-2");
    }

    [Fact]
    public async Task OnDisconnectedAsync_ActiveConnection_MarksSatelliteOffline()
    {
        var registry = new RecordingRegistry(Satellite("desktop-01", "jon"));
        var hub = CreateHub(registry, "connection-1", SatelliteIdentity("desktop-01"));
        await hub.OnConnectedAsync();

        await hub.OnDisconnectedAsync(null);

        registry.GetById("desktop-01")!.Status.ShouldBe(SatelliteStatus.Offline);
    }

    public static TheoryData<GatewayCallerIdentity?> RejectedIdentities => new()
    {
        null,
        new GatewayCallerIdentity { CallerId = "gateway-api-key", Permissions = ["*"] , IsAdmin = true },
        new GatewayCallerIdentity { CallerId = "satellite:desktop-01", Permissions = ["satellite:heartbeat"] },
        new GatewayCallerIdentity { CallerId = "satellite:", Permissions = ["satellite:connect", "satellite:heartbeat"] }
    };

    private static SatelliteHub CreateHub(
        ISatelliteRegistry registry,
        string connectionId,
        GatewayCallerIdentity? identity)
        => new(registry, NullLogger<SatelliteHub>.Instance)
        {
            Context = new SatelliteHubCallerContext(connectionId, identity)
        };

    private static GatewayCallerIdentity SatelliteIdentity(string satelliteId) => new()
    {
        CallerId = $"satellite:{satelliteId}",
        DisplayName = satelliteId,
        Permissions = ["satellite:connect", "satellite:heartbeat"]
    };

    private static SatelliteConnectionInfo Satellite(string id, string owner) => new()
    {
        Id = id,
        DisplayName = id,
        Platform = "windows",
        OwnerUserId = owner
    };

    private sealed class SatelliteHubCallerContext : HubCallerContext
    {
        private readonly Dictionary<object, object?> _items = [];

        public SatelliteHubCallerContext(string connectionId, GatewayCallerIdentity? identity)
        {
            ConnectionId = connectionId;
            var httpContext = new DefaultHttpContext();
            if (identity is not null)
                httpContext.Items[GatewayAuthHttpContext.CallerIdentityItemKey] = identity;

            var features = new FeatureCollection();
            features.Set<IHttpContextFeature>(new HttpContextFeature { HttpContext = httpContext });
            Features = features;
        }

        public override string ConnectionId { get; }
        public override string? UserIdentifier => null;
        public override System.Security.Claims.ClaimsPrincipal? User => null;
        public override IDictionary<object, object?> Items => _items;
        public override IFeatureCollection Features { get; }
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() { }

        private sealed class HttpContextFeature : IHttpContextFeature
        {
            public HttpContext? HttpContext { get; set; }
        }
    }

    private sealed class RecordingRegistry(params SatelliteConnectionInfo[] satellites) : ISatelliteRegistry
    {
        private readonly Dictionary<string, SatelliteConnectionInfo> _satellites =
            satellites.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);

        public List<(string SatelliteId, string ConnectionId)> OnlineCalls { get; } = [];
        public List<(string SatelliteId, string ConnectionId)> HeartbeatCalls { get; } = [];

        public IReadOnlyList<SatelliteConnectionInfo> GetAll() => _satellites.Values.ToList();
        public SatelliteConnectionInfo? GetById(string satelliteId) => _satellites.GetValueOrDefault(satelliteId);
        public IReadOnlyList<SatelliteConnectionInfo> GetOnlineForUser(string userId) =>
            _satellites.Values.Where(s => s.Status == SatelliteStatus.Online && s.OwnerUserId == userId).ToList();

        public bool MarkOnline(string satelliteId, string connectionId)
        {
            if (!_satellites.TryGetValue(satelliteId, out var satellite))
                return false;
            satellite.Status = SatelliteStatus.Online;
            satellite.ConnectionId = connectionId;
            OnlineCalls.Add((satelliteId, connectionId));
            return true;
        }

        public bool MarkOffline(string satelliteId, string connectionId)
        {
            if (!_satellites.TryGetValue(satelliteId, out var satellite) || satellite.ConnectionId != connectionId)
                return false;
            satellite.Status = SatelliteStatus.Offline;
            satellite.ConnectionId = null;
            return true;
        }

        public bool RecordHeartbeat(string satelliteId, string connectionId)
        {
            if (!_satellites.TryGetValue(satelliteId, out var satellite) || satellite.ConnectionId != connectionId)
                return false;
            HeartbeatCalls.Add((satelliteId, connectionId));
            return true;
        }

        public IReadOnlyList<SatelliteConnectionInfo> GetStaleSatellites() => [];
    }
}
