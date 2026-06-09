using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Streaming;
using Moq;

namespace BotNexus.Gateway.Tests;

public sealed class ProviderStallWatchdogTests
{
    [Fact]
    public void Constructor_WithZeroTimeout_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new ProviderStallWatchdog(TimeSpan.Zero));
    }

    [Fact]
    public void Constructor_WithNegativeTimeout_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new ProviderStallWatchdog(TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void Constructor_WithNullTimeout_UsesDefault()
    {
        var watchdog = new ProviderStallWatchdog();
        watchdog.InactivityTimeout.ShouldBe(ProviderStallWatchdog.DefaultTimeout);
    }

    [Fact]
    public void Constructor_WithCustomTimeout_UsesProvidedValue()
    {
        var watchdog = new ProviderStallWatchdog(TimeSpan.FromSeconds(30));
        watchdog.InactivityTimeout.ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task WrapAsync_StreamCompletesNormally_YieldsAllEvents()
    {
        var watchdog = new ProviderStallWatchdog(TimeSpan.FromSeconds(5));
        var events = new[]
        {
            new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "Hello" },
            new AgentStreamEvent { Type = AgentStreamEventType.MessageEnd }
        };

        var results = new List<AgentStreamEvent>();
        await foreach (var evt in watchdog.WrapAsync(ToAsync(events)))
        {
            results.Add(evt);
        }

        results.Count.ShouldBe(2);
        results[0].Type.ShouldBe(AgentStreamEventType.ContentDelta);
        results[1].Type.ShouldBe(AgentStreamEventType.MessageEnd);
    }

    [Fact]
    public async Task WrapAsync_StreamStalls_YieldsErrorAndTerminates()
    {
        var watchdog = new ProviderStallWatchdog(TimeSpan.FromMilliseconds(100));

        var results = new List<AgentStreamEvent>();
        await foreach (var evt in watchdog.WrapAsync(StallAfterFirst()))
        {
            results.Add(evt);
        }

        results.Count.ShouldBe(2);
        results[0].Type.ShouldBe(AgentStreamEventType.ContentDelta);
        results[1].Type.ShouldBe(AgentStreamEventType.Error);
        results[1].ErrorMessage.ShouldNotBeNull();
        results[1].ErrorMessage!.ShouldContain("Provider stall detected");
    }

    [Fact]
    public async Task WrapAsync_EmptyStream_YieldsNothing()
    {
        var watchdog = new ProviderStallWatchdog(TimeSpan.FromSeconds(5));

        var results = new List<AgentStreamEvent>();
        await foreach (var evt in watchdog.WrapAsync(ToAsync(Array.Empty<AgentStreamEvent>())))
        {
            results.Add(evt);
        }

        results.ShouldBeEmpty();
    }

    [Fact]
    public async Task WrapAsync_CancellationRequested_TerminatesWithoutError()
    {
        var watchdog = new ProviderStallWatchdog(TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource();

        var results = new List<AgentStreamEvent>();
        await foreach (var evt in watchdog.WrapAsync(InfiniteStream(), cts.Token))
        {
            results.Add(evt);
            cts.Cancel();
        }

        // Should get the first event, then cancellation stops iteration.
        results.Count.ShouldBe(1);
        results[0].Type.ShouldBe(AgentStreamEventType.ContentDelta);
    }

    private static async IAsyncEnumerable<AgentStreamEvent> ToAsync(IEnumerable<AgentStreamEvent> events)
    {
        foreach (var evt in events)
        {
            yield return evt;
            await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<AgentStreamEvent> StallAfterFirst()
    {
        yield return new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "data" };
        await Task.Delay(TimeSpan.FromSeconds(30)); // Will be interrupted by watchdog timeout
        yield return new AgentStreamEvent { Type = AgentStreamEventType.MessageEnd }; // Never reached
    }

    private static async IAsyncEnumerable<AgentStreamEvent> InfiniteStream()
    {
        while (true)
        {
            yield return new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "." };
            await Task.Delay(10);
        }
    }
}
