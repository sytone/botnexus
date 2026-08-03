using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Channels.Startup;

/// <summary>
/// Wraps a channel-owned <see cref="IHostedService"/> so a start or run fault degrades that
/// channel instead of terminating the gateway host (#2731).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not <c>BackgroundServiceExceptionBehavior.Ignore</c>.</b> The obvious one-line fix is
/// to set <c>HostOptions.BackgroundServiceExceptionBehavior = Ignore</c> globally. That was
/// rejected deliberately. It is indiscriminate: it would also swallow a fault in the cron
/// scheduler, the session store warmup, the config hydration service, or the memory indexer -
/// services whose failure genuinely means the process is not fit to serve and SHOULD stop the
/// host loudly rather than limp on in an unobservable half-state. The default
/// <c>StopHost</c> therefore stays in force for every background service in the process.
/// </para>
/// <para>
/// The fault is instead isolated at the exact boundary where the blast radius is known to be one
/// channel: services explicitly registered through
/// <see cref="ChannelHostedServiceRegistrationExtensions.AddChannelHostedService{TService}"/>.
/// A channel is, by construction, one optional ingress surface; a missing Telegram BotToken or an
/// unset Service Bus connection string must cost that channel and nothing else. On 2026-08-01 it
/// cost the whole process: five seconds after the gateway logged
/// "Gateway started DEGRADED: 3 of 5 channel adapter(s) running", six FTL lines naming
/// <c>BackgroundServiceExceptionBehavior is configured to StopHost</c> took cron, the portal,
/// SignalR and every agent surface down with it.
/// </para>
/// <para>
/// Containment without observability would just move the outage somewhere quieter, so every
/// swallowed fault is recorded on the shared <see cref="ChannelStartupReport"/> singleton - the
/// same surface <c>GET /api/channels/health</c> already reads for adapter start outcomes (#2447).
/// </para>
/// </remarks>
public sealed class ChannelFaultBarrierHostedService : IHostedService
{
    private readonly IHostedService _inner;
    private readonly string _channelType;
    private readonly ChannelStartupReport _report;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChannelFaultBarrierHostedService"/> class.
    /// </summary>
    /// <param name="inner">The channel-owned hosted service being guarded.</param>
    /// <param name="channelType">Channel identity used when reporting a contained fault.</param>
    /// <param name="report">Shared startup/health report the fault is published to.</param>
    /// <param name="logger">Logger for the containment diagnostic.</param>
    public ChannelFaultBarrierHostedService(
        IHostedService inner,
        string channelType,
        ChannelStartupReport report,
        ILogger logger)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _channelType = channelType ?? throw new ArgumentNullException(nameof(channelType));
        _report = report ?? throw new ArgumentNullException(nameof(report));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Gets the guarded service, for tests and diagnostics.</summary>
    public IHostedService Inner => _inner;

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _inner.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown, not a channel fault. Let the host observe it normally.
            throw;
        }
        catch (Exception ex)
        {
            Contain(ex);
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _inner.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A channel that failed to start commonly fails to stop cleanly too. Neither is a
            // reason to fault shutdown for every other surface.
            _logger.LogWarning(
                ex,
                "Channel '{ChannelType}' background service '{Service}' faulted during stop; contained.",
                _channelType,
                _inner.GetType().Name);
        }
    }

    private void Contain(Exception ex)
    {
        var serviceName = _inner.GetType().Name;

        _logger.LogError(
            ex,
            "Channel '{ChannelType}' background service '{Service}' failed to start. The fault is contained to this channel: the channel is degraded and the gateway host stays up. See GET /api/channels/health.",
            _channelType,
            serviceName);

        _report.RecordServiceFault(new ChannelServiceFault(_channelType, serviceName, ex));
    }
}

/// <summary>
/// Registration helpers that place channel-owned background services behind the #2731 fault
/// barrier.
/// </summary>
public static class ChannelHostedServiceRegistrationExtensions
{
    /// <summary>
    /// Registers <typeparamref name="TService"/> as a hosted service whose start/stop faults
    /// degrade the named channel instead of stopping the host (#2731).
    /// </summary>
    /// <typeparam name="TService">The channel-owned hosted service implementation.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="channelType">Channel identity reported when a fault is contained.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <remarks>
    /// Use this ONLY for services whose failure is confined to one optional ingress channel.
    /// Registering a genuinely load-bearing service here would hide a fault that should stop the
    /// host - use <c>AddHostedService</c> for those so the default <c>StopHost</c> behaviour
    /// still applies.
    /// </remarks>
    public static IServiceCollection AddChannelHostedService<TService>(
        this IServiceCollection services,
        string channelType)
        where TService : class, IHostedService
    {
        ArgumentNullException.ThrowIfNull(services);

        AddChannelHostedService(services, typeof(TService), channelType);
        return services;
    }

    /// <summary>
    /// Non-generic form of <see cref="AddChannelHostedService{TService}"/> for composition roots
    /// that only know the implementation type at runtime - notably the extension loader, which
    /// discovers channel-extension hosted services by reflection (#2731).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="implementationType">A concrete <see cref="IHostedService"/> implementation.</param>
    /// <param name="channelType">Channel identity reported when a fault is contained.</param>
    /// <returns>
    /// The descriptors that were added, so a caller which later prunes an un-activatable
    /// extension service can remove exactly what it registered.
    /// </returns>
    public static IReadOnlyList<ServiceDescriptor> AddChannelHostedService(
        this IServiceCollection services,
        Type implementationType,
        string channelType)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(implementationType);
        ArgumentException.ThrowIfNullOrWhiteSpace(channelType);

        if (!typeof(IHostedService).IsAssignableFrom(implementationType))
        {
            throw new ArgumentException(
                $"Type '{implementationType.FullName}' does not implement IHostedService.",
                nameof(implementationType));
        }

        services.TryAddSingleton<ChannelStartupReport>();

        var added = new List<ServiceDescriptor>();

        if (!services.Any(descriptor => descriptor.ServiceType == implementationType))
        {
            var concrete = ServiceDescriptor.Singleton(implementationType, implementationType);
            services.Add(concrete);
            added.Add(concrete);
        }

        var barrier = ServiceDescriptor.Singleton<IHostedService>(sp => new ChannelFaultBarrierHostedService(
            (IHostedService)sp.GetRequiredService(implementationType),
            channelType,
            sp.GetRequiredService<ChannelStartupReport>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<ChannelFaultBarrierHostedService>()));
        services.Add(barrier);
        added.Add(barrier);

        return added;
    }
}
