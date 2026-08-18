using BotNexus.Gateway.Abstractions.Providers;
using BotNexus.Gateway.Contracts.Events;
using BotNexus.Gateway.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Covers the provider-health signalling introduced by #3281.
///
/// <para>
/// The defect these tests pin is an absence: <c>WorldEventTypes.HealthDegraded</c> was a declared
/// constant with no publisher anywhere in the codebase, so a seven-hour provider outage produced 391
/// log lines and zero events. Assertions here therefore check that a specific event type reaches the
/// bus with specific payload keys - asserting merely that "something was published" would pass on a
/// implementation that published the wrong event.
/// </para>
/// </summary>
public sealed class WorldEventProviderHealthObserverTests
{
    private const string Provider = "github-copilot";

    /// <summary>
    /// Records every published event so assertions can inspect type and payload rather than just
    /// counting calls.
    /// </summary>
    private sealed class RecordingEventBus : IWorldEventBus
    {
        public List<WorldEvent> Published { get; } = [];

        public Task<int> PublishAsync(WorldEvent worldEvent, CancellationToken cancellationToken = default)
        {
            Published.Add(worldEvent);
            return Task.FromResult(1);
        }

        public void SetSubscriptions(string agentId, IReadOnlyList<EventSubscription> subscriptions) { }
        public IReadOnlyList<EventSubscription> GetSubscriptions(string agentId) => [];
        public IReadOnlyList<string> GetSubscribers(string eventType) => [];
    }

    /// <summary>
    /// A controllable clock. Written locally rather than taking a dependency on
    /// Microsoft.Extensions.TimeProvider.Testing, which this repository does not reference - adding a
    /// package to a shared props file for one test file is a larger change than the fake it replaces.
    /// Cooldown behaviour must be asserted without sleeping, or the test becomes a timing flake.
    /// </summary>
    private sealed class TestTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;

        public TestTimeProvider(DateTimeOffset start) => _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }

    private static (WorldEventProviderHealthObserver Observer, RecordingEventBus Bus, TestTimeProvider Time) CreateObserver(
        int threshold = 3,
        TimeSpan? cooldown = null)
    {
        var bus = new RecordingEventBus();
        var time = new TestTimeProvider(DateTimeOffset.Parse("2026-08-17T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var observer = new WorldEventProviderHealthObserver(
            bus,
            NullLogger<WorldEventProviderHealthObserver>.Instance,
            time,
            threshold,
            cooldown ?? TimeSpan.FromMinutes(15));
        return (observer, bus, time);
    }

    private static ProviderCredentialOutcome Failure(int? status = 503) =>
        ProviderCredentialOutcome.Failed("HttpRequestException", status, "Response status code does not indicate success: 503 (Service Unavailable).");

    /// <summary>
    /// AC2: the degraded event actually reaches the bus. This is the clause that fails on the
    /// pre-fix tree, where no publisher existed at all.
    /// </summary>
    [Fact]
    public async Task RepeatedFailures_PublishHealthDegradedEvent()
    {
        var (observer, bus, _) = CreateObserver(threshold: 3);

        for (var i = 0; i < 3; i++)
            await observer.RecordAsync(Provider, Failure());

        bus.Published.Count.ShouldBe(1);
        bus.Published[0].EventType.ShouldBe(WorldEventTypes.HealthDegraded);
    }

    /// <summary>
    /// AC3: the payload names the provider and the observed failure class and status code, so a
    /// channel can say which provider is down and why rather than that "something" is wrong.
    /// </summary>
    [Fact]
    public async Task DegradedEvent_PayloadCarriesProviderAndFailureDetail()
    {
        var (observer, bus, _) = CreateObserver(threshold: 2);

        await observer.RecordAsync(Provider, Failure(503));
        await observer.RecordAsync(Provider, Failure(503));

        var payload = bus.Published.ShouldHaveSingleItem().Payload;
        payload[WorldEventProviderHealthObserver.PayloadProvider].ShouldBe(Provider);
        payload[WorldEventProviderHealthObserver.PayloadFailureClass].ShouldBe("HttpRequestException");
        payload[WorldEventProviderHealthObserver.PayloadStatusCode].ShouldBe("503");
        payload[WorldEventProviderHealthObserver.PayloadConsecutiveFailures].ShouldBe("2");
    }

    /// <summary>
    /// A failure with no observable HTTP status omits the key rather than reporting zero. A
    /// fabricated 0 would read as a genuine measurement and misdirect whoever diagnoses the outage.
    /// </summary>
    [Fact]
    public async Task DegradedEvent_WhenNoStatusObserved_OmitsStatusCodeRatherThanReportingZero()
    {
        var (observer, bus, _) = CreateObserver(threshold: 1);

        await observer.RecordAsync(Provider, ProviderCredentialOutcome.Failed("SocketException", null, "connection reset"));

        var payload = bus.Published.ShouldHaveSingleItem().Payload;
        payload.ContainsKey(WorldEventProviderHealthObserver.PayloadStatusCode).ShouldBeFalse();
        payload[WorldEventProviderHealthObserver.PayloadFailureClass].ShouldBe("SocketException");
    }

    /// <summary>
    /// AC4: a sustained outage publishes once, not once per failed attempt. The observed incident
    /// retried 391 times in seven hours; without the cooldown that is 391 events.
    /// </summary>
    [Fact]
    public async Task SustainedOutage_WithinCooldown_PublishesExactlyOnce()
    {
        var (observer, bus, time) = CreateObserver(threshold: 3, cooldown: TimeSpan.FromMinutes(15));

        for (var i = 0; i < 50; i++)
        {
            await observer.RecordAsync(Provider, Failure());
            time.Advance(TimeSpan.FromSeconds(10));
        }

        bus.Published.Count.ShouldBe(1);
    }

    /// <summary>
    /// The cooldown re-arms: an outage still ongoing after the window publishes a fresh reminder,
    /// so a long incident is not announced once and then silently forgotten.
    /// </summary>
    [Fact]
    public async Task SustainedOutage_AfterCooldownElapses_PublishesAgain()
    {
        var (observer, bus, time) = CreateObserver(threshold: 1, cooldown: TimeSpan.FromMinutes(15));

        await observer.RecordAsync(Provider, Failure());
        time.Advance(TimeSpan.FromMinutes(16));
        await observer.RecordAsync(Provider, Failure());

        bus.Published.Count.ShouldBe(2);
        bus.Published.ShouldAllBe(e => e.EventType == WorldEventTypes.HealthDegraded);
    }

    /// <summary>
    /// AC6: a single transient failure below the threshold is normal operation and must alarm nobody.
    /// </summary>
    [Fact]
    public async Task SingleFailureBelowThreshold_PublishesNothing()
    {
        var (observer, bus, _) = CreateObserver(threshold: 3);

        await observer.RecordAsync(Provider, Failure());
        await observer.RecordAsync(Provider, Failure());

        bus.Published.ShouldBeEmpty();
    }

    /// <summary>
    /// A success resets the streak, so intermittent failures spread across successes never
    /// accumulate into a false outage signal.
    /// </summary>
    [Fact]
    public async Task InterleavedSuccess_ResetsFailureStreak()
    {
        var (observer, bus, _) = CreateObserver(threshold: 3);

        await observer.RecordAsync(Provider, Failure());
        await observer.RecordAsync(Provider, Failure());
        await observer.RecordAsync(Provider, ProviderCredentialOutcome.Success("key"));
        await observer.RecordAsync(Provider, Failure());
        await observer.RecordAsync(Provider, Failure());

        bus.Published.ShouldBeEmpty();
    }

    /// <summary>
    /// AC5: recovery is observable, so a channel that announced an outage can announce its end.
    /// </summary>
    [Fact]
    public async Task RecoveryAfterDegraded_PublishesRecoveredEvent()
    {
        var (observer, bus, _) = CreateObserver(threshold: 2);

        await observer.RecordAsync(Provider, Failure());
        await observer.RecordAsync(Provider, Failure());
        await observer.RecordAsync(Provider, ProviderCredentialOutcome.Success("key"));

        bus.Published.Count.ShouldBe(2);
        bus.Published[0].EventType.ShouldBe(WorldEventTypes.HealthDegraded);
        bus.Published[1].EventType.ShouldBe(WorldEventProviderHealthObserver.HealthRecoveredEventType);
        bus.Published[1].Payload[WorldEventProviderHealthObserver.PayloadProvider].ShouldBe(Provider);
    }

    /// <summary>
    /// Recovery is only announced if a degraded event was actually published. Otherwise the first
    /// thing a channel would hear about the provider is that it recovered from nothing.
    /// </summary>
    [Fact]
    public async Task SuccessWithoutPriorDegradedEvent_PublishesNothing()
    {
        var (observer, bus, _) = CreateObserver(threshold: 3);

        await observer.RecordAsync(Provider, Failure());
        await observer.RecordAsync(Provider, ProviderCredentialOutcome.Success("key"));

        bus.Published.ShouldBeEmpty();
    }

    /// <summary>
    /// An unconfigured provider is a steady state, not an outage. Reporting it as one would fire a
    /// degraded event on every host that simply does not use a given provider.
    /// </summary>
    [Fact]
    public async Task NotConfiguredOutcome_NeverPublishesAndNeverCountsAsFailure()
    {
        var (observer, bus, _) = CreateObserver(threshold: 2);

        for (var i = 0; i < 10; i++)
            await observer.RecordAsync(Provider, ProviderCredentialOutcome.NotConfigured());

        bus.Published.ShouldBeEmpty();

        // The NotConfigured results must not have contributed to the streak either: two real
        // failures are still required before anything is published.
        await observer.RecordAsync(Provider, Failure());
        bus.Published.ShouldBeEmpty();
        await observer.RecordAsync(Provider, Failure());
        bus.Published.Count.ShouldBe(1);
    }

    /// <summary>
    /// Failure state is per provider: one provider being down must neither trigger nor suppress a
    /// signal about another.
    /// </summary>
    [Fact]
    public async Task FailureState_IsTrackedPerProvider()
    {
        var (observer, bus, _) = CreateObserver(threshold: 2);

        await observer.RecordAsync("github-copilot", Failure());
        await observer.RecordAsync("anthropic", Failure());

        bus.Published.ShouldBeEmpty();

        await observer.RecordAsync("github-copilot", Failure());

        var published = bus.Published.ShouldHaveSingleItem();
        published.Payload[WorldEventProviderHealthObserver.PayloadProvider].ShouldBe("github-copilot");
    }

    /// <summary>
    /// A failing bus must not propagate into credential resolution. Failing to report an outage
    /// must not itself become an outage on the critical path of every agent turn.
    /// </summary>
    [Fact]
    public async Task WhenBusThrows_ObserverSwallowsTheFault()
    {
        var observer = new WorldEventProviderHealthObserver(
            new ThrowingEventBus(),
            NullLogger<WorldEventProviderHealthObserver>.Instance,
            new TestTimeProvider(DateTimeOffset.UnixEpoch),
            failureThreshold: 1);

        await Should.NotThrowAsync(() => observer.RecordAsync(Provider, Failure()));
    }

    private sealed class ThrowingEventBus : IWorldEventBus
    {
        public Task<int> PublishAsync(WorldEvent worldEvent, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("bus is down");

        public void SetSubscriptions(string agentId, IReadOnlyList<EventSubscription> subscriptions) { }
        public IReadOnlyList<EventSubscription> GetSubscriptions(string agentId) => [];
        public IReadOnlyList<string> GetSubscribers(string eventType) => [];
    }

    /// <summary>
    /// A threshold below one would mean "publish before any failure has happened", which is not a
    /// meaningful policy; it is rejected at construction rather than silently normalised.
    /// </summary>
    [Fact]
    public void Constructor_RejectsThresholdBelowOne()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new WorldEventProviderHealthObserver(
            new RecordingEventBus(),
            NullLogger<WorldEventProviderHealthObserver>.Instance,
            new TestTimeProvider(DateTimeOffset.UnixEpoch),
            failureThreshold: 0));
    }
}
