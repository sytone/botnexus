using System.Reflection;
using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile.Pages;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using NSubstitute;
using Shouldly;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Issue #2320: the mobile chat page enumerated LIVE collections owned by the client state store
/// (the conversation message list, the agent dictionary and the per-agent conversation dictionary).
/// A concurrent SignalR handler appending a message during one of those enumerations raised
/// <c>InvalidOperationException</c> ("Collection was modified") and aborted the render, leaving the
/// UI stale. The worst offender awaited JS interop INSIDE the message loop, so the enumeration was
/// suspended across an async yield -- a guaranteed mutation window.
///
/// These tests pin the observable behaviour: the render survives a store mutation raised while it is
/// in flight, every message that was in the timeline when the pass started is still rendered, and the
/// concurrently appended message is visible afterwards.
/// </summary>
public sealed class MobileChatConcurrentStoreMutationTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly ClientStateStore _store = new();
    private readonly IPortalLoadService _portalLoad = Substitute.For<IPortalLoadService>();
    private readonly IAgentInteractionService _interaction = Substitute.For<IAgentInteractionService>();
    private readonly PausingJsRuntime _js;

    public MobileChatConcurrentStoreMutationTests()
    {
        _js = new PausingJsRuntime();

        _portalLoad.IsReady.Returns(true);
        _portalLoad.IsLoading.Returns(false);
        _portalLoad.IsSignalRConnected.Returns(true);
        _portalLoad.LoadError.Returns((string?)null);
        _portalLoad.InitializeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        _ctx.Services.AddSingleton<IClientStateStore>(_store);
        var displayedConversation = Substitute.For<IDisplayedConversation>();
        displayedConversation.DisplayedConversationIdFor(Arg.Any<string?>())
            .Returns(call => call.Arg<string?>() is { } agentId ? (_store as IDisplayedConversation)?.DisplayedConversationIdFor(agentId) : null);
        _ctx.Services.AddSingleton(displayedConversation);
        _ctx.Services.AddSingleton(_portalLoad);
        _ctx.Services.AddSingleton(new BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile.Services.MobileHubTuningOptions());
        _ctx.Services.AddSingleton(_interaction);
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        // Registered last so the component resolves this runtime instead of the bUnit one: it gives a
        // deterministic hook for "a concurrent handler mutates the store WHILE the render pass is
        // suspended on JS interop", which is exactly the #2320 window.
        _ctx.Services.AddSingleton<IJSRuntime>(_js);

        _store.SeedAgents([new AgentSummary("agent-1", "Alpha", null, null, false)]);
        _store.SeedConversations("agent-1",
        [
            new ConversationSummaryDto("conv-1", "agent-1", "C", true, "Active", "sess-1", 1,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        ]);
        _store.SelectView("agent-1", "conv-1", SelectionSource.RouteNavigation);
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task Markdown_render_pass_survives_message_appended_mid_pass()
    {
        _store.AppendMessage("conv-1", new ChatMessage("assistant", "first", DateTimeOffset.UtcNow));
        _store.AppendMessage("conv-1", new ChatMessage("assistant", "second", DateTimeOffset.UtcNow));

        var cut = _ctx.Render<Chat>(p => p.Add(c => c.AgentId, "agent-1").Add(c => c.ConversationId, "conv-1"));
        _js.PauseFirstMarkdown = true;
        var activePass = RenderMarkdownPassAsync(cut);
        try
        {
            // This deadline diagnoses a missing interop boundary; it does not retry assertions.
            await _js.FirstMarkdownStarted.WaitAsync(TimeSpan.FromSeconds(1));
            activePass.IsCompleted.ShouldBeFalse();
            _js.MarkdownSources.ShouldBe(["first"]);

            // No notification: no second pass can hide an aborted first pass. GetMessages already
            // returns a locked snapshot (#2712), independently of the component's ToArray (#2320).
            var conv = _store.GetConversation("conv-1")
                ?? throw new InvalidOperationException("The seeded conversation is missing.");
            conv.AppendMessage(new ChatMessage("assistant", "raced", DateTimeOffset.UtcNow));
        }
        finally
        {
            // Always release and drain the actual pass before disposing the renderer, even if a
            // readiness/mutation assertion fails. Interop returning is NOT pass completion.
            _js.ResumeFirstMarkdown();
            await activePass;
        }

        // Under unsafe live enumeration the second MoveNext throws; awaiting the pass surfaces it
        // directly instead of letting HandleStateChanged swallow it and an unrelated pass repair it.
        cut.Markup.ShouldContain("MD:first");
        cut.Markup.ShouldContain("MD:second");
        _js.MarkdownSources.ShouldBe(["first", "second"]);
        cut.Markup.ShouldContain("raced");
        cut.Markup.ShouldNotContain("MD:raced");
        _js.Failures.ShouldBeEmpty();

        await RenderMarkdownPassAsync(cut);
        cut.Markup.ShouldContain("MD:first");
        cut.Markup.ShouldContain("MD:second");
        cut.Markup.ShouldContain("MD:raced");
        _js.MarkdownSources.ShouldBe(["first", "second", "raced"]);
        _js.Failures.ShouldBeEmpty();
    }

    [Fact]
    public async Task Markdown_render_coordination_is_stable_across_parallel_repetitions()
    {
        // Independent renderers and signals exercise parallel scheduling without serializing the
        // assembly or retrying a failed assertion. Every repetition must satisfy the whole contract.
        await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            using var fixture = new MobileChatConcurrentStoreMutationTests();
            await fixture.Markdown_render_pass_survives_message_appended_mid_pass();
        }));
    }

    [Fact]
    public async Task Chat_render_survives_concurrent_appends_from_background_continuations()
    {
        _store.AppendMessage("conv-1", new ChatMessage("assistant", "seed", DateTimeOffset.UtcNow));

        var cut = _ctx.Render<Chat>(p => p.Add(c => c.AgentId, "agent-1").Add(c => c.ConversationId, "conv-1"));

        var conv = _store.GetConversation("conv-1")!;
        var errors = new List<Exception>();
        using var stop = new CancellationTokenSource();

        var mutator = Task.Run(async () =>
        {
            for (var i = 0; i < 400 && !stop.IsCancellationRequested; i++)
            {
                conv.AppendMessage(new ChatMessage("assistant", $"bg-{i}", DateTimeOffset.UtcNow));
                await Task.Yield();
            }
        });

        for (var i = 0; i < 40; i++)
        {
            try
            {
                await cut.InvokeAsync(() => _store.NotifyChanged());
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        }

        stop.Cancel();
        await mutator;

        errors.ShouldBeEmpty();
        _js.Failures.ShouldBeEmpty();

        conv.AppendMessage(new ChatMessage("assistant", "final-marker", DateTimeOffset.UtcNow));
        await cut.InvokeAsync(() => _store.NotifyChanged());
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("final-marker"));
    }

    private static Task RenderMarkdownPassAsync(IRenderedComponent<Chat> cut)
    {
        // NotifyChanged is fire-and-forget and OnAfterRenderAsync does not render markdown. Keep
        // production encapsulation intact while awaiting the real pass and its StateHasChanged on
        // the renderer; do not add a production API solely to expose completion to this fixture.
        var method = typeof(Chat).GetMethod("RenderMarkdownAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Chat.RenderMarkdownAsync was not found.");
        return cut.InvokeAsync(() => method.Invoke(cut.Instance, null) as Task
            ?? throw new InvalidOperationException("Chat.RenderMarkdownAsync did not return a Task."));
    }

    /// <summary>
    /// Minimal JS runtime stand-in with instance-local readiness/resume signals. The focused race
    /// test pauses its first markdown call; the background stress test retains yielding interop.
    /// </summary>
    private sealed class PausingJsRuntime : IJSRuntime
    {
        private int _markdownCalls;
        private readonly TaskCompletionSource _firstMarkdownStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _resumeFirstMarkdown = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool PauseFirstMarkdown { get; set; }
        public Task FirstMarkdownStarted => _firstMarkdownStarted.Task;
        public List<string> MarkdownSources { get; } = new();
        public List<Exception> Failures { get; } = new();

        public void ResumeFirstMarkdown() => _resumeFirstMarkdown.TrySetResult();

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "BotNexus.renderMarkdown")
            {
                var source = args is { Length: > 0 } && args[0] is string s ? s : string.Empty;
                MarkdownSources.Add(source);
                if (Interlocked.Increment(ref _markdownCalls) == 1 && PauseFirstMarkdown)
                {
                    _firstMarkdownStarted.TrySetResult();
                    try
                    {
                        await _resumeFirstMarkdown.Task.WaitAsync(cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        Failures.Add(ex);
                        throw;
                    }
                }
                else
                {
                    await Task.Yield();
                }

                if (typeof(TValue) == typeof(string))
                    return (TValue)(object)$"<p><strong>MD:{source}</strong></p>";
            }

            return default!;
        }
    }
}
