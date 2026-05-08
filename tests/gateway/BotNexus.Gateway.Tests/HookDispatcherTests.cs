using BotNexus.Gateway.Abstractions.Hooks;
using BotNexus.Gateway.Hooks;

namespace BotNexus.Gateway.Tests;

public sealed class HookDispatcherTests
{
    private readonly HookDispatcher _dispatcher = new();

    // ── Priority ordering ────────────────────────────────────────────

    [Fact]
    public async Task Dispatch_MultipleHandlers_RunsInPriorityOrder()
    {
        var executionOrder = new List<string>();

        _dispatcher.Register<BeforePromptBuildEvent, BeforePromptBuildResult>(
            new DelegateHookHandler<BeforePromptBuildEvent, BeforePromptBuildResult>(
                priority: 10, _ =>
                {
                    executionOrder.Add("second");
                    return new BeforePromptBuildResult { AppendSystemContext = "B" };
                }));

        _dispatcher.Register<BeforePromptBuildEvent, BeforePromptBuildResult>(
            new DelegateHookHandler<BeforePromptBuildEvent, BeforePromptBuildResult>(
                priority: -5, _ =>
                {
                    executionOrder.Add("first");
                    return new BeforePromptBuildResult { PrependSystemContext = "A" };
                }));

        var evt = new BeforePromptBuildEvent("agent-1", "prompt", []);
        var results = await _dispatcher.DispatchAsync<BeforePromptBuildEvent, BeforePromptBuildResult>(evt);

        executionOrder.ToList().ShouldBe(new[] { "first", "second" });
        results.Count().ShouldBe(2);
        results[0].PrependSystemContext.ShouldBe("A");
        results[1].AppendSystemContext.ShouldBe("B");
    }

    // ── No handlers ──────────────────────────────────────────────────

    [Fact]
    public async Task Dispatch_NoHandlers_ReturnsEmpty()
    {
        var evt = new AfterToolCallEvent("agent-1", "tool", "tc-1", null, false);
        var results = await _dispatcher.DispatchAsync<AfterToolCallEvent, AfterToolCallResult>(evt);

        results.ShouldBeEmpty();
    }

    // ── Null filtering ───────────────────────────────────────────────

    [Fact]
    public async Task Dispatch_NullResult_IsFilteredOut()
    {
        _dispatcher.Register<AfterToolCallEvent, AfterToolCallResult>(
            new DelegateHookHandler<AfterToolCallEvent, AfterToolCallResult>(
                priority: 0, _ => null));

        _dispatcher.Register<AfterToolCallEvent, AfterToolCallResult>(
            new DelegateHookHandler<AfterToolCallEvent, AfterToolCallResult>(
                priority: 1, _ => new AfterToolCallResult()));

        var evt = new AfterToolCallEvent("agent-1", "tool", "tc-1", "ok", false);
        var results = await _dispatcher.DispatchAsync<AfterToolCallEvent, AfterToolCallResult>(evt);

        results.Count().ShouldBe(1);
    }

    // ── BeforePromptBuild merging ────────────────────────────────────

    [Fact]
    public async Task BeforePromptBuild_MergesPrependAndAppendFromMultipleHandlers()
    {
        _dispatcher.Register<BeforePromptBuildEvent, BeforePromptBuildResult>(
            new DelegateHookHandler<BeforePromptBuildEvent, BeforePromptBuildResult>(
                priority: 0, _ => new BeforePromptBuildResult { PrependSystemContext = "Ctx-A" }));

        _dispatcher.Register<BeforePromptBuildEvent, BeforePromptBuildResult>(
            new DelegateHookHandler<BeforePromptBuildEvent, BeforePromptBuildResult>(
                priority: 1, _ => new BeforePromptBuildResult
                {
                    PrependSystemContext = "Ctx-B",
                    AppendSystemContext = "Suffix-B"
                }));

        var evt = new BeforePromptBuildEvent("agent-1", "prompt", []);
        var results = await _dispatcher.DispatchAsync<BeforePromptBuildEvent, BeforePromptBuildResult>(evt);

        results.Count().ShouldBe(2);

        // Consumers can merge: collect all prepend/append values
        var allPrepend = results
            .Where(r => r.PrependSystemContext is not null)
            .Select(r => r.PrependSystemContext)
            .ToList();
        var allAppend = results
            .Where(r => r.AppendSystemContext is not null)
            .Select(r => r.AppendSystemContext)
            .ToList();

        allPrepend.ToList().ShouldBe(new[] { "Ctx-A", "Ctx-B" }, ignoreOrder: false);
        allAppend.ShouldHaveSingleItem().ShouldBe("Suffix-B");
    }

    // ── BeforeToolCall deny ──────────────────────────────────────────

    [Fact]
    public async Task BeforeToolCall_DenyResult_IsReturnedInResults()
    {
        _dispatcher.Register<BeforeToolCallEvent, BeforeToolCallResult>(
            new DelegateHookHandler<BeforeToolCallEvent, BeforeToolCallResult>(
                priority: 0, _ => new BeforeToolCallResult
                {
                    Denied = true,
                    DenyReason = "Extension policy blocks this tool."
                }));

        // A lower-priority handler that would modify args — still runs, but
        // the consumer should short-circuit on the first Denied == true.
        _dispatcher.Register<BeforeToolCallEvent, BeforeToolCallResult>(
            new DelegateHookHandler<BeforeToolCallEvent, BeforeToolCallResult>(
                priority: 10, _ => new BeforeToolCallResult
                {
                    ModifiedArguments = new Dictionary<string, object?> { ["key"] = "val" }
                }));

        var args = new Dictionary<string, object?> { ["file"] = "secret.txt" };
        var evt = new BeforeToolCallEvent("agent-1", "read_file", "tc-1", args);
        var results = await _dispatcher.DispatchAsync<BeforeToolCallEvent, BeforeToolCallResult>(evt);

        // Consumer short-circuits: check first result with Denied == true
        var denied = results.FirstOrDefault(r => r.Denied);
        denied.ShouldNotBeNull();
        denied!.DenyReason.ShouldBe("Extension policy blocks this tool.");
    }

    // ── Test helper ──────────────────────────────────────────────────

    private sealed class DelegateHookHandler<TEvent, TResult>(
        int priority,
        Func<TEvent, TResult?> handler)
        : IHookHandler<TEvent, TResult>
    {
        public int Priority => priority;

        public Task<TResult?> HandleAsync(TEvent hookEvent, CancellationToken ct = default)
            => Task.FromResult(handler(hookEvent));
    }
}
