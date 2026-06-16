using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

using NSubstitute;
using Xunit;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Tests for the per-conversation todo live update + REST hydration wiring (#1464 step 5).
/// Mirrors <see cref="CanvasPerConversationTests"/>: the <c>TodoUpdated</c> SignalR event routes
/// the raw TodoJson to the target conversation, and selecting a conversation hydrates the todo
/// state from REST when not already loaded.
/// </summary>
public sealed class TodoPerConversationTests
{
    private const string SampleTodoJson =
        "{\"items\":[{\"id\":\"a\",\"text\":\"First\",\"status\":\"done\"},{\"id\":\"b\",\"text\":\"Second\",\"status\":\"pending\"}]}";

    private readonly ClientStateStore _store = new();
    private readonly IGatewayRestClient _restClient = Substitute.For<IGatewayRestClient>();
    private readonly GatewayHubConnection _hub = new();
    private readonly GatewayEventHandler _handler;
    private readonly AgentInteractionService _interaction;

    public TodoPerConversationTests()
    {
        _handler = new GatewayEventHandler(_store, _hub);
        _interaction = new AgentInteractionService(_store, _hub, _restClient);
    }

    private AgentState SeedAgent(string agentId)
    {
        _store.SeedAgents([new AgentSummary(agentId, "Test Agent")]);
        return _store.GetAgent(agentId)!;
    }

    private static ConversationState SeedConversation(AgentState agent, string convId)
    {
        var conv = new ConversationState
        {
            ConversationId = convId,
            Title = "Test",
            IsDefault = false,
            Status = "Active",
        };
        agent.Conversations[convId] = conv;
        return conv;
    }

    // ── HandleTodoUpdated routes to conversation ──────────────────────────

    [Fact]
    public void HandleTodoUpdated_RoutesToConversation()
    {
        var agent = SeedAgent("agent-1");
        var conv = SeedConversation(agent, "conv-1");

        _handler.HandleTodoUpdated("agent-1", "conv-1", SampleTodoJson);

        Assert.Equal(SampleTodoJson, conv.TodoJson);
        Assert.NotNull(conv.TodoUpdatedAt);
    }

    [Fact]
    public void HandleTodoUpdated_EmptyPayload_ClearsConversationTodo()
    {
        var agent = SeedAgent("agent-2");
        var conv = SeedConversation(agent, "conv-2");
        conv.TodoJson = SampleTodoJson;

        _handler.HandleTodoUpdated("agent-2", "conv-2", "");

        Assert.Null(conv.TodoJson);
    }

    [Fact]
    public void HandleTodoUpdated_NullPayload_ClearsConversationTodo()
    {
        var agent = SeedAgent("agent-2b");
        var conv = SeedConversation(agent, "conv-2b");
        conv.TodoJson = SampleTodoJson;

        _handler.HandleTodoUpdated("agent-2b", "conv-2b", null);

        Assert.Null(conv.TodoJson);
    }

    [Fact]
    public void HandleTodoUpdated_UnknownConversation_DoesNotThrow()
    {
        SeedAgent("agent-3");

        _handler.HandleTodoUpdated("agent-3", "unknown-conv", SampleTodoJson);
    }

    [Fact]
    public void HandleTodoUpdated_UnknownAgent_DoesNotThrow()
    {
        _handler.HandleTodoUpdated("unknown-agent", "conv-1", SampleTodoJson);
    }

    [Fact]
    public void HandleTodoUpdated_SwitchConversations_EachHasOwnTodo()
    {
        var agent = SeedAgent("agent-4");
        var convA = SeedConversation(agent, "conv-a");
        var convB = SeedConversation(agent, "conv-b");

        _handler.HandleTodoUpdated("agent-4", "conv-a", "{\"items\":[{\"id\":\"x\",\"text\":\"A\",\"status\":\"pending\"}]}");
        _handler.HandleTodoUpdated("agent-4", "conv-b", "{\"items\":[{\"id\":\"y\",\"text\":\"B\",\"status\":\"done\"}]}");

        Assert.Contains("\"A\"", convA.TodoJson);
        Assert.Contains("\"B\"", convB.TodoJson);
    }

    // ── SelectConversation hydrates todo from REST ────────────────────────

    [Fact]
    public async Task SelectConversationAsync_FetchesTodoFromRest_WhenTodoIsNull()
    {
        var agent = SeedAgent("agent-5");
        var conv = SeedConversation(agent, "conv-5");

        _restClient.GetConversationTodoAsync("agent-5", "conv-5", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(SampleTodoJson));

        await _interaction.SelectConversationAsync("agent-5", "conv-5");

        Assert.Equal(SampleTodoJson, conv.TodoJson);
    }

    [Fact]
    public async Task SelectConversationAsync_DoesNotOverwriteExistingTodo()
    {
        var agent = SeedAgent("agent-6");
        var conv = SeedConversation(agent, "conv-6");
        conv.TodoJson = SampleTodoJson;

        await _interaction.SelectConversationAsync("agent-6", "conv-6");

        await _restClient.DidNotReceive().GetConversationTodoAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.Equal(SampleTodoJson, conv.TodoJson);
    }
}
