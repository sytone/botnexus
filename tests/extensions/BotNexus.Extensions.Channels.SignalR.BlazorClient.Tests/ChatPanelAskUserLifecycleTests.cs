using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services.SlashCommands;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// #2792 AC4/AC5: what the user sees after clicking Submit. The reported symptom was a prompt
/// that stayed on screen after an accepted (but empty) answer, so the button read as dead. These
/// assert the rendered transcript, not just the service call.
/// </summary>
public sealed class ChatPanelAskUserLifecycleTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly ClientStateStore _store = new();
    private readonly IAgentInteractionService _interaction = Substitute.For<IAgentInteractionService>();

    public ChatPanelAskUserLifecycleTests()
    {
        _ctx.Services.AddSingleton<IClientStateStore>(_store);
        _ctx.Services.AddSingleton(_interaction);
        _ctx.Services.AddSingleton<ISlashCommandDispatcher>(new SlashCommandDispatcher(_interaction));
        _ctx.Services.AddSingleton(Substitute.For<IGatewayRestClient>());
        _ctx.Services.AddSingleton(new HttpClient());
        var preferences = Substitute.For<IPortalPreferencesService>();
        preferences.Current.Returns(new PortalPreferences { ArchiveConfirmEnabled = false });
        _ctx.Services.AddSingleton(preferences);
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    // AC4: accepting a submission removes the prompt from the RENDERED transcript, and the
    // answered value appears in its place.
    [Fact]
    public async Task Accepted_choice_or_free_form_submission_removes_the_prompt_from_the_transcript()
    {
        var cut = RenderWithPendingPrompt("ChoiceOrFreeForm");

        await cut.InvokeAsync(() => cut.FindAll("input[type='radio']")[1].Change(true));
        await cut.InvokeAsync(() => cut.Find(".ask-user-actions .send-btn").Click());

        await _interaction.Received(1).RespondToAskUserAsync(
            "conv-1",
            "req-1",
            null,
            Arg.Is<string[]?>(values => values != null && values.Contains("b")),
            false);

        Assert.Empty(cut.FindAll(".ask-user-prompt"));
        Assert.Null(_store.GetPendingAskUser("conv-1"));
        Assert.Contains(_store.GetMessages("conv-1"),
            message => message.Content.Contains("You answered", StringComparison.Ordinal));
    }

    // AC5: a failed submission leaves the prompt rendered and shows the error text - never a
    // silent no-op that looks like a dead button.
    [Fact]
    public async Task Failed_submission_keeps_the_prompt_rendered_and_shows_the_error()
    {
        _interaction
            .RespondToAskUserAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string[]?>(), Arg.Any<bool>())
            .ThrowsAsync(new InvalidOperationException("gateway unreachable"));

        var cut = RenderWithPendingPrompt("SingleChoice");

        await cut.InvokeAsync(() => cut.FindAll("input[type='radio']")[0].Change(true));
        await cut.InvokeAsync(() => cut.Find(".ask-user-actions .send-btn").Click());

        cut.Find(".ask-user-prompt");
        Assert.NotNull(_store.GetPendingAskUser("conv-1"));
        Assert.Contains("gateway unreachable", cut.Find(".ask-user-error").TextContent, StringComparison.Ordinal);
    }

    private IRenderedComponent<ChatPanel> RenderWithPendingPrompt(string inputType)
    {
        _store.UpsertAgent(new AgentState { AgentId = "agent-1", DisplayName = "Test Agent", IsConnected = true });
        _store.SeedConversations("agent-1", [MakeConvDto("conv-1", "agent-1")]);
        _store.SetActiveConversation("agent-1", "conv-1");
        _store.SetPendingAskUser(new AskUserPromptState
        {
            RequestId = "req-1",
            ConversationId = "conv-1",
            Prompt = "Merge to main and deploy the portal now?",
            InputType = inputType,
            Choices =
            [
                new AskUserChoiceState("a", "Yes, deploy", null),
                new AskUserChoiceState("b", "No, hold", null)
            ]
        });

        return _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));
    }

    private static ConversationSummaryDto MakeConvDto(string convId, string agentId) =>
        new(
            ConversationId: convId,
            AgentId: agentId,
            Title: "Test Conv",
            IsDefault: false,
            Status: "Active",
            ActiveSessionId: null,
            BindingCount: 0,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            Kind: "HumanAgent",
            Source: "Channel",
            IsPinned: false);
}
