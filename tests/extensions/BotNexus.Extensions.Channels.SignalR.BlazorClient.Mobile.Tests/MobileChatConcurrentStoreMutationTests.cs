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
    private readonly MutatingJsRuntime _js;

    public MobileChatConcurrentStoreMutationTests()
    {
        _js = new MutatingJsRuntime();

        _portalLoad.IsReady.Returns(true);
        _portalLoad.IsLoading.Returns(false);
        _portalLoad.IsSignalRConnected.Returns(true);
        _portalLoad.LoadError.Returns((string?)null);
        _portalLoad.InitializeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        _ctx.Services.AddSingleton<IClientStateStore>(_store);
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
    public void Markdown_render_pass_survives_message_appended_mid_pass()
    {
        _store.AppendMessage("conv-1", new ChatMessage("assistant", "first", DateTimeOffset.UtcNow));
        _store.AppendMessage("conv-1", new ChatMessage("assistant", "second", DateTimeOffset.UtcNow));

        // While the render pass is suspended awaiting markdown interop for the FIRST message, a
        // concurrent handler appends a third message straight onto the conversation timeline (no
        // notification, so this is purely the mutation half of the race).
        _js.OnMarkdownRender = () => _store.GetConversation("conv-1")!
            .AppendMessage(new ChatMessage("assistant", "raced", DateTimeOffset.UtcNow));

        var cut = _ctx.Render<Chat>(p => p.Add(c => c.AgentId, "agent-1"));
        _store.NotifyChanged();

        cut.WaitForAssertion(() =>
        {
            // Every message present when the pass started must still be rendered through markdown.
            // Under the defect the enumeration threw on the second MoveNext and the pass aborted.
            cut.Markup.ShouldContain("MD:first");
            cut.Markup.ShouldContain("MD:second");
        });

        _js.Failures.ShouldBeEmpty();
        // ...and the concurrently appended message is visible once the render settles.
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("raced"));
    }

    [Fact]
    public async Task Chat_render_survives_concurrent_appends_from_background_continuations()
    {
        _store.AppendMessage("conv-1", new ChatMessage("assistant", "seed", DateTimeOffset.UtcNow));

        var cut = _ctx.Render<Chat>(p => p.Add(c => c.AgentId, "agent-1"));

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

    /// <summary>
    /// Minimal JS runtime stand-in. Returns deterministic markdown HTML, records any exception the
    /// component surfaced while a call was in flight, and lets a test append to the store at the exact
    /// moment the render pass is suspended on interop.
    /// </summary>
    private sealed class MutatingJsRuntime : IJSRuntime
    {
        private int _markdownCalls;

        public Action? OnMarkdownRender { get; set; }

        public List<Exception> Failures { get; } = new();

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "BotNexus.renderMarkdown")
            {
                // Yield first so the caller's enumeration is genuinely suspended before the mutation.
                await Task.Yield();
                if (Interlocked.Increment(ref _markdownCalls) == 1)
                {
                    try
                    {
                        OnMarkdownRender?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        Failures.Add(ex);
                    }
                }

                var source = args is { Length: > 0 } && args[0] is string s ? s : string.Empty;
                if (typeof(TValue) == typeof(string))
                    return (TValue)(object)$"<p><strong>MD:{source}</strong></p>";
            }

            return default!;
        }
    }
}
