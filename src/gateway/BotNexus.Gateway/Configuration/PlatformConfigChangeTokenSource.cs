using System.IO.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Signals <see cref="IOptionsMonitor{TOptions}"/> to reload when platform config changes.
/// </summary>
public sealed class PlatformConfigChangeTokenSource : IOptionsChangeTokenSource<PlatformConfig>, IDisposable
{
    private readonly Lock _sync = new();
    private readonly Action<PlatformConfig> _onConfigChanged;
    private CancellationTokenSource _cts = new();
    private bool _disposed;

    public PlatformConfigChangeTokenSource(string configPath, IFileSystem fileSystem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        ArgumentNullException.ThrowIfNull(fileSystem);

        Name = Options.DefaultName;
        _onConfigChanged = _ => SignalChanged();
        PlatformConfigLoader.ConfigChanged += _onConfigChanged;
    }

    public string Name { get; }

    public IChangeToken GetChangeToken()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            return new CancellationChangeToken(_cts.Token);
        }
    }

    public void Dispose()
    {
        CancellationTokenSource ctsToDispose;
        lock (_sync)
        {
            if (_disposed)
                return;

            _disposed = true;
            ctsToDispose = _cts;
        }

        PlatformConfigLoader.ConfigChanged -= _onConfigChanged;
        ctsToDispose.Dispose();
    }

    private void SignalChanged()
    {
        CancellationTokenSource oldCts;
        lock (_sync)
        {
            if (_disposed)
                return;

            oldCts = _cts;
            _cts = new CancellationTokenSource();
        }

        oldCts.Cancel();
        oldCts.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(PlatformConfigChangeTokenSource));
    }
}
