using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Extensions.Channels.Test.Tests;

public sealed class TestChannelMultiChannelScenarioTests
{
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
