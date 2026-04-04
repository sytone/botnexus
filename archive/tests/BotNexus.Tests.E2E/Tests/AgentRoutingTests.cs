using BotNexus.Tests.E2E.Infrastructure;
using FluentAssertions;

namespace BotNexus.Tests.E2E.Tests;

/// <summary>Scenario 8: Agent routing — messages addressed by name go to the correct agent.</summary>
[Collection(MultiAgentE2eCollection.Name)]
public sealed class AgentRoutingTests(MultiAgentFixture fixture) : IAsyncLifetime
{
    [Fact]
    public async Task Message_AddressedToNova_IsHandledByNova()
    {
        var chatId = $"route-nova-{Guid.NewGuid():N}";

        await fixture.SendMessageAsync("nova", "What are the best pizzas?", fixture.WebChannel, chatId);

        var response = await fixture.WebChannel.WaitForResponseAsync(chatId);
        response.Content.Should().ContainAny("pizza", "Margherita", "New York");
    }

    [Fact]
    public async Task Message_AddressedToBolt_IsHandledByBolt()
    {
        var chatId = $"route-bolt-{Guid.NewGuid():N}";

        await fixture.SendMessageAsync("bolt", "What is 10 + 20?", fixture.WebChannel, chatId);

        var response = await fixture.WebChannel.WaitForResponseAsync(chatId);
        response.Content.Should().Contain("30");
    }

    [Fact]
    public async Task Message_AddressedToEcho_IsHandledByEcho()
    {
        var chatId = $"route-echo-{Guid.NewGuid():N}";
        var testMessage = "Route this to Echo please";

        await fixture.SendMessageAsync("echo", testMessage, fixture.WebChannel, chatId);

        var response = await fixture.WebChannel.WaitForResponseAsync(chatId);
        response.Content.Should().Be(testMessage);
    }

    [Fact]
    public async Task Message_AddressedToSage_IsHandledBySage()
    {
        var chatId = $"route-sage-{Guid.NewGuid():N}";

        await fixture.SendMessageAsync("sage", "Summarize: The quick brown fox jumps over the lazy dog", fixture.WebChannel, chatId);

        var response = await fixture.WebChannel.WaitForResponseAsync(chatId);
        response.Content.Should().StartWith("Summary:");
    }

    [Fact]
    public async Task Message_AddressedToQuill_IsHandledByQuill()
    {
        var chatId = $"route-quill-{Guid.NewGuid():N}";

        await fixture.SendMessageAsync("quill", "Remember: testing agent routing", fixture.WebChannel, chatId);

        var response = await fixture.WebChannel.WaitForResponseAsync(chatId);
        response.Content.Should().Contain("saved");
    }

    public Task InitializeAsync()
    {
        fixture.WebChannel.Reset();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;
}
