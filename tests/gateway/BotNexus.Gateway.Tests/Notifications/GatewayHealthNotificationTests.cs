using BotNexus.Gateway.Diagnostics;
using BotNexus.Gateway.Abstractions.Notifications;
using BotNexus.Gateway.Notifications;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Tests.Notifications;

/// <summary>
/// Pins gateway-health notifications raised by the liveness watchdog.
/// </summary>
/// <remarks>
/// The load-bearing clause is that notifications follow the TRANSITION, not the state. The watchdog
/// re-checks on an interval, so raising from the condition would file the same alarm every few
/// minutes for as long as the gateway stayed unwell - which is how an operator learns to ignore the
/// one notification that means something is genuinely broken.
/// </remarks>
public sealed class GatewayHealthNotificationTests
{
    private static LivenessWatchdogService Service(
        RecordingPublisher publisher,
        TimeSpan inactivity,
        bool probeResponsive) =>
        new(new StubTracker(inactivity),
            new StubProbe(probeResponsive),
            Options.Create(new LivenessWatchdogOptions()),
            NullLogger<LivenessWatchdogService>.Instance,
            publisher);

    // An unresponsive scheduler with a long silence is the critical case.
    private static readonly TimeSpan LongSilence = TimeSpan.FromHours(2);

    [Fact]
    public async Task An_unresponsive_gateway_raises_one_error_notification()
    {
        var publisher = new RecordingPublisher();
        var service = Service(publisher, LongSilence, probeResponsive: false);

        await service.CheckLivenessAsync(CancellationToken.None);

        var raised = Assert.Single(publisher.Published);
        Assert.Equal(NotificationKind.GatewayHealth, raised.Kind);
        Assert.Equal(NotificationSeverity.Error, raised.Severity);
        Assert.Contains("not responding", raised.Title);
    }

    // The clause that stops the feature becoming noise.
    [Fact]
    public async Task Staying_unresponsive_does_not_raise_it_again()
    {
        var publisher = new RecordingPublisher();
        var service = Service(publisher, LongSilence, probeResponsive: false);

        await service.CheckLivenessAsync(CancellationToken.None);
        await service.CheckLivenessAsync(CancellationToken.None);
        await service.CheckLivenessAsync(CancellationToken.None);

        Assert.Single(publisher.Published);
    }

    [Fact]
    public async Task Recovering_after_a_reported_outage_raises_an_info_notification()
    {
        var publisher = new RecordingPublisher();
        var tracker = new StubTracker(LongSilence);
        var probe = new StubProbe(false);
        var service = new LivenessWatchdogService(
            tracker,
            probe,
            Options.Create(new LivenessWatchdogOptions()),
            NullLogger<LivenessWatchdogService>.Instance,
            publisher);

        await service.CheckLivenessAsync(CancellationToken.None);

        // Activity resumes and the scheduler answers again.
        tracker.Elapsed = TimeSpan.Zero;
        probe.Responsive = true;
        await service.CheckLivenessAsync(CancellationToken.None);

        Assert.Equal(2, publisher.Published.Count);
        Assert.Equal(NotificationSeverity.Info, publisher.Published[1].Severity);
        Assert.Contains("recovered", publisher.Published[1].Title, StringComparison.OrdinalIgnoreCase);
    }

    // Recovery from something nobody was told about is not news.
    [Fact]
    public async Task A_healthy_gateway_raises_nothing()
    {
        var publisher = new RecordingPublisher();
        var service = Service(publisher, TimeSpan.Zero, probeResponsive: true);

        await service.CheckLivenessAsync(CancellationToken.None);
        await service.CheckLivenessAsync(CancellationToken.None);

        Assert.Empty(publisher.Published);
    }

    // A watchdog must never fail because notifications are unavailable: reporting a problem is not
    // allowed to become one.
    [Fact]
    public async Task A_publisher_that_throws_does_not_break_the_watchdog()
    {
        var service = new LivenessWatchdogService(
            new StubTracker(LongSilence),
            new StubProbe(false),
            Options.Create(new LivenessWatchdogOptions()),
            NullLogger<LivenessWatchdogService>.Instance,
            new ThrowingPublisher());

        var thrown = await Record.ExceptionAsync(() => service.CheckLivenessAsync(CancellationToken.None));

        Assert.Null(thrown);
    }

    private sealed class RecordingPublisher : INotificationPublisher
    {
        public List<Notification> Published { get; } = [];

        public Task PublishAsync(Notification notification, CancellationToken ct = default)
        {
            Published.Add(notification);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingPublisher : INotificationPublisher
    {
        public Task PublishAsync(Notification notification, CancellationToken ct = default) =>
            throw new InvalidOperationException("notification store is unavailable");
    }

    private sealed class StubTracker(TimeSpan elapsed) : IActivityTracker
    {
        public TimeSpan Elapsed { get; set; } = elapsed;

        public void RecordActivity() => Elapsed = TimeSpan.Zero;

        public TimeSpan TimeSinceLastActivity => Elapsed;

        public DateTimeOffset LastActivityUtc => DateTimeOffset.UtcNow - Elapsed;
    }

    private sealed class StubProbe(bool responsive) : IThreadPoolProbe
    {
        public bool Responsive { get; set; } = responsive;

        public Task<bool> IsResponsiveAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(Responsive);
    }
}
