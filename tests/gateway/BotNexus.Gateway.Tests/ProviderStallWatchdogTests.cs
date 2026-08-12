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

    /// <summary>
    /// Upper bound on elements the cancellation test will consume before giving up. It exists purely so
    /// that a watchdog which ignores cancellation fails the assertion instead of hanging the run; a
    /// correct watchdog never comes close to it.
    /// </summary>
    private const int CancellationSafetyCap = 50;

    [Fact]
    public async Task WrapAsync_CancellationRequested_TerminatesWithoutError()
    {
        var watchdog = new ProviderStallWatchdog(TimeSpan.FromSeconds(30));

        // Two independent tokens on purpose. The watchdog is given `consumerCts`, which is what the test
        // cancels; the producer is given `producerCts`, which the test cancels only during teardown. That
        // separation is what makes this a test of the *watchdog*: the upstream iterator keeps producing
        // regardless of the consumer's cancellation, so ending the loop is something only the watchdog
        // can do. (The producer still honours a token so it can always be torn down safely -- an iterator
        // left with a pending MoveNextAsync kills the test host, see InfiniteStream / #2970.)
        using var consumerCts = new CancellationTokenSource();
        using var producerCts = new CancellationTokenSource();

        var results = new List<AgentStreamEvent>();
        var hitSafetyCap = false;

        try
        {
            // Enumeration completing without throwing is itself half the "terminates without error"
            // proof: external cancellation must not surface as an OperationCanceledException here.
            await foreach (var evt in watchdog.WrapAsync(InfiniteStream(producerCts.Token), consumerCts.Token))
            {
                results.Add(evt);
                consumerCts.Cancel();

                if (results.Count >= CancellationSafetyCap)
                {
                    hitSafetyCap = true;
                    break;
                }
            }
        }
        finally
        {
            await producerCts.CancelAsync();
        }

        // 1. Iteration ended because cancellation propagated, not because the test bailed out. This is
        //    the deterministic replacement for the old exact-count assertion: it holds under any legal
        //    scheduling (the producer may race ahead by any number of elements) yet still fails if the
        //    watchdog stops honouring cancellation.
        hitSafetyCap.ShouldBeFalse(
            $"iteration did not terminate after cancellation; consumed {results.Count} events up to the safety cap");

        // 2. Cancellation really was requested, so clause 1 cannot be satisfied by a stream that simply
        //    ended on its own.
        consumerCts.IsCancellationRequested.ShouldBeTrue();

        // 3. At least one event was delivered before cancellation took effect, and it is a real payload
        //    event -- the bounded form of the old `Count.ShouldBe(1)`.
        results.ShouldNotBeEmpty();
        results[0].Type.ShouldBe(AgentStreamEventType.ContentDelta);

        // 4. No synthetic error was surfaced. Cancellation is not a stall: the watchdog must terminate
        //    silently rather than emit its stall Error event. True for however many elements raced
        //    through before cancellation propagated.
        results.ShouldAllBe(e => e.Type == AgentStreamEventType.ContentDelta);
        results.ShouldNotContain(e => e.Type == AgentStreamEventType.Error);
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

    /// <summary>
    /// A long-running producer used to exercise cancellation. It honours the token so the iterator can
    /// always terminate: an async iterator that yields forever leaves a MoveNextAsync pending once the
    /// consumer stops reading, and disposing it in that state corrupts its value-task source and throws
    /// InvalidOperationException on a ThreadPool thread -- crashing the test host rather than failing a
    /// test (#2970). Fenced by TestConcurrencyFlakeFenceTests.
    /// </summary>
    private static async IAsyncEnumerable<AgentStreamEvent> InfiniteStream(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            yield return new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "." };

            try
            {
                await Task.Delay(10, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
        }
    }
}
