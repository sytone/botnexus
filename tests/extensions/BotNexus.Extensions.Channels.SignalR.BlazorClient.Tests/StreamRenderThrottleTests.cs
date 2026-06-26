using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Shouldly;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Tests for the streaming hot-path performance fix (#1620): the content/thinking
/// delta buffers must accumulate without an O(n^2) per-delta string copy, and the
/// high-frequency delta render-notify must be time-coalesced so a long streamed
/// response does not produce one re-render per token, while discrete lifecycle
/// events still notify immediately.
/// </summary>
public sealed class StreamRenderThrottleTests
{
    // ── ConversationStreamState buffer accumulation (O(n^2) fix) ───────────────

    [Fact]
    public void AppendBuffer_accumulates_deltas_in_order()
    {
        var state = new ConversationStreamState();

        state.AppendBuffer("Hello");
        state.AppendBuffer(", ");
        state.AppendBuffer("world");

        state.Buffer.ShouldBe("Hello, world");
    }

    [Fact]
    public void AppendBuffer_treats_null_delta_as_empty()
    {
        var state = new ConversationStreamState();

        state.AppendBuffer("a");
        state.AppendBuffer(null);
        state.AppendBuffer("b");

        state.Buffer.ShouldBe("ab");
    }

    [Fact]
    public void AppendThinking_accumulates_separately_from_content()
    {
        var state = new ConversationStreamState();

        state.AppendBuffer("answer");
        state.AppendThinking("reasoning");

        state.Buffer.ShouldBe("answer");
        state.ThinkingBuffer.ShouldBe("reasoning");
    }

    [Fact]
    public void Reset_clears_both_buffers()
    {
        var state = new ConversationStreamState { IsStreaming = true };
        state.AppendBuffer("content");
        state.AppendThinking("thinking");

        state.Reset();

        state.Buffer.ShouldBe("");
        state.ThinkingBuffer.ShouldBe("");
        state.IsStreaming.ShouldBeFalse();
    }

    [Fact]
    public void Buffer_returns_full_accumulation_after_many_appends()
    {
        // Regression guard for the O(n^2) concat path: many small deltas must still
        // reassemble losslessly into the full streamed response.
        var state = new ConversationStreamState();
        var expected = string.Empty;
        for (var i = 0; i < 2000; i++)
        {
            var token = $"t{i} ";
            state.AppendBuffer(token);
            expected += token;
        }

        state.Buffer.ShouldBe(expected);
    }

    // ── ClientStateStore throttled notify (render-storm fix) ───────────────────

    [Fact]
    public void NotifyChangedThrottled_coalesces_a_burst_into_few_renders()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 6, 25, 19, 0, 0, TimeSpan.Zero));
        var store = new ClientStateStore(clock);
        var renders = 0;
        store.OnChanged += () => renders++;

        // A tight burst of 1000 deltas all within the same coalescing window
        // (clock does not advance) must NOT produce 1000 renders.
        for (var i = 0; i < 1000; i++)
            store.NotifyChangedThrottled();

        renders.ShouldBeLessThan(1000);
        // Leading-edge fire: the first throttled notify renders immediately.
        renders.ShouldBe(1);
    }

    [Fact]
    public void NotifyChangedThrottled_fires_again_after_window_elapses()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 6, 25, 19, 0, 0, TimeSpan.Zero));
        var store = new ClientStateStore(clock);
        var renders = 0;
        store.OnChanged += () => renders++;

        store.NotifyChangedThrottled();   // leading edge -> render #1
        renders.ShouldBe(1);

        // Still inside the window: coalesced, no new render.
        store.NotifyChangedThrottled();
        renders.ShouldBe(1);

        // Advance past the coalescing window: the next throttled notify flushes.
        clock.Advance(TimeSpan.FromMilliseconds(100));
        store.NotifyChangedThrottled();
        renders.ShouldBe(2);
    }

    [Fact]
    public void NotifyChanged_always_fires_immediately_even_within_window()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 6, 25, 19, 0, 0, TimeSpan.Zero));
        var store = new ClientStateStore(clock);
        var renders = 0;
        store.OnChanged += () => renders++;

        store.NotifyChangedThrottled();   // render #1 (leading edge)
        store.NotifyChangedThrottled();   // coalesced
        renders.ShouldBe(1);

        // Discrete lifecycle event: must flush immediately, never coalesced,
        // so tool-start/tool-end/message-complete are always visible at once.
        store.NotifyChanged();
        renders.ShouldBe(2);
    }

    [Fact]
    public void NotifyChanged_flushes_a_pending_coalesced_throttled_change()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 6, 25, 19, 0, 0, TimeSpan.Zero));
        var store = new ClientStateStore(clock);
        var renders = 0;
        store.OnChanged += () => renders++;

        store.NotifyChangedThrottled();   // render #1, opens the window
        store.NotifyChangedThrottled();   // coalesced (pending)
        store.NotifyChangedThrottled();   // coalesced (pending)
        renders.ShouldBe(1);

        // A discrete notify after coalesced deltas flushes immediately and resets the window,
        // guaranteeing the trailing accumulated state is on screen even if no further delta arrives.
        store.NotifyChanged();            // render #2 (flush)
        renders.ShouldBe(2);

        // The window was just reset, so an immediate throttled delta coalesces (no new render)
        // until the window elapses -- then the next throttled delta fires on its leading edge.
        store.NotifyChangedThrottled();   // still inside reset window -> coalesced
        renders.ShouldBe(2);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        store.NotifyChangedThrottled();   // new window leading edge -> render #3
        renders.ShouldBe(3);
    }

    [Fact]
    public void Default_store_constructs_without_a_clock_argument()
    {
        // DI registers ClientStateStore with no clock; the system clock must be the default.
        var store = new ClientStateStore();
        var renders = 0;
        store.OnChanged += () => renders++;

        store.NotifyChangedThrottled();

        renders.ShouldBe(1);
    }

    /// <summary>
    /// Minimal fake <see cref="TimeProvider"/> that only models wall-clock advance,
    /// mirroring the repo's existing TestTimeProvider pattern. The throttle computes
    /// the coalescing window from <see cref="GetUtcNow"/>, so this is deterministic.
    /// </summary>
    private sealed class FixedClock : TimeProvider
    {
        private DateTimeOffset _now;

        public FixedClock(DateTimeOffset start) => _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
