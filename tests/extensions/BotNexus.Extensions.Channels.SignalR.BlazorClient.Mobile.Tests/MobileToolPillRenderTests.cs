using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile.Pages;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Tests for issue #1483: the mobile chat message loop must not throw a Blazor render
/// exception when the message list mutates mid-stream and a tool-call pill replaces a
/// text bubble at the same tree position.
///
/// Two compounding defects caused the crash on <c>main</c>:
///  1. The message <c>@foreach</c> had no <c>@key</c>, so Blazor diffed by position.
///  2. The tool-call branch rendered a <c>&lt;button&gt;</c> root while every other
///     branch rendered a <c>&lt;div&gt;</c>, so an in-place element-type swap was attempted.
///
/// The fix keys the loop by <see cref="ChatMessage.Id"/> and makes the tool pill a
/// <c>&lt;div role="button"&gt;</c> so the renderer only ever diffs <c>div</c>→<c>div</c>.
/// These tests pin both the render-stability invariant and the element-type contract.
/// </summary>
public sealed class MobileToolPillRenderTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly IClientStateStore _store;
    private readonly IPortalLoadService _portalLoad;
    private readonly IAgentInteractionService _interaction;

    public MobileToolPillRenderTests()
    {
        _store = Substitute.For<IClientStateStore>();
        _portalLoad = Substitute.For<IPortalLoadService>();
        _interaction = Substitute.For<IAgentInteractionService>();

        _portalLoad.IsReady.Returns(true);
        _portalLoad.IsLoading.Returns(false);
        _portalLoad.LoadError.Returns((string?)null);
        _portalLoad.InitializeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var agent = new AgentState
        {
            AgentId = "agent-1",
            DisplayName = "Alpha",
            ActiveConversationId = "conv-1",
            IsConnected = true
        };
        agent.Conversations["conv-1"] = new ConversationState { ConversationId = "conv-1", Title = "C" };
        _store.Agents.Returns(new Dictionary<string, AgentState> { ["agent-1"] = agent }.AsReadOnly());
        _store.ActiveAgentId.Returns("agent-1");
        _store.GetAgent("agent-1").Returns(agent);
        _store.GetStreamState(Arg.Any<string>()).Returns(new ConversationStreamState());

        _ctx.Services.AddSingleton(_store);
        _ctx.Services.AddSingleton(_portalLoad);
        _ctx.Services.AddSingleton(_interaction);
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void Tool_call_message_renders_as_keyed_div_not_button_root()
    {
        var messages = new List<ChatMessage>
        {
            new("assistant", "Working on it", DateTimeOffset.UtcNow),
            new("tool", string.Empty, DateTimeOffset.UtcNow)
            {
                IsToolCall = true,
                ToolName = "read",
                ToolResult = "ok"
            }
        };
        _store.GetMessages("conv-1").Returns(messages.AsReadOnly());

        var cut = _ctx.Render<Chat>(p => p.Add(c => c.AgentId, "agent-1"));

        // The tool pill renders...
        var pill = cut.Find(".tool-pill");
        // ...as a <div> root, NOT a <button>, matching its <div> siblings so the renderer
        // never has to patch a <div> into a <button> at the same position.
        Assert.Equal("DIV", pill.TagName);
        // ...and remains keyboard-activatable / a11y-labelled as a button surrogate.
        Assert.Equal("button", pill.GetAttribute("role"));
        Assert.Equal("0", pill.GetAttribute("tabindex"));
        Assert.Contains("read", cut.Markup);
    }

    [Fact]
    public void Message_list_transition_from_text_to_tool_call_does_not_throw()
    {
        // Start: a single streaming assistant text bubble (<div>).
        var initial = new List<ChatMessage>
        {
            new("assistant", "Let me check", DateTimeOffset.UtcNow)
        };
        _store.GetMessages("conv-1").Returns(initial.AsReadOnly());

        var cut = _ctx.Render<Chat>(p => p.Add(c => c.AgentId, "agent-1"));
        Assert.Contains("Let me check", cut.Markup);

        // Mid-stream mutation: a tool-call is inserted at the front and the text bubble
        // moves down. Without @key this reorders an element-type boundary in place; with
        // the fix both branches are <div> and keyed, so the re-render must not throw.
        var mutated = new List<ChatMessage>
        {
            new("tool", string.Empty, DateTimeOffset.UtcNow)
            {
                IsToolCall = true,
                ToolName = "grep"
            },
            initial[0]
        };
        _store.GetMessages("conv-1").Returns(mutated.AsReadOnly());

        // Re-render via a store change; must complete without a render exception.
        var ex = Record.Exception(() => _store.OnChanged += Raise.Event<Action>());
        Assert.Null(ex);
        // The newly-inserted tool pill renders, and the message list still shows two
        // message bubbles (a tool pill + the text bubble) after the in-place reorder.
        Assert.Contains("grep", cut.Markup);
        Assert.Contains("tool-pill", cut.Markup);
        Assert.Single(cut.FindAll(".tool-pill"));
        Assert.NotEmpty(cut.FindAll(".message"));
    }

    [Fact]
    public void Mixed_boundary_text_and_tool_messages_render_without_throwing()
    {
        var messages = new List<ChatMessage>
        {
            new("system", string.Empty, DateTimeOffset.UtcNow) { Kind = "boundary", BoundaryLabel = "New session" },
            new("user", "hello", DateTimeOffset.UtcNow),
            new("assistant", "hi", DateTimeOffset.UtcNow),
            new("tool", string.Empty, DateTimeOffset.UtcNow)
            {
                IsToolCall = true,
                ToolName = "ls",
                ToolIsError = true
            }
        };
        _store.GetMessages("conv-1").Returns(messages.AsReadOnly());

        var ex = Record.Exception(() => _ctx.Render<Chat>(p => p.Add(c => c.AgentId, "agent-1")));
        Assert.Null(ex);
    }
}
