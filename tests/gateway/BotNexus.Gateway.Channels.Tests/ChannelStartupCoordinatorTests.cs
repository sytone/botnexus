using System.Net;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Channels.Startup;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Channels.Tests;

/// <summary>
/// #2447: adapter startup was one-shot - a single transient upstream 502 permanently disabled a
/// channel for the process lifetime, and the host still reported plain success.
/// </summary>
public sealed class ChannelStartupCoordinatorTests
{
    private static ChannelStartupCoordinator CreateCoordinator(
        ILogger logger,
        int maxAttempts = 4)
        // No real waiting: the backoff is asserted via ComputeDelay, not by sleeping.
        => new(logger, new ChannelStartRetryPolicy(maxAttempts, TimeSpan.FromMilliseconds(1)), (_, _) => Task.CompletedTask);

    [Fact]
    public async Task StartAllAsync_TransientFailureAtStart_AdapterReachesStartedStateAfterRetry()
    {
        // Fails the first attempt with the exact incident shape (502 Bad Gateway), then succeeds.
        var adapter = new FakeAdapter(
            "telegram",
            failuresBeforeSuccess: 1,
            failureFactory: () => new HttpRequestException(
                "Telegram API call 'deleteWebhook' failed (502): Bad Gateway", null, HttpStatusCode.BadGateway));

        var outcomes = await CreateCoordinator(NullLogger.Instance)
            .StartAllAsync([adapter], new NoOpDispatcher(), CancellationToken.None);

        var outcome = outcomes.ShouldHaveSingleItem();
        outcome.Started.ShouldBeTrue();
        outcome.Attempts.ShouldBe(2);
        adapter.IsRunning.ShouldBeTrue();
        adapter.StartAttempts.ShouldBe(2);
    }

    [Fact]
    public async Task StartAllAsync_TerminalFailure_IsNotRetriedAndIsLoggedOnce()
    {
        var adapter = new FakeAdapter(
            "telegram",
            failuresBeforeSuccess: int.MaxValue,
            failureFactory: () => new HttpRequestException(
                "Telegram API call 'getMe' failed (401): Unauthorized", null, HttpStatusCode.Unauthorized));

        var logger = new RecordingLogger();

        var outcomes = await CreateCoordinator(logger)
            .StartAllAsync([adapter], new NoOpDispatcher(), CancellationToken.None);

        var outcome = outcomes.ShouldHaveSingleItem();
        outcome.Started.ShouldBeFalse();
        outcome.FailureKind.ShouldBe(ChannelFailureKind.Terminal);

        // Exactly one attempt: a revoked token cannot be retried into working.
        outcome.Attempts.ShouldBe(1);
        adapter.StartAttempts.ShouldBe(1);

        logger.Entries.Count(e => e.Level == LogLevel.Error).ShouldBe(1);
        logger.Entries.ShouldNotContain(e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task StartAllAsync_TransientFailureThatNeverClears_GivesUpAfterBoundedAttempts()
    {
        var adapter = new FakeAdapter(
            "telegram",
            failuresBeforeSuccess: int.MaxValue,
            failureFactory: () => new HttpRequestException("502", null, HttpStatusCode.BadGateway));

        var logger = new RecordingLogger();

        var outcomes = await CreateCoordinator(logger, maxAttempts: 3)
            .StartAllAsync([adapter], new NoOpDispatcher(), CancellationToken.None);

        var outcome = outcomes.ShouldHaveSingleItem();
        outcome.Started.ShouldBeFalse();

        // Bounded - this must NOT become #2386's unbounded-retry defect.
        outcome.Attempts.ShouldBe(3);
        adapter.StartAttempts.ShouldBe(3);
        logger.Entries.Count(e => e.Level == LogLevel.Error).ShouldBe(1);
    }

    [Fact]
    public async Task StartAllAsync_OneAdapterFails_OtherAdaptersStillStart()
    {
        var failing = new FakeAdapter("telegram", int.MaxValue,
            () => new HttpRequestException("401", null, HttpStatusCode.Unauthorized));
        var healthy = new FakeAdapter("signalr", 0, () => new InvalidOperationException());

        var outcomes = await CreateCoordinator(NullLogger.Instance)
            .StartAllAsync([failing, healthy], new NoOpDispatcher(), CancellationToken.None);

        outcomes.Count.ShouldBe(2);
        outcomes[0].Started.ShouldBeFalse();
        outcomes[1].Started.ShouldBeTrue();
        healthy.IsRunning.ShouldBeTrue();
    }

    [Fact]
    public void DescribeStartup_WithFailure_NamesTheFailedChannelAndDistinguishesConfiguredFromStarted()
    {
        var summary = ChannelStartupCoordinator.DescribeStartup(
        [
            new ChannelStartOutcome("telegram", "Telegram Bot", Started: false, 4, ChannelFailureKind.Transient, new HttpRequestException()),
            new ChannelStartOutcome("signalr", "SignalR", Started: true, 1, null, null),
        ]);

        summary.ShouldContain("DEGRADED");
        summary.ShouldContain("1 of 2");
        summary.ShouldContain("telegram");
    }

    [Fact]
    public void DescribeStartup_AllStarted_ReportsFullCount()
    {
        var summary = ChannelStartupCoordinator.DescribeStartup(
        [
            new ChannelStartOutcome("signalr", "SignalR", Started: true, 1, null, null),
        ]);

        summary.ShouldNotContain("DEGRADED");
        summary.ShouldContain("1 of 1");
    }

    [Fact]
    public void ComputeDelay_GrowsExponentiallyAndIsCapped()
    {
        var policy = new ChannelStartRetryPolicy(
            maxAttempts: 10, baseDelay: TimeSpan.FromSeconds(2), maxDelay: TimeSpan.FromSeconds(10));

        policy.ComputeDelay(1).ShouldBe(TimeSpan.FromSeconds(2));
        policy.ComputeDelay(2).ShouldBe(TimeSpan.FromSeconds(4));
        policy.ComputeDelay(3).ShouldBe(TimeSpan.FromSeconds(8));
        policy.ComputeDelay(4).ShouldBe(TimeSpan.FromSeconds(10)); // capped
        policy.ComputeDelay(50).ShouldBe(TimeSpan.FromSeconds(10)); // no overflow
    }

    private sealed class FakeAdapter(string channelType, int failuresBeforeSuccess, Func<Exception> failureFactory)
        : ChannelAdapterBase(NullLogger.Instance)
    {
        private int _failuresRemaining = failuresBeforeSuccess;

        public int StartAttempts { get; private set; }

        public override ChannelKey ChannelType { get; } = ChannelKey.From(channelType);

        public override string DisplayName => ChannelType.Value;

        public override Task SendAsync(OutboundMessage message, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        protected override Task OnStartAsync(CancellationToken cancellationToken)
        {
            StartAttempts++;

            if (_failuresRemaining <= 0)
                return Task.CompletedTask;

            if (_failuresRemaining != int.MaxValue)
                _failuresRemaining--;

            throw failureFactory();
        }

        protected override Task OnStopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoOpDispatcher : IChannelDispatcher
    {
        public Task DispatchAsync(InboundMessage message, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class RecordingLogger : ILogger
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
    }
}
