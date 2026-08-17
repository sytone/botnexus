using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services.SlashCommands;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Regression coverage for #2873: gateway-owned slash commands typed in the Blazor chat must be
/// dispatched to the gateway command pipeline (<c>POST /api/commands/execute</c>) and render the
/// returned <c>CommandResult</c>, instead of being delivered to the model as user message text.
/// </summary>
/// <remarks>
/// The defect was structural, not cosmetic: the portal's <see cref="IGatewayRestClient"/> had no
/// command-execute method at all, so <c>CommandsController</c> was unreachable from the client and
/// the registry's <c>SendToAgent</c> classification was the only thing it could do. These tests pin
/// the whole path - classification, dispatch, execution and every failure mode - so no single link
/// can silently regress to the paid-model-turn behaviour.
/// </remarks>
public sealed class GatewayCommandDispatchTests
{
    private const string AgentId = "agent-1";
    private const string ConversationId = "conv-1";

    private static (AgentInteractionService service, ClientStateStore store, IGatewayRestClient rest) CreateService(
        string? activeConversationId = "conv-1",
        string? activeSessionId = "sess-1")
    {
        var store = new ClientStateStore();
        var rest = Substitute.For<IGatewayRestClient>();
        var service = new AgentInteractionService(
            store,
            new GatewayHubConnection(),
            rest,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentInteractionService>.Instance);

        store.UpsertAgent(new AgentState { AgentId = AgentId, DisplayName = "Agent 1", IsConnected = true });
        var agent = store.GetAgent(AgentId)!;
        if (activeConversationId is not null)
        {
            agent.ActiveConversationId = activeConversationId;
            agent.Conversations[activeConversationId] = new ConversationState
            {
                ConversationId = activeConversationId,
                Status = "Active",
                ActiveSessionId = activeSessionId
            };
        }

        return (service, store, rest);
    }

    private static IReadOnlyList<ChatMessage> Messages(ClientStateStore store, string conversationId = "conv-1")
        => store.GetAgent(AgentId)!.Conversations[conversationId].Messages;

    // ── AC1/AC3: classification ───────────────────────────────────────────

    [Theory]
    [InlineData("/help")]
    [InlineData("/status")]
    [InlineData("/agents")]
    [InlineData("/context")]
    [InlineData("/model")]
    [InlineData("/reasoning")]
    public void Gateway_owned_commands_are_classified_as_GatewayCommand(string name)
    {
        var command = SlashCommandRegistry.All.Single(c => c.Name == name);

        Assert.Equal(SlashCommandKind.GatewayCommand, command.Kind);
    }

    [Fact]
    public void No_gateway_owned_command_is_still_classified_SendToAgent()
    {
        // #2873 fingerprint: SendToAgent on a gateway-owned command is the defect itself.
        var offenders = SlashCommandRegistry.All
            .Where(c => c.Kind == SlashCommandKind.SendToAgent)
            .Select(c => c.Name)
            .Where(n => n is "/help" or "/status" or "/agents" or "/context" or "/model" or "/reasoning")
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Client_side_command_kinds_are_unchanged()
    {
        // Behaviour-preservation guard: the fix must not reclassify the original quick actions.
        Assert.Equal(SlashCommandKind.ResetSession, SlashCommandRegistry.All.Single(c => c.Name == "/new").Kind);
        Assert.Equal(SlashCommandKind.CompactSession, SlashCommandRegistry.All.Single(c => c.Name == "/compact").Kind);
        Assert.Equal(SlashCommandKind.ClearLocalMessages, SlashCommandRegistry.All.Single(c => c.Name == "/clear").Kind);
    }

    [Fact]
    public void Prompts_stays_SendToAgent_because_no_contributor_declares_it()
    {
        // The issue body listed /prompts as gateway-owned. No ICommandContributor declares a
        // /prompts descriptor, so routing it to the pipeline would return "Unknown command".
        Assert.Equal(SlashCommandKind.SendToAgent, SlashCommandRegistry.All.Single(c => c.Name == "/prompts").Kind);
    }

    // ── AC2/AC6: dispatch does not reach the agent ────────────────────────

    [Fact]
    public async Task Dispatcher_routes_GatewayCommand_to_the_pipeline_and_never_to_SendMessageAsync()
    {
        var interaction = Substitute.For<IAgentInteractionService>();
        var sut = new SlashCommandDispatcher(interaction);
        var command = SlashCommandRegistry.All.Single(c => c.Name == "/status");

        var executed = await sut.ExecuteAsync(AgentId, ConversationId, command);

        Assert.True(executed);
        await interaction.Received(1).ExecuteGatewayCommandAsync(AgentId, ConversationId, "/status");
        await interaction.DidNotReceiveWithAnyArgs().SendMessageAsync(default!, default!, default!, default!);
    }

    [Theory]
    [InlineData("/help")]
    [InlineData("/status")]
    [InlineData("/agents")]
    [InlineData("/context")]
    [InlineData("/model")]
    [InlineData("/reasoning")]
    public async Task No_gateway_command_consumes_a_model_turn(string name)
    {
        var interaction = Substitute.For<IAgentInteractionService>();
        var sut = new SlashCommandDispatcher(interaction);

        await sut.ExecuteAsync(AgentId, ConversationId, SlashCommandRegistry.All.Single(c => c.Name == name));

        await interaction.DidNotReceiveWithAnyArgs().SendMessageAsync(default!, default!, default!, default!);
        await interaction.DidNotReceiveWithAnyArgs()
            .SendMessageAsync(default!, default!, default!, default!);
        await interaction.Received(1).ExecuteGatewayCommandAsync(AgentId, ConversationId, name);
    }

    [Fact]
    public async Task SendToAgent_commands_still_reach_the_agent()
    {
        // Sad-path complement: the fix must not break the genuinely model-answered entries.
        var interaction = Substitute.For<IAgentInteractionService>();
        var sut = new SlashCommandDispatcher(interaction);

        await sut.ExecuteAsync(AgentId, ConversationId, SlashCommandRegistry.All.Single(c => c.Name == "/prompts"));

        await interaction.Received(1).SendMessageAsync(AgentId, ConversationId, "/prompts");
        await interaction.DidNotReceiveWithAnyArgs().ExecuteGatewayCommandAsync(default!, default!, default!);
    }

    [Fact]
    public async Task Protected_gateway_command_denied_by_hook_never_reaches_the_pipeline()
    {
        var interaction = Substitute.For<IAgentInteractionService>();
        var hook = Substitute.For<ISlashCommandApprovalHook>();
        var command = new SlashCommand("/status", "d", SlashCommandKind.GatewayCommand, RequiresApproval: true);
        hook.IsApprovedAsync(AgentId, command).Returns(false);

        var executed = await new SlashCommandDispatcher(interaction, hook).ExecuteAsync(AgentId, ConversationId, command);

        Assert.False(executed);
        await interaction.DidNotReceiveWithAnyArgs().ExecuteGatewayCommandAsync(default!, default!, default!);
    }

    // ── AC1: happy path renders the CommandResult ─────────────────────────

    [Fact]
    public async Task Successful_command_posts_to_the_pipeline_and_renders_the_result()
    {
        var (service, store, rest) = CreateService();
        rest.ExecuteCommandAsync(Arg.Any<CommandExecuteRequestDto>(), Arg.Any<CancellationToken>())
            .Returns(new CommandResultDto("Gateway Status", "Uptime: 3h", false));

        var ok = await service.ExecuteGatewayCommandAsync(AgentId, ConversationId, "/status");

        Assert.True(ok);
        var last = Messages(store)[^1];
        Assert.Equal("System", last.Role);
        Assert.Contains("Gateway Status", last.Content, StringComparison.Ordinal);
        Assert.Contains("Uptime: 3h", last.Content, StringComparison.Ordinal);
        Assert.Equal(AgentInteractionService.GatewayCommandKind, last.Kind);
    }

    [Fact]
    public async Task Request_carries_the_agent_and_active_session_ids()
    {
        // Session-scoped commands (/context, /model, /reasoning) are meaningless without these,
        // so the wire contract is pinned rather than just "some request was sent".
        var (service, _, rest) = CreateService(activeSessionId: "sess-42");
        rest.ExecuteCommandAsync(Arg.Any<CommandExecuteRequestDto>(), Arg.Any<CancellationToken>())
            .Returns(new CommandResultDto("Context", "ok", false));

        await service.ExecuteGatewayCommandAsync(AgentId, ConversationId, "/context");

        await rest.Received(1).ExecuteCommandAsync(
            Arg.Is<CommandExecuteRequestDto>(r =>
                r.Input == "/context" && r.AgentId == AgentId && r.SessionId == "sess-42"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task No_user_message_row_is_appended_for_a_gateway_command()
    {
        var (service, store, rest) = CreateService();
        rest.ExecuteCommandAsync(Arg.Any<CommandExecuteRequestDto>(), Arg.Any<CancellationToken>())
            .Returns(new CommandResultDto("Gateway Status", "ok", false));

        await service.ExecuteGatewayCommandAsync(AgentId, ConversationId, "/status");

        Assert.DoesNotContain(Messages(store), m => m.Role == "User");
    }

    // ── AC4: rejections are visible, never a silent fall-through ──────────

    [Fact]
    public async Task Error_result_from_the_pipeline_is_rendered_as_an_error_row()
    {
        var (service, store, rest) = CreateService();
        rest.ExecuteCommandAsync(Arg.Any<CommandExecuteRequestDto>(), Arg.Any<CancellationToken>())
            .Returns(new CommandResultDto("Command Not Found", "Unknown command: /nope", true));

        var ok = await service.ExecuteGatewayCommandAsync(AgentId, ConversationId, "/nope");

        Assert.False(ok);
        var last = Messages(store)[^1];
        Assert.Equal("Error", last.Role);
        Assert.Contains("Unknown command: /nope", last.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Null_response_surfaces_a_visible_rejection()
    {
        var (service, store, rest) = CreateService();
        rest.ExecuteCommandAsync(Arg.Any<CommandExecuteRequestDto>(), Arg.Any<CancellationToken>())
            .Returns((CommandResultDto?)null);

        var ok = await service.ExecuteGatewayCommandAsync(AgentId, ConversationId, "/status");

        Assert.False(ok);
        Assert.Equal("Error", Messages(store)[^1].Role);
    }

    [Fact]
    public async Task Transport_exception_surfaces_a_visible_error_and_does_not_throw()
    {
        var (service, store, rest) = CreateService();
        rest.ExecuteCommandAsync(Arg.Any<CommandExecuteRequestDto>(), Arg.Any<CancellationToken>())
            .Returns<Task<CommandResultDto?>>(_ => throw new HttpRequestException("boom"));

        var ok = await service.ExecuteGatewayCommandAsync(AgentId, ConversationId, "/status");

        Assert.False(ok);
        var last = Messages(store)[^1];
        Assert.Equal("Error", last.Role);
        Assert.Contains("boom", last.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_active_conversation_reports_an_error_and_calls_nothing()
    {
        var (service, _, rest) = CreateService(activeConversationId: null);

        var ok = await service.ExecuteGatewayCommandAsync(AgentId, ConversationId, "/status");

        Assert.False(ok);
        await rest.DidNotReceiveWithAnyArgs().ExecuteCommandAsync(default!, default);
    }

    [Fact]
    public async Task Blank_command_text_is_rejected_before_any_request()
    {
        var (service, _, rest) = CreateService();

        var ok = await service.ExecuteGatewayCommandAsync(AgentId, ConversationId, "   ");

        Assert.False(ok);
        await rest.DidNotReceiveWithAnyArgs().ExecuteCommandAsync(default!, default);
    }
}
