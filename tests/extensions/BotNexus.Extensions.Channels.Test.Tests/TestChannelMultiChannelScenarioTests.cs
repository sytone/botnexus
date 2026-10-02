using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Events;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Extensions.Channels.Test.Tests;

public sealed class TestChannelMultiChannelScenarioTests
{
    [Fact]
    public async Task ResetActiveSessionAsync_TwoEligibleChannels_ProjectsStableConversationAndClearedSessionOncePerSurface()
    {
        await using var scenario = new TestChannelConversationScenario(
            TestChannelConversationScenario.Channel("telegram"),
            TestChannelConversationScenario.Channel("signalr"));

        scenario.Bind("telegram", "chat-reset");
        scenario.Bind("signalr", "portal-reset");

        var result = await scenario.ResetActiveSessionAsync();

        result.Reset.Outcome.ShouldBe(ConversationResetOutcome.Reset);
        result.Conversation.ConversationId.ShouldBe(scenario.ConversationId);
        result.Conversation.ActiveSessionId.ShouldBeNull();
        result.Session.Status.ShouldBe(SessionStatus.Sealed);

        AssertResetProjection(scenario.LifecycleEvents("telegram", "chat-reset"), scenario.ConversationId, result);
        AssertResetProjection(scenario.LifecycleEvents("signalr", "portal-reset"), scenario.ConversationId, result);
    }

    [Fact]
    public async Task PublishCompactionAsync_TwoEligibleChannels_ProjectsPersistedLifecycleEventOncePerSurface()
    {
        await using var scenario = new TestChannelConversationScenario(
            TestChannelConversationScenario.Channel("telegram"),
            TestChannelConversationScenario.Channel("signalr"));

        scenario.Bind("telegram", "chat-lifecycle");
        scenario.Bind("signalr", "portal-lifecycle");

        var persisted = await scenario.PublishCompactionAsync();

        persisted.Role.ShouldBe(MessageRole.Notification);
        persisted.Content.ShouldContain("Session context compacted");

        var telegram = scenario.LifecycleEvents("telegram", "chat-lifecycle")
            .ShouldHaveSingleItem()
            .ConversationEvent.ShouldBeOfType<ConversationSessionItemPersistedEvent>();
        telegram.Item.ShouldBe(persisted);

        var signalR = scenario.LifecycleEvents("signalr", "portal-lifecycle")
            .ShouldHaveSingleItem()
            .ConversationEvent.ShouldBeOfType<ConversationSessionItemPersistedEvent>();
        signalR.Item.ShouldBe(persisted);
    }

    private static void AssertResetProjection(
        IReadOnlyList<TestChannelLifecycleEventRecord> events,
        ConversationId conversationId,
        TestChannelConversationScenario.ResetScenarioResult result)
    {
        var projected = events.ShouldHaveSingleItem()
            .ConversationEvent.ShouldBeOfType<ConversationActiveSessionChangedEvent>();
        projected.ConversationId.ShouldBe(conversationId);
        projected.PreviousSessionId.ShouldBe(result.Reset.SealedSessionId);
        projected.ActiveSessionId.ShouldBeNull();
        projected.SessionId.ShouldBe(result.Reset.SealedSessionId);
    }

    [Fact]
    public async Task PublishAsync_TwoEligibleChannels_ProjectsOncePerSurfaceWithoutCorrelationLeakage()
    {
        await using var scenario = new TestChannelConversationScenario(
            TestChannelConversationScenario.Channel("telegram"),
            TestChannelConversationScenario.Channel("signalr"));

        var telegramBinding = scenario.Bind("telegram", "chat-100");
        scenario.Bind("signalr", "portal-100");
        scenario.Bind("telegram", "muted-chat", BindingMode.Muted);
        scenario.Bind("signalr", "other-conversation", BindingMode.Interactive, ConversationId.Create());

        await scenario.PublishAsync(
            originBindingId: telegramBinding,
            correlationId: "request-7",
            new AgentStreamEvent
            {
                Type = AgentStreamEventType.ContentDelta,
                ContentDelta = "hello",
            });

        var telegram = scenario.Events("telegram", "chat-100").ShouldHaveSingleItem();
        telegram.ChannelRequestId.ShouldBe("request-7");
        telegram.StreamEvent.ContentDelta.ShouldBe("hello");

        var signalR = scenario.Events("signalr", "portal-100").ShouldHaveSingleItem();
        signalR.ChannelRequestId.ShouldBeNull();
        signalR.StreamEvent.ContentDelta.ShouldBe("hello");

        scenario.Events("telegram", "muted-chat").ShouldBeEmpty();
        scenario.Events("signalr", "other-conversation").ShouldBeEmpty();
    }
}
