using BotNexus.Tests.E2E.Infrastructure;
using FluentAssertions;

namespace BotNexus.Tests.E2E.Tests;

/// <summary>Scenario 1: Single agent Q&A via MockWebChannel.</summary>
[Collection(MultiAgentE2eCollection.Name)]
public sealed class SingleAgentQaTests(MultiAgentFixture fixture) : IAsyncLifetime
{
    [Fact]
    public async Task Nova_ReceivesPizzaQuestion_ReturnsRelevantResponse()
    {
        var chatId = $"qa-{Guid.NewGuid():N}";

        await fixture.SendMessageAsync("nova", "What are the best pizzas?", fixture.WebChannel, chatId);

        var response = await fixture.WebChannel.WaitForResponseAsync(chatId);

        response.Content.Should().Contain("pizza");
        response.Content.Should().ContainAny("Margherita", "New York", "Neapolitan");
        response.ChatId.Should().Be(chatId);
        response.Channel.Should().Be("mock-web");
    }

    [Fact]
    public async Task Nova_ReceivesCaliforniaPizzaQuestion_ReturnsCaliforniaSpecificResponse()
    {
        var chatId = $"qa-cali-{Guid.NewGuid():N}";

        await fixture.SendMessageAsync("nova", "What pizzas should I try in California?", fixture.WebChannel, chatId);

        var response = await fixture.WebChannel.WaitForResponseAsync(chatId);

        response.Content.Should().Contain("California");
        response.Content.Should().ContainAny("Pizzeria Mozza", "Tony's Pizza");
    }

    public Task InitializeAsync()
    {
        fixture.WebChannel.Reset();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;
}
