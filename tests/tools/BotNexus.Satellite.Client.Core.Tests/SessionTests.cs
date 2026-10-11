using BotNexus.Satellite.Client.Core;

namespace BotNexus.Satellite.Client.Core.Tests;

public sealed class SessionTests
{
    [Fact]
    public async Task Run_StoredCredentials_UsesDedicatedHubAndSeparateHeaders()
    {
        var h = new Harness();
        h.Delay.OnDelay = _ => h.Stop.Cancel();
        await h.Run();
        h.Store.Loads.ShouldBe(1);
        var request = h.Transport.Requests.ShouldHaveSingleItem();
        request.HubUri.ShouldBe(new Uri("https://demo.devtunnels.ms/hub/satellite"));
        request.Headers["X-Api-Key"].ShouldBe("gateway-secret");
        request.Headers["X-Tunnel-Authorization"].ShouldBe("tunnel tunnel-secret");
        request.Headers.ShouldNotContainKey("Cookie");
        request.HubUri.Query.ShouldBeEmpty();
        request.HubUri.Fragment.ShouldBeEmpty();
        h.States.ShouldBe(new[] { ClientState.Connecting, ClientState.Online, ClientState.Offline });
        h.Connection.Disposals.ShouldBe(1);
    }

    [Theory]
    [InlineData(null, "tunnel-secret")]
    [InlineData("", "tunnel-secret")]
    [InlineData("gateway-secret", null)]
    [InlineData("gateway-secret", " ")]
    public async Task Run_MissingRequiredCredential_NeedsAuthenticationWithoutConnecting(string? key, string? token)
    {
        var h = new Harness();
        h.Store.Credentials = new ClientCredentials { GatewayApiKey = key, TunnelAccessToken = token };
        await h.Run();
        h.Transport.Requests.ShouldBeEmpty();
        h.States.ShouldBe(new[] { ClientState.NeedsAuthentication });
        h.Browser.Opened.ShouldBeEmpty();
    }

    [Fact]
    public async Task Run_DirectGateway_DoesNotRequireOrSendTunnelToken()
    {
        var h = new Harness();
        h.Options = new SatelliteClientOptions { GatewayEndpoint = "https://gateway.example" };
        h.Store.Credentials = new ClientCredentials { GatewayApiKey = "gateway-secret" };
        h.Delay.OnDelay = _ => h.Stop.Cancel();
        await h.Run();
        h.Transport.Requests.ShouldHaveSingleItem().Headers.ShouldNotContainKey("X-Tunnel-Authorization");
        h.States.ShouldContain(ClientState.Online);
    }

    [Theory]
    [InlineData(ConnectionFailure.AuthenticationRequired)]
    [InlineData(ConnectionFailure.AccessDenied)]
    public async Task Run_AuthenticationFailure_StopsRetryingAndDoesNotOpenBrowserAutomatically(ConnectionFailure failure)
    {
        var h = new Harness();
        h.Transport.Connect = _ => throw new SatelliteConnectionException(failure);
        await h.Run();
        h.States.ShouldBe(new[] { ClientState.Connecting, ClientState.NeedsAuthentication });
        h.Transport.Requests.Count.ShouldBe(1);
        h.Delay.Durations.ShouldBeEmpty();
        h.Browser.Opened.ShouldBeEmpty();
    }

    [Fact]
    public async Task Run_NetworkFailures_RetriesWithBoundedBackoffAndCancellation()
    {
        var h = new Harness();
        h.Transport.Connect = _ => throw new SatelliteConnectionException(ConnectionFailure.Transient);
        h.Delay.OnDelay = count => { if (count == 7) h.Stop.Cancel(); };
        await h.Run();
        h.Delay.Durations.ShouldBe(new[] { 1, 2, 4, 8, 16, 30, 30 }.Select(seconds => TimeSpan.FromSeconds(seconds)));
        h.States.First().ShouldBe(ClientState.Connecting);
        h.States.ShouldContain(ClientState.Reconnecting);
        h.States.Last().ShouldBe(ClientState.Offline);
        h.Transport.Requests.Count.ShouldBe(7);
        h.Delay.Clocks.ShouldAllBe(clock => ReferenceEquals(clock, h.Clock));
    }

    [Fact]
    public async Task Run_HeartbeatScheduling_WaitsBeforeSendingAndStopsOnCancellation()
    {
        var h = new Harness();
        h.Delay.OnDelay = count =>
        {
            h.Connection.Heartbeats.ShouldBe(count - 1);
            if (count == 3) h.Stop.Cancel();
        };
        await h.Run();
        h.Delay.Durations.ShouldBe(Enumerable.Repeat(TimeSpan.FromSeconds(30), 3));
        h.Delay.Clocks.ShouldAllBe(clock => ReferenceEquals(clock, h.Clock));
        h.Connection.Heartbeats.ShouldBe(2);
        h.Connection.Disposals.ShouldBe(1);
    }

    [Theory]
    [InlineData(ConnectionFailure.Transient)]
    [InlineData(ConnectionFailure.Replaced)]
    public async Task Run_ActiveHeartbeatFailure_DisposesOldConnectionAndReconnects(ConnectionFailure failure)
    {
        var h = new Harness();
        var replacement = new FakeConnection();
        h.Connection.OnHeartbeat = () => throw new SatelliteConnectionException(failure);
        h.Transport.Connect = count => count == 1 ? h.Connection : replacement;
        h.Delay.OnDelay = count => { if (count == 3) h.Stop.Cancel(); };
        await h.Run();
        h.Transport.Requests.Count.ShouldBe(2);
        h.Connection.Heartbeats.ShouldBe(1);
        h.Connection.Disposals.ShouldBe(1);
        replacement.Disposals.ShouldBe(1);
        h.Delay.Durations.ShouldBe(new[] { 30, 1, 30 }.Select(seconds => TimeSpan.FromSeconds(seconds)));
        h.States.ShouldBe(new[] { ClientState.Connecting, ClientState.Online, ClientState.Reconnecting, ClientState.Online, ClientState.Offline });
    }

    [Fact]
    public async Task Run_ClosedConnection_ReconnectsWithoutWaitingForHeartbeatDeadline()
    {
        var h = new Harness();
        var replacement = new FakeConnection();
        h.Connection.ClosedSignal.SetResult(ConnectionFailure.Transient);
        h.Transport.Connect = count => count == 1 ? h.Connection : replacement;
        h.Delay.OnDelay = _ => h.Stop.Cancel();
        await h.Run();
        h.Connection.Disposals.ShouldBe(1);
        h.Connection.Heartbeats.ShouldBe(0);
        h.Delay.Durations.ShouldHaveSingleItem().ShouldBe(TimeSpan.FromSeconds(1));
        h.States.ShouldContain(ClientState.Reconnecting);
    }

    [Fact]
    public async Task Run_SuccessAfterFailures_ResetsRetryBackoff()
    {
        var h = new Harness();
        h.Transport.Connect = count => count <= 2
            ? throw new SatelliteConnectionException(ConnectionFailure.Transient) : h.Connection;
        h.Connection.OnHeartbeat = () => throw new SatelliteConnectionException(ConnectionFailure.Transient);
        h.Delay.OnDelay = count => { if (count == 4) h.Stop.Cancel(); };
        await h.Run();
        h.Delay.Durations.ShouldBe(new[] { 1, 2, 30, 1 }.Select(seconds => TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public async Task Run_InvalidConfiguration_FailsBeforeCredentialsOrNetwork()
    {
        var h = new Harness();
        h.Options = new SatelliteClientOptions { GatewayEndpoint = "http://evil.example" };
        await Should.ThrowAsync<ArgumentException>(() => h.Run());
        h.Store.Loads.ShouldBe(0);
        h.Transport.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Run_AlreadyCancelled_DoesNotLoadCredentialsOrConnect()
    {
        var h = new Harness();
        h.Stop.Cancel();
        await h.Run();
        h.Store.Loads.ShouldBe(0);
        h.Transport.Requests.ShouldBeEmpty();
        h.States.Last().ShouldBe(ClientState.Offline);
    }

    [Fact]
    public async Task Run_CancellationDuringConnect_PropagatesTokenAndDoesNotRetry()
    {
        var h = new Harness();
        h.Transport.ConnectHandlerAsync = async token =>
        {
            token.ShouldBe(h.Stop.Token);
            h.Stop.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return h.Connection;
        };
        await h.Run();
        h.Transport.Requests.Count.ShouldBe(1);
        h.Delay.Durations.ShouldBeEmpty();
        h.States.Last().ShouldBe(ClientState.Offline);
    }

    [Fact]
    public async Task OpenTunnelSignIn_ExplicitAction_OpensOnlyConfiguredOriginWithoutSecrets()
    {
        var h = new Harness();
        await h.CreateClient().OpenTunnelSignInAsync(h.Stop.Token);
        h.Browser.Opened.ShouldBe(new[] { new Uri("https://demo.devtunnels.ms") });
        h.Store.Loads.ShouldBe(0);
        h.Transport.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task SaveTunnelToken_ExplicitSecretInput_PersistsThroughInjectedProtectedStore()
    {
        var h = new Harness();
        await h.CreateClient().SaveTunnelTokenAsync("new-secret", h.Stop.Token);
        h.Store.SavedTokens.ShouldBe(new[] { "new-secret" });
        h.Store.SaveToken.ShouldBe(h.Stop.Token);
        h.Browser.Opened.ShouldBeEmpty();
        h.Transport.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("bad\r\nheader")]
    public async Task SaveTunnelToken_InvalidSecret_RejectsWithoutPersisting(string token)
    {
        var h = new Harness();
        await Should.ThrowAsync<ArgumentException>(() => h.CreateClient().SaveTunnelTokenAsync(token, h.Stop.Token));
        h.Store.SavedTokens.ShouldBeEmpty();
    }

    [Fact]
    public async Task OpenTunnelSignIn_UnconfiguredOrigin_RejectsWithoutLaunching()
    {
        var h = new Harness();
        h.Options = new SatelliteClientOptions { GatewayEndpoint = "https://gateway.example" };
        await Should.ThrowAsync<InvalidOperationException>(() => h.CreateClient().OpenTunnelSignInAsync(h.Stop.Token));
        h.Browser.Opened.ShouldBeEmpty();
    }

    private sealed class Harness
    {
        public SatelliteClientOptions Options { get; set; } = new()
        {
            GatewayEndpoint = "https://demo.devtunnels.ms",
            TunnelBrowserOrigin = "https://demo.devtunnels.ms"
        };
        public FakeStore Store { get; } = new();
        public FakeTransport Transport { get; } = new();
        public FakeConnection Connection { get; } = new();
        public FakeBrowser Browser { get; } = new();
        public FakeDelay Delay { get; } = new();
        public ManualTimeProvider Clock { get; } = new(DateTimeOffset.UnixEpoch);
        public CancellationTokenSource Stop { get; } = new();
        public List<ClientState> States { get; } = [];

        public Harness() => Transport.Connect = _ => Connection;
        public SatelliteClient CreateClient() => new(Options, Store, Transport, Browser, Clock, Delay, States.Add);
        public Task Run() => CreateClient().RunAsync(Stop.Token);
    }

    private sealed class FakeStore : ICredentialStore
    {
        public ClientCredentials Credentials { get; set; } = new() { GatewayApiKey = "gateway-secret", TunnelAccessToken = "tunnel-secret" };
        public int Loads { get; private set; }
        public List<string> SavedTokens { get; } = [];
        public CancellationToken SaveToken { get; private set; }
        public Task<ClientCredentials> LoadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Loads++;
            return Task.FromResult(Credentials);
        }
        public Task SaveTunnelTokenAsync(string token, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SavedTokens.Add(token);
            SaveToken = cancellationToken;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTransport : ISatelliteTransport
    {
        public List<ConnectionRequest> Requests { get; } = [];
        public Func<int, ISatelliteConnection> Connect { get; set; } = _ => throw new InvalidOperationException("Test must configure transport");
        public Func<CancellationToken, Task<ISatelliteConnection>>? ConnectHandlerAsync { get; set; }
        public Task<ISatelliteConnection> ConnectAsync(ConnectionRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return ConnectHandlerAsync is { } connect ? connect(cancellationToken) : Task.FromResult(Connect(Requests.Count));
        }
    }

    private sealed class FakeConnection : ISatelliteConnection
    {
        public int Heartbeats { get; private set; }
        public int Disposals { get; private set; }
        public Action? OnHeartbeat { get; set; }
        public TaskCompletionSource<ConnectionFailure> ClosedSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ConnectionFailure> Closed => ClosedSignal.Task;
        public Task HeartbeatAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Heartbeats++;
            OnHeartbeat?.Invoke();
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }

    private sealed class FakeBrowser : IBrowserLauncher
    {
        public List<Uri> Opened { get; } = [];
        public Task OpenAsync(Uri origin, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Opened.Add(origin);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeDelay : IClientDelay
    {
        public List<TimeSpan> Durations { get; } = [];
        public List<TimeProvider> Clocks { get; } = [];
        public Action<int>? OnDelay { get; set; }
        public Task DelayAsync(TimeSpan duration, TimeProvider clock, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Durations.Add(duration);
            Clocks.Add(clock);
            OnDelay?.Invoke(Durations.Count);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
