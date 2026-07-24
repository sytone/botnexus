using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;

namespace BotNexus.Gateway.Channels;

/// <summary>
/// Provides runtime-reloadable options for channel extensions that are loaded <b>after</b> the host
/// has already run its <c>IOptions&lt;T&gt;</c> binding pass.
/// </summary>
/// <remarks>
/// <para>
/// Dynamically-loaded channel extensions (Service Bus, Telegram, Agent 365) are registered by the
/// <c>AssemblyLoadContextExtensionLoader</c> via a bare <c>AddSingleton&lt;IChannelAdapter&gt;</c>.
/// That path never invokes the extension's <c>AddBotNexus*Channel</c> method, so
/// <c>services.Configure&lt;T&gt;</c> is never called and <c>IOptions&lt;T&gt;</c> resolves to an
/// empty instance. Each adapter therefore self-binds from <see cref="IConfiguration"/> under its
/// <c>channels:&lt;channelType&gt;</c> section. Historically that bind ran exactly once in the
/// adapter constructor, freezing the options: a <c>config.json</c> edit at runtime was not picked
/// up until the next gateway restart.
/// </para>
/// <para>
/// The host already adds <c>config.json</c> to the configuration pipeline with
/// <c>reloadOnChange: true</c>, so <see cref="IConfiguration.GetReloadToken"/> fires whenever the
/// file changes. This holder subscribes to that token and re-runs the supplied
/// <paramref name="resolve"/> delegate on every change, so <see cref="Current"/> always reflects the
/// latest configuration without a restart. When <see cref="IConfiguration"/> is <c>null</c> (unit
/// tests, or a host that early-bound the options) the value is resolved once and never changes,
/// preserving the original cold-boot fallback behaviour exactly.
/// </para>
/// </remarks>
/// <typeparam name="TOptions">The strongly-typed options class for the channel.</typeparam>
public sealed class LateBoundChannelOptions<TOptions>
    where TOptions : class
{
    private readonly Func<TOptions> _resolve;
    // Held so the reload subscription's lifetime is explicit and the registration is not flagged as
    // an unused disposable. Adapters are process-lifetime singletons, so it is never disposed.
    private readonly IDisposable? _reloadRegistration;
    private volatile TOptions _current;

    /// <summary>
    /// Creates a late-bound options holder. The <paramref name="resolve"/> delegate is the adapter's
    /// existing one-shot resolver (IOptions-first, IConfiguration fallback); it is invoked once
    /// immediately and again on every subsequent configuration reload.
    /// </summary>
    /// <param name="resolve">
    /// Resolves the effective options. Must be safe to call repeatedly; typically the adapter's
    /// static <c>ResolveOptions(IOptions&lt;T&gt;, IConfiguration?)</c> helper.
    /// </param>
    /// <param name="configuration">
    /// The host configuration whose <see cref="IConfiguration.GetReloadToken"/> signals
    /// <c>config.json</c> changes. When <c>null</c>, options are resolved once and never reload.
    /// </param>
    public LateBoundChannelOptions(Func<TOptions> resolve, IConfiguration? configuration)
    {
        ArgumentNullException.ThrowIfNull(resolve);

        _resolve = resolve;
        _current = _resolve();

        if (configuration is not null)
        {
            // ChangeToken.OnChange re-registers itself after each fire (reload tokens are single-use),
            // so this keeps tracking config.json edits for the life of the adapter.
            _reloadRegistration = ChangeToken.OnChange(
                configuration.GetReloadToken,
                () => _current = _resolve());
        }
    }

    /// <summary>
    /// The most recently resolved options. Reflects the latest <c>config.json</c> contents when the
    /// holder was constructed with a live <see cref="IConfiguration"/>; adapters should read this at
    /// point of use rather than caching a snapshot so runtime edits take effect.
    /// </summary>
    public TOptions Current => _current;
}
