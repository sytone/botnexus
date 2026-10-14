using System.Collections.ObjectModel;

namespace BotNexus.Satellite.Client.Core;

/// <summary>Owns one portable, cancellation-aware session without platform or browser authentication effects.</summary>
public sealed class SatelliteClient
{
    private readonly SatelliteClientOptions _options;
    private readonly ICredentialStore _credentialStore;
    private readonly ISatelliteTransport _transport;
    private readonly IBrowserLauncher _browser;
    private readonly TimeProvider _clock;
    private readonly IClientDelay _delay;
    private readonly Action<ClientState> _stateChanged;
    private readonly RetryPolicy _retry = new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));
    private int _running;

    /// <summary>Injects all platform effects so tests require neither a gateway nor a Windows desktop.</summary>
    public SatelliteClient(
        SatelliteClientOptions options,
        ICredentialStore credentialStore,
        ISatelliteTransport transport,
        IBrowserLauncher browser,
        TimeProvider clock,
        IClientDelay delay,
        Action<ClientState> stateChanged)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(credentialStore);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(browser);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(delay);
        ArgumentNullException.ThrowIfNull(stateChanged);
        _options = options;
        _credentialStore = credentialStore;
        _transport = transport;
        _browser = browser;
        _clock = clock;
        _delay = delay;
        _stateChanged = stateChanged;
    }

    /// <summary>Maintains the session until cancellation or needs-auth; cancellation ends offline and disposes transport.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            throw new InvalidOperationException("A satellite session is already running.");
        }

        var needsAuthentication = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hubUri = _options.GetHubUri();
            var credentials = await _credentialStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var useTunnel = _options.TunnelBrowserOrigin is not null;
            if (!IsHeaderValue(credentials.GatewayApiKey) || (useTunnel && !IsHeaderValue(credentials.TunnelAccessToken)))
            {
                needsAuthentication = true;
                _stateChanged(ClientState.NeedsAuthentication);
                return;
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["X-Api-Key"] = credentials.GatewayApiKey ?? throw new InvalidOperationException("Gateway credential is required.")
            };
            if (useTunnel)
            {
                headers["X-Tunnel-Authorization"] = "tunnel " + credentials.TunnelAccessToken;
            }

            var request = new ConnectionRequest
            {
                HubUri = hubUri,
                Headers = new ReadOnlyDictionary<string, string>(headers)
            };
            var attempt = 0;
            _stateChanged(ClientState.Connecting);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ConnectionFailure failure;
                ISatelliteConnection? connection = null;
                try
                {
                    connection = await _transport.ConnectAsync(request, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    attempt = 0;
                    _stateChanged(ClientState.Online);
                    failure = await MaintainConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (SatelliteConnectionException ex)
                {
                    failure = ex.Failure;
                }
                catch (Exception)
                {
                    // Unclassified transport faults are retriable; never surface their potentially secret-bearing text.
                    failure = ConnectionFailure.Transient;
                }
                finally
                {
                    if (connection is not null)
                    {
                        // The runner is the sole owner. Dispose before any retry, including cancellation and auth failures.
                        await connection.DisposeAsync().ConfigureAwait(false);
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (failure is ConnectionFailure.AuthenticationRequired or ConnectionFailure.AccessDenied)
                {
                    needsAuthentication = true;
                    _stateChanged(ClientState.NeedsAuthentication);
                    return;
                }

                _stateChanged(ClientState.Reconnecting);
                await _delay.DelayAsync(_retry.GetDelay(attempt), _clock, cancellationToken).ConfigureAwait(false);
                if (attempt < int.MaxValue)
                {
                    attempt++;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // User cancellation is normal session termination, not an authentication or retry failure.
        }
        finally
        {
            try
            {
                if (!needsAuthentication || cancellationToken.IsCancellationRequested)
                {
                    _stateChanged(ClientState.Offline);
                }
            }
            finally
            {
                Volatile.Write(ref _running, 0);
            }
        }
    }

    /// <summary>Explicit menu action opens configured HTTPS tunnel origin without implying .NET authentication succeeded.</summary>
    public Task OpenTunnelSignInAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = _options.GetHubUri();
        if (_options.TunnelBrowserOrigin is not { } origin)
        {
            throw new InvalidOperationException("No tunnel browser origin is configured.");
        }

        return _browser.OpenAsync(new Uri(origin, UriKind.Absolute), cancellationToken);
    }

    /// <summary>Validates user secret input and saves it through the protected store; user starts/restarts connection separately.</summary>
    public Task SaveTunnelTokenAsync(string token, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsHeaderValue(token))
        {
            throw new ArgumentException("Tunnel token must be nonempty and contain no whitespace or control characters.", nameof(token));
        }

        return _credentialStore.SaveTunnelTokenAsync(token, cancellationToken);
    }

    private async Task<ConnectionFailure> MaintainConnectionAsync(ISatelliteConnection connection, CancellationToken cancellationToken)
    {
        var closed = connection.Closed;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (closed.IsCompleted)
            {
                return await closed.ConfigureAwait(false);
            }

            using (var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                var heartbeatDelay = _delay.DelayAsync(_options.HeartbeatInterval, _clock, delayCancellation.Token);
                try
                {
                    await Task.WhenAny(closed, heartbeatDelay).WaitAsync(cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (closed.IsCompleted)
                    {
                        return await closed.ConfigureAwait(false);
                    }

                    await heartbeatDelay.ConfigureAwait(false);
                }
                finally
                {
                    // When closure wins, cancel and observe the losing delay before disposing its token source.
                    delayCancellation.Cancel();
                    await ObserveCompletionAsync(heartbeatDelay).ConfigureAwait(false);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (closed.IsCompleted)
            {
                return await closed.ConfigureAwait(false);
            }

            using var heartbeatCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var heartbeat = connection.HeartbeatAsync(heartbeatCancellation.Token);
            try
            {
                // Closure can arrive during a slow heartbeat as well as during its scheduled delay.
                await Task.WhenAny(closed, heartbeat).WaitAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (closed.IsCompleted)
                {
                    return await closed.ConfigureAwait(false);
                }

                await heartbeat.ConfigureAwait(false);
            }
            finally
            {
                heartbeatCancellation.Cancel();
                await ObserveCompletionAsync(heartbeat).ConfigureAwait(false);
            }
        }
    }

    private static bool IsHeaderValue(string? value) =>
        !string.IsNullOrEmpty(value) && !value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));

    private static async Task ObserveCompletionAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The winning task determines the session outcome; losing work must still be fully observed.
        }
    }
}
