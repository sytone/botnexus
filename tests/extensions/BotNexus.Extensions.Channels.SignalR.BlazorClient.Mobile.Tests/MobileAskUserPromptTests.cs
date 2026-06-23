using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile.Pages;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Tests for issue #1491 (ask_user durability Step 4/5): the mobile chat page must
/// render a pending <c>ask_user</c> prompt and route the response through the same
/// <see cref="IAgentInteractionService.RespondToAskUserAsync"/> path the desktop uses.
/// Before this work a mobile user blocked on an <c>ask_user</c> prompt saw nothing.
/// </summary>
public sealed class MobileAskUserPromptTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly IClientStateStore _store;
    private readonly IPortalLoadService _portalLoad;
    private readonly IAgentInteractionService _interaction;

    public MobileAskUserPromptTests()
    {
        _store = Substitute.For<IClientStateStore>();
        _portalLoad = Substitute.For<IPortalLoadService>();
        _interaction = Substitute.For<IAgentInteractionService>();

        _portalLoad.IsReady.Returns(true);
        _portalLoad.IsSignalRConnected.Returns(true);
        _portalLoad.LoadError.Returns((string?)null);
        _portalLoad.InitializeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _store.GetStreamState(Arg.Any<string>()).Returns(new ConversationStreamState());
        _store.GetMessages(Arg.Any<string>()).Returns(new List<ChatMessage>().AsReadOnly());
        _store.GetPendingAskUser(Arg.Any<string>()).Returns((AskUserPromptState?)null);

        _ctx.Services.AddSingleton(_store);
        _ctx.Services.AddSingleton(_portalLoad);
        _ctx.Services.AddSingleton(_interaction);
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    private AgentState ArrangeActiveConversation(string agentId = "agent-1", string convId = "conv-1")
    {
        var agent = new AgentState
        {
            AgentId = agentId,
            DisplayName = "Alpha",
            ActiveConversationId = convId,
            IsConnected = true
        };
        agent.Conversations[convId] = new ConversationState { ConversationId = convId, Title = "C" };
        _store.Agents.Returns(new Dictionary<string, AgentState> { [agentId] = agent }.AsReadOnly());
        _store.ActiveAgentId.Returns(agentId);
        _store.GetAgent(agentId).Returns(agent);
        return agent;
    }

    private static AskUserPromptState FreeFormPrompt(string convId = "conv-1") => new()
    {
        RequestId = "req-1",
        ConversationId = convId,
        Prompt = "What is your name?",
        InputType = "FreeForm",
        AllowFreeForm = true
    };

    // -- Render -------------------------------------------------------------

    [Fact]
    public void Renders_ask_user_prompt_when_a_pending_prompt_exists_for_the_active_conversation()
    {
        ArrangeActiveConversation();
        _store.GetPendingAskUser("conv-1").Returns(FreeFormPrompt());

        var cut = _ctx.Render<Chat>(p => p.Add(c => c.AgentId, "agent-1"));

        // The shared AskUserPrompt component renders with its data-testid + the prompt text.
        Assert.Contains("ask-user-prompt", cut.Markup);
        Assert.Contains("What is your name?", cut.Markup);
    }

    [Fact]
    public void Does_not_render_ask_user_prompt_when_no_pending_prompt()
    {
        ArrangeActiveConversation();
        _store.GetPendingAskUser("conv-1").Returns((AskUserPromptState?)null);

        var cut = _ctx.Render<Chat>(p => p.Add(c => c.AgentId, "agent-1"));

        Assert.DoesNotContain("ask-user-prompt", cut.Markup);
        // The normal message input remains available.
        Assert.Contains("input-textarea", cut.Markup);
    }

    [Fact]
    public void Renders_single_choice_options_as_radio_inputs()
    {
        ArrangeActiveConversation();
        var prompt = new AskUserPromptState
        {
            RequestId = "req-sc",
            ConversationId = "conv-1",
            Prompt = "Pick one",
            InputType = "SingleChoice",
            Choices = new List<AskUserChoiceState>
            {
                new("a", "Option A", null),
                new("b", "Option B", null)
            }
        };
        _store.GetPendingAskUser("conv-1").Returns(prompt);

        var cut = _ctx.Render<Chat>(p => p.Add(c => c.AgentId, "agent-1"));

        Assert.Contains("Pick one", cut.Markup);
        Assert.Contains("Option A", cut.Markup);
        Assert.Contains("Option B", cut.Markup);
        Assert.Contains("type=\"radio\"", cut.Markup);
    }

    [Fact]
    public void Renders_multiple_choice_options_as_checkbox_inputs()
    {
        ArrangeActiveConversation();
        var prompt = new AskUserPromptState
        {
            RequestId = "req-mc",
            ConversationId = "conv-1",
            Prompt = "Pick some",
            InputType = "MultipleChoice",
            AllowMultiple = true,
            Choices = new List<AskUserChoiceState>
            {
                new("x", "Option X", null),
                new("y", "Option Y", null)
            }
        };
        _store.GetPendingAskUser("conv-1").Returns(prompt);

        var cut = _ctx.Render<Chat>(p => p.Add(c => c.AgentId, "agent-1"));

        Assert.Contains("Pick some", cut.Markup);
        Assert.Contains("type=\"checkbox\"", cut.Markup);
    }

    // -- Submit -------------------------------------------------------------

    [Fact]
    public void Submitting_a_free_form_answer_routes_through_RespondToAskUserAsync()
    {
        ArrangeActiveConversation();
        _store.GetPendingAskUser("conv-1").Returns(FreeFormPrompt());
        _interaction.RespondToAskUserAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string[]?>(), Arg.Any<bool>())
            .Returns(Task.CompletedTask);

        var cut = _ctx.Render<Chat>(p => p.Add(c => c.AgentId, "agent-1"));

        cut.Find("textarea.ask-user-free-form").Input("Farnsworth");
        cut.Find("[data-testid=\"ask-user-submit\"]").Click();

        _interaction.Received(1).RespondToAskUserAsync(
            "conv-1", "req-1", "Farnsworth", null, false);
        _store.Received().ClearPendingAskUser("conv-1");
    }

    [Fact]
    public void Cancelling_routes_a_cancelled_submission_through_RespondToAskUserAsync()
    {
        ArrangeActiveConversation();
        _store.GetPendingAskUser("conv-1").Returns(FreeFormPrompt());
        _interaction.RespondToAskUserAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string[]?>(), Arg.Any<bool>())
            .Returns(Task.CompletedTask);

        var cut = _ctx.Render<Chat>(p => p.Add(c => c.AgentId, "agent-1"));

        cut.Find("[data-testid=\"ask-user-cancel\"]").Click();

        _interaction.Received(1).RespondToAskUserAsync(
            "conv-1", "req-1", null, null, true);
    }
}
