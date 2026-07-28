using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BotNexus.Integration.ConfigDiskE2E.Tests;

/// <summary>
/// A live runtime consumer of the physical config file: the real JSON configuration provider
/// (with <c>reloadOnChange</c>) feeding a real <see cref="IOptionsMonitor{TOptions}"/> of
/// <see cref="PlatformConfig"/>.
/// </summary>
/// <remarks>
/// Exists so a test can assert the part of the pipeline that mock-filesystem tests structurally
/// cannot reach: that a production write to disk actually propagates to the object graph the
/// gateway's services read at runtime, and that the change token fires. Reload is inherently
/// asynchronous (the provider debounces filesystem notifications), so
/// <see cref="WaitForReloadAsync"/> gives tests a deterministic wait rather than a sleep.
/// </remarks>
public sealed class RuntimeConsumer : IDisposable
{
    private readonly IConfiguration _configuration;
    private readonly ServiceProvider _provider;
    private readonly IDisposable? _changeSubscription;
    private readonly SemaphoreSlim _reloadSignal = new(0);

    internal RuntimeConsumer(IConfiguration configuration, ServiceProvider provider)
    {
        _configuration = configuration;
        _provider = provider;
        Monitor = provider.GetRequiredService<IOptionsMonitor<PlatformConfig>>();

        // Touch CurrentValue once so the options instance is materialised and cached before the
        // test mutates the file; otherwise the first read after the write would succeed trivially
        // without proving that a reload occurred. A config that already fails validation throws
        // here - that is a legitimate state for a test to set up deliberately, so the warm-up is
        // best-effort and the test's own assertion on CurrentValue is what reports the failure.
        try
        {
            _ = Monitor.CurrentValue;
        }
        catch (OptionsValidationException)
        {
        }

        _changeSubscription = Monitor.OnChange(_ =>
        {
            ReloadCount++;
            _reloadSignal.Release();
        });
    }

    /// <summary>The runtime options view services resolve.</summary>
    public IOptionsMonitor<PlatformConfig> Monitor { get; }

    /// <summary>Number of reload acknowledgements observed since construction.</summary>
    public int ReloadCount { get; private set; }

    /// <summary>Reads a raw configuration key (e.g. <c>gateway:listenUrl</c>) from the provider.</summary>
    public string? ReadKey(string key) => _configuration[key];

    /// <summary>
    /// Waits until the configuration provider acknowledges at least one reload, or the timeout
    /// elapses. Returns <see langword="true"/> when a reload was observed.
    /// </summary>
    public async Task<bool> WaitForReloadAsync(TimeSpan? timeout = null)
        => await _reloadSignal.WaitAsync(timeout ?? TimeSpan.FromSeconds(15));

    /// <summary>
    /// Forces the configuration provider to re-read the file and returns the freshly bound
    /// options. Used when a test cares about the <em>content</em> that reaches the consumer
    /// rather than about watcher timing (which is asserted separately and can be debounced by
    /// the OS).
    /// </summary>
    public PlatformConfig ReloadNow()
    {
        ((IConfigurationRoot)_configuration).Reload();
        return Monitor.CurrentValue;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _changeSubscription?.Dispose();
        _reloadSignal.Dispose();
        _provider.Dispose();
        (_configuration as IDisposable)?.Dispose();
    }
}
