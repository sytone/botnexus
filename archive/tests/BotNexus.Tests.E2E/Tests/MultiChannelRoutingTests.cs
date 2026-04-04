using BotNexus.Tests.E2E.Infrastructure;
using FluentAssertions;

namespace BotNexus.Tests.E2E.Tests;

/// <summary>Scenario 4: Multi-channel routing — same question via both channels.</summary>
[Collection(MultiAgentE2eCollection.Name)]
public sealed class MultiChannelRoutingTests(MultiAgentFixture fixture) : IAsyncLifetime
{
    [Fact]
    public async Task SameQuestion_ViaBothChannels_BothGetResponses()
    {
        var webChatId = $"multi-web-{Guid.NewGuid():N}";
        var apiChatId = $"multi-api-{Guid.NewGuid():N}";

        // Send the same question via MockWebChannel
        await fixture.SendMessageAsync(
            "nova",
            "What are the best pizzas?",
            fixture.WebChannel,
            webChatId);

        // Send the same question via MockApiChannel
        await fixture.SendMessageAsync(
            "nova",
            "What are the best pizzas?",
            fixture.ApiChannel,
            apiChatId);

        // Both channels should receive responses
        var webResponse = await fixture.WebChannel.WaitForResponseAsync(webChatId);
        var apiResponse = await fixture.ApiChannel.WaitForResponseAsync(apiChatId);

        // Both should have pizza-related content
        webResponse.Content.Should().ContainAny("pizza", "Margherita", "New York");
        apiResponse.Content.Should().ContainAny("pizza", "Margherita", "New York");

        // Responses should be routed to correct channels
        webResponse.Channel.Should().Be("mock-web");
        apiResponse.Channel.Should().Be("mock-api");

        // Chat IDs should match their respective requests
        webResponse.ChatId.Should().Be(webChatId);
        apiResponse.ChatId.Should().Be(apiChatId);
    }

    public Task InitializeAsync()
    {
        fixture.WebChannel.Reset();
        fixture.ApiChannel.Reset();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;
}
