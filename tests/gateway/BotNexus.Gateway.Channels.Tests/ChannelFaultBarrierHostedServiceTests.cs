using BotNexus.Gateway.Channels.Startup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Channels.Tests;

/// <summary>
/// #2731: a channel-adapter/channel-service start fault must degrade one channel, never
/// terminate the gateway host - while a genuinely load-bearing background service fault must
/// still take the host down.
/// </summary>
public sealed class ChannelFaultBarrierHostedServiceTests
{
    private sealed class ThrowingHostedService(Exception error) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.FromException(error);
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class HealthyHostedService : IHostedService
    {
        public bool Started { get; private set; }
        public Task StartAsync(CancellationToken cancellationToken)
        {
            Started = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>
    /// Acceptance criterion 1 / issue clause 4. One channel service throws on start; the host
    /// still reaches the started state and the other channel service is running.
    /// </summary>
    [Fact]
    public async Task HostRemainsStartedWhenOneChannelServiceThrowsOnStart()
    {
        var healthy = new HealthyHostedService();
        var report = new ChannelStartupReport();

        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton(report);
                services.AddSingleton<IHostedService>(_ => new ChannelFaultBarrierHostedService(
                    new ThrowingHostedService(
                        new InvalidOperationException("Telegram bot 'default' requires BotToken.")),
                    "telegram",
                    report,
                    NullLogger.Instance));
                services.AddSingleton<IHostedService>(_ => new ChannelFaultBarrierHostedService(
                    healthy, "signalr", report, NullLogger.Instance));
            })
            .Build();

        await host.StartAsync();

        Assert.True(healthy.Started);

        await host.StopAsync();
    }

    /// <summary>
    /// Acceptance criterion 3 / issue clause 5. The contained fault is retrievable by channel
    /// identity and message from the existing status surface, not merely logged once.
    /// </summary>
    [Fact]
    public async Task ContainedFaultIsRetrievableFromStartupReport()
    {
        var report = new ChannelStartupReport();
        var barrier = new ChannelFaultBarrierHostedService(
            new ThrowingHostedService(
                new InvalidOperationException("Telegram bot 'default' requires BotToken.")),
            "telegram",
            report,
            NullLogger.Instance);

        await barrier.StartAsync(CancellationToken.None);

        var fault = Assert.Single(report.ServiceFaults);
        Assert.Equal("telegram", fault.ChannelType);
        Assert.Equal("ThrowingHostedService", fault.ServiceName);
        Assert.Contains("BotToken", fault.Error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Scope clause 3 - the load-bearing discrimination test. An UNGUARDED background service
    /// (anything registered with plain <c>AddHostedService</c>: cron, config hydration, the
    /// session store) that throws on start MUST still fail host start. This is what proves the
    /// fix is an isolation, not a blanket suppression: if someone "fixed" #2731 by setting
    /// <c>BackgroundServiceExceptionBehavior = Ignore</c> globally, or by widening the barrier to
    /// all hosted services, this test goes red.
    /// </summary>
    [Fact]
    public async Task UnguardedBackgroundServiceFaultStillFailsHostStart()
    {
        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IHostedService>(_ => new ThrowingHostedService(
                    new InvalidOperationException("cron scheduler store is corrupt")));
            })
            .Build();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());
        Assert.Contains("cron scheduler", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The barrier must not swallow cooperative shutdown cancellation - that is host lifecycle,
    /// not a channel fault, and hiding it would corrupt shutdown ordering.
    /// </summary>
    [Fact]
    public async Task CancellationDuringStartIsNotContained()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var report = new ChannelStartupReport();
        var barrier = new ChannelFaultBarrierHostedService(
            new ThrowingHostedService(new OperationCanceledException(cts.Token)),
            "telegram",
            report,
            NullLogger.Instance);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => barrier.StartAsync(cts.Token));
        Assert.Empty(report.ServiceFaults);
    }

    /// <summary>
    /// <c>AddChannelHostedService</c> must place the service behind the barrier, so a channel
    /// registration cannot accidentally get default StopHost semantics.
    /// </summary>
    [Fact]
    public void AddChannelHostedServiceWrapsTheServiceInTheBarrier()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddChannelHostedService<HealthyHostedService>("telegram");

        using var provider = services.BuildServiceProvider();
        var hosted = Assert.Single(provider.GetServices<IHostedService>());
        var barrier = Assert.IsType<ChannelFaultBarrierHostedService>(hosted);
        Assert.IsType<HealthyHostedService>(barrier.Inner);
    }
}
