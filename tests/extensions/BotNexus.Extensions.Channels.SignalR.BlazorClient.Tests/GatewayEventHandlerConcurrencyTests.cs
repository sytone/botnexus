using System.Text.RegularExpressions;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.Logging;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// #1621 (b) + (c): concurrency-correctness contract for <see cref="GatewayEventHandler"/>.
/// (b) Fire-and-forget work on the reconnect/refresh path must be OBSERVED -- a faulted task must
/// surface to the logger instead of becoming an unobserved task exception, and must not throw to the
/// synchronous SignalR callback that launched it.
/// (c) The two shared <c>HashSet&lt;string&gt;</c> fields (the streaming-when-disconnected set and the
/// pending-conversation-refresh set) are mutated from SignalR client callbacks that are NOT guaranteed
/// to be serialized, so every Add/Remove/Clear/enumerate site must be synchronized -- a concurrent
/// enumerate-while-mutate can throw <see cref="InvalidOperationException"/> or tear state.
/// </summary>
public sealed class GatewayEventHandlerConcurrencyTests
{
    private readonly ClientStateStore _store = new();
    private readonly RecordingLogger<GatewayEventHandler> _logger = new();
    private readonly GatewayEventHandler _handler;

    public GatewayEventHandlerConcurrencyTests()
    {
        // A fresh, unconnected GatewayHubConnection makes SubscribeAllAsync fail inside
        // HandleReconnectedAsync (the reconnect-recovery failure path).
        _handler = new GatewayEventHandler(_store, new GatewayHubConnection(), _logger);

        _store.UpsertAgent(new AgentState
        {
            AgentId = "agent-1",
            DisplayName = "Agent 1",
            IsConnected = true,
            ActiveConversationId = "conv-1"
        });
        var agent = _store.GetAgent("agent-1")!;
        agent.Conversations["conv-1"] = new ConversationState
        {
            ConversationId = "conv-1",
            Title = "Conversation 1",
            HistoryLoaded = true
        };
    }

    // ── (b) fire-and-forget observation ──────────────────────────────────────

    [Fact]
    public async Task FireAndForget_refresh_failure_is_observed_and_logged_without_throwing_to_caller()
    {
        // The refresh delegate (wired by the portal) throws. HandleConversationChanged launches the
        // refresh as fire-and-forget; before the fix the faulted Task was discarded via `_ = ...` and
        // became an unobserved task exception. The synchronous SignalR callback must NOT throw, and
        // the failure must be logged.
        _handler.ConversationRefreshDelegate = _ => throw new InvalidOperationException("refresh boom");

        // agent-1 is not streaming -> HandleConversationChanged takes the immediate-refresh path.
        var act = () => _handler.HandleConversationChanged(
            new ConversationChangedPayload("updated", "agent-1", "conv-1"));

        // (1) does not throw to the caller (the SignalR callback thread)
        act.ShouldNotThrow();

        // (2) the failure is observed and logged at Error within a bounded wait
        await WaitForAsync(() => _logger.Entries.Any(e => e.Level == LogLevel.Error));
        var error = _logger.Entries.FirstOrDefault(e => e.Level == LogLevel.Error);
        error.ShouldNotBeNull("A failed fire-and-forget refresh must be logged at Error, not vanish.");
        error!.Exception.ShouldBeOfType<InvalidOperationException>();
    }

    [Fact]
    public async Task FireAndForget_refresh_faulted_task_is_observed_and_logged()
    {
        // Same contract, but the delegate returns a faulted Task (async failure) rather than throwing
        // synchronously -- both must be observed by the wrapper.
        _handler.ConversationRefreshDelegate = _ => Task.FromException(new TimeoutException("late boom"));

        _handler.HandleConversationChanged(
            new ConversationChangedPayload("updated", "agent-1", "conv-1"));

        await WaitForAsync(() => _logger.Entries.Any(e => e.Level == LogLevel.Error));
        var error = _logger.Entries.FirstOrDefault(e => e.Level == LogLevel.Error);
        error.ShouldNotBeNull("A faulted fire-and-forget refresh task must be observed and logged.");
        error!.Exception.ShouldBeOfType<TimeoutException>();
    }

    [Fact]
    public async Task HandleReconnectedAsync_recovery_failure_logs_error_and_still_clears_stale_stream_state()
    {
        // The #759 invariant: even when reconnect recovery fails, stale streaming state must be cleared
        // so the portal never gets stuck in a perpetual streaming indicator.
        var agent = _store.GetAgent("agent-1")!;
        var conv = agent.Conversations["conv-1"];
        agent.IsStreaming = true;
        conv.StreamState.IsStreaming = true;
        conv.StreamState.Buffer = "partial";

        _handler.HandleReconnecting();
        await _handler.HandleReconnectedAsync();

        var error = _logger.Entries.FirstOrDefault(e => e.Level == LogLevel.Error);
        error.ShouldNotBeNull("Reconnect recovery failure must be logged at Error.");

        conv.StreamState.IsStreaming.ShouldBeFalse();
        conv.StreamState.Buffer.ShouldBe(string.Empty);
        conv.HistoryLoaded.ShouldBeFalse();
    }

    // ── (c) shared HashSet synchronization ───────────────────────────────────

    [Fact]
    public async Task Concurrent_pending_refresh_set_access_does_not_throw_and_ends_consistent()
    {
        // Drive the pending-conversation-refresh set from many threads at once: Add via
        // HandleConversationChanged (mid-stream agent) racing Remove via the terminal handlers that
        // drain it. Without a lock around the HashSet this throws InvalidOperationException or corrupts
        // internal state (HashSet is not thread-safe).
        var agent = _store.GetAgent("agent-1")!;
        agent.IsStreaming = true; // forces HandleConversationChanged into the deferred (Add) branch

        // Register the session so the drain (which routes by session) can resolve the agent.
        _store.RegisterSession("agent-1", "sess-1", conversationId: "conv-1");
        agent.Conversations["conv-1"].ActiveSessionId = "sess-1";

        // A no-op refresh delegate keeps the drain side effect-free and fast.
        _handler.ConversationRefreshDelegate = _ => Task.CompletedTask;

        var exceptions = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        const int threads = 8;
        const int iterations = 5_000;

        var workers = Enumerable.Range(0, threads).Select(t => Task.Run(() =>
        {
            try
            {
                for (var i = 0; i < iterations; i++)
                {
                    if ((t & 1) == 0)
                    {
                        // Add path: keep the agent streaming so the deferred (Add) branch is taken.
                        agent.IsStreaming = true;
                        _handler.HandleConversationChanged(
                            new ConversationChangedPayload("updated", "agent-1", "conv-1"));
                    }
                    else
                    {
                        // Remove path: re-arm IsStreaming so HandleTurnEnd proceeds to the drain
                        // (which Removes from the pending set) rather than short-circuiting.
                        agent.IsStreaming = true;
                        _handler.HandleTurnEnd(new AgentStreamEvent { SessionId = "sess-1" });
                    }
                }
            }
            catch (Exception ex)
            {
                exceptions.Enqueue(ex);
            }
        })).ToArray();

        await Task.WhenAll(workers);

        exceptions.ShouldBeEmpty(
            "Concurrent access to the pending-conversation-refresh HashSet must be synchronized.");
    }

    [Fact]
    public async Task Concurrent_streaming_set_access_does_not_throw()
    {
        // Add many agents so the streaming set is populated and the enumerate-then-clear loop in
        // HandleReconnectedAsync has a non-trivial window. Race HandleReconnecting (Add) against
        // HandleReconnectedAsync (enumerate + Clear). Without a lock the foreach over the HashSet can
        // throw "Collection was modified" mid-enumeration.
        for (var a = 0; a < 50; a++)
        {
            var id = $"agent-stream-{a}";
            _store.UpsertAgent(new AgentState
            {
                AgentId = id,
                DisplayName = id,
                IsConnected = true,
                IsStreaming = true,
                ActiveConversationId = $"conv-{a}"
            });
            _store.GetAgent(id)!.Conversations[$"conv-{a}"] = new ConversationState
            {
                ConversationId = $"conv-{a}"
            };
        }

        var exceptions = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        const int iterations = 3_000;

        var adder = Task.Run(() =>
        {
            try
            {
                for (var i = 0; i < iterations; i++)
                    _handler.HandleReconnecting();
            }
            catch (Exception ex)
            {
                exceptions.Enqueue(ex);
            }
        });

        var clearer = Task.Run(async () =>
        {
            try
            {
                for (var i = 0; i < iterations; i++)
                    await _handler.HandleReconnectedAsync();
            }
            catch (Exception ex)
            {
                exceptions.Enqueue(ex);
            }
        });

        await Task.WhenAll(adder, clearer);

        exceptions.ShouldBeEmpty(
            "Concurrent access to the streaming-when-disconnected HashSet must be synchronized.");
    }

    // ── supplementary source guards ──────────────────────────────────────────

    [Fact]
    public void Reconnect_and_refresh_fire_and_forget_route_through_the_observed_helper()
    {
        var src = ReadHandlerSource();

        // No raw discard of the reconnect/refresh Task (the unobserved-exception bug).
        src.ShouldNotContain(
            "_ = HandleReconnectedAsync(",
            customMessage: "HandleReconnected must route through the observed SafeFireAsync helper.");
        Regex.IsMatch(src, @"_\s*=\s*RefreshConversationsAsync\(")
            .ShouldBeFalse("RefreshConversationsAsync fire-and-forget must route through SafeFireAsync.");

        src.ShouldContain(
            "SafeFireAsync",
            customMessage: "An observed fire-and-forget helper (SafeFireAsync) must exist.");
    }

    [Fact]
    public void Both_shared_hashsets_are_only_touched_inside_a_lock()
    {
        var src = ReadHandlerSource().Replace("\r\n", "\n");

        // Strip every synchronized access region, then assert NOTHING references either shared set
        // outside its declaration. Both the block form -- `lock (_stateGate) { ... }` -- and the
        // single-statement form -- `lock (_stateGate) <stmt>;` and `lock (_stateGate)\n <stmt>;` --
        // are removed. Whatever set reference survives the strip is an unsynchronized access site.
        var stripped = src;

        // Block form: lock (_stateGate) { ... } (no nested braces in our lock bodies).
        stripped = Regex.Replace(stripped, @"lock\s*\(_stateGate\)\s*\{[^{}]*\}", "/*locked*/", RegexOptions.Singleline);

        // Single-statement form: lock (_stateGate) <one statement up to the terminating ;>.
        stripped = Regex.Replace(stripped, @"lock\s*\(_stateGate\)\s*[^{};]*;", "/*locked*/", RegexOptions.Singleline);

        var leaks = stripped
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l =>
                !l.StartsWith("private readonly HashSet<string>") &&
                (l.Contains("_streamingWhenDisconnected") || l.Contains("_pendingConversationRefresh")))
            .ToList();

        leaks.ShouldBeEmpty(
            "Every Add/Remove/Clear/enumerate on the shared HashSets must be inside a lock (_stateGate): "
            + string.Join(" | ", leaks));
    }

    private static string ReadHandlerSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BotNexus.slnx")))
            dir = dir.Parent;
        if (dir is null)
            throw new InvalidOperationException("Could not locate BotNexus.slnx from test base directory.");

        var path = Path.Combine(
            dir.FullName,
            "src", "extensions",
            "BotNexus.Extensions.Channels.SignalR.BlazorClient.Core",
            "Services",
            "GatewayEventHandler.cs");
        return File.ReadAllText(path);
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 5_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(15);
        }
    }
}
