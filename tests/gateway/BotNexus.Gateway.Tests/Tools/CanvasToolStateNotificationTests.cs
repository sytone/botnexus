using System.Text.Json;
using BotNexus.Agent.Core.Types;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Tools;
using NSubstitute;
using Xunit;

namespace BotNexus.Gateway.Tests.Tools;

public sealed class CanvasToolStateNotificationTests
{
    private static CanvasTool CreateTool(
        IConversationStore? store = null,
        IAgentCanvasNotifier? notifier = null)
    {
        var agentId = AgentId.From("test-agent");
        var conversationId = ConversationId.From("c_test123");
        var notifiers = notifier is not null
            ? new List<IAgentCanvasNotifier> { notifier }
            : new List<IAgentCanvasNotifier>();
        return new CanvasTool(agentId, conversationId, store, notifiers);
    }

    [Fact]
    public async Task SetState_Success_FiresCanvasStateChanged()
    {
        var store = Substitute.For<IConversationStore>();
        store.SetCanvasStateKeyAsync(
            ConversationId.From("c_test123"), "counter", Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        var notifier = Substitute.For<IAgentCanvasNotifier>();
        var tool = CreateTool(store, notifier);

        var args = new Dictionary<string, object?>
        {
            ["action"] = JsonDocument.Parse("\"set_state\"").RootElement,
            ["key"] = JsonDocument.Parse("\"counter\"").RootElement,
            ["value"] = JsonDocument.Parse("42").RootElement
        };

        await tool.ExecuteAsync("tc1", args, CancellationToken.None);

        await notifier.Received(1).NotifyCanvasStateChangedAsync(
            "c_test123", "counter", Arg.Any<object?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetState_Failure_DoesNotFireEvent()
    {
        var store = Substitute.For<IConversationStore>();
        store.SetCanvasStateKeyAsync(
            ConversationId.From("c_test123"), "counter", Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));

        var notifier = Substitute.For<IAgentCanvasNotifier>();
        var tool = CreateTool(store, notifier);

        var args = new Dictionary<string, object?>
        {
            ["action"] = JsonDocument.Parse("\"set_state\"").RootElement,
            ["key"] = JsonDocument.Parse("\"counter\"").RootElement,
            ["value"] = JsonDocument.Parse("42").RootElement
        };

        await tool.ExecuteAsync("tc1", args, CancellationToken.None);

        await notifier.DidNotReceive().NotifyCanvasStateChangedAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ClearState_FiresCanvasStateChangedWithWildcard()
    {
        var store = Substitute.For<IConversationStore>();
        var notifier = Substitute.For<IAgentCanvasNotifier>();
        var tool = CreateTool(store, notifier);

        var args = new Dictionary<string, object?>
        {
            ["action"] = JsonDocument.Parse("\"clear_state\"").RootElement
        };

        await tool.ExecuteAsync("tc1", args, CancellationToken.None);

        await notifier.Received(1).NotifyCanvasStateChangedAsync(
            "c_test123", "*", null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetState_DoesNotFireEvent()
    {
        var store = Substitute.For<IConversationStore>();
        store.GetCanvasStateAsync(ConversationId.From("c_test123"), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, JsonElement>());

        var notifier = Substitute.For<IAgentCanvasNotifier>();
        var tool = CreateTool(store, notifier);

        var args = new Dictionary<string, object?>
        {
            ["action"] = JsonDocument.Parse("\"get_state\"").RootElement
        };

        await tool.ExecuteAsync("tc1", args, CancellationToken.None);

        await notifier.DidNotReceive().NotifyCanvasStateChangedAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetState_NoConversationId_DoesNotFireEvent()
    {
        var store = Substitute.For<IConversationStore>();
        var notifier = Substitute.For<IAgentCanvasNotifier>();

        // Create tool with no conversationId
        var tool = new CanvasTool(
            AgentId.From("test"),
            conversationId: null,
            store,
            new List<IAgentCanvasNotifier> { notifier });

        var args = new Dictionary<string, object?>
        {
            ["action"] = JsonDocument.Parse("\"set_state\"").RootElement,
            ["key"] = JsonDocument.Parse("\"k\"").RootElement,
            ["value"] = JsonDocument.Parse("1").RootElement
        };

        await tool.ExecuteAsync("tc1", args, CancellationToken.None);

        await notifier.DidNotReceive().NotifyCanvasStateChangedAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>());
    }
}
