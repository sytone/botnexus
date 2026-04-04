using BotNexus.Tests.E2E.Infrastructure;
using FluentAssertions;

namespace BotNexus.Tests.E2E.Tests;

/// <summary>Scenario 7: Concurrent agents — Nova and Bolt simultaneously with no cross-talk.</summary>
[Collection(MultiAgentE2eCollection.Name)]
public sealed class ConcurrentAgentTests(MultiAgentFixture fixture) : IAsyncLifetime
{
    [Fact]
    public async Task Nova_And_Bolt_HandleSimultaneousRequests_NoCrossTalk()
    {
        var novaChatId = $"concurrent-nova-{Guid.NewGuid():N}";
        var boltChatId = $"concurrent-bolt-{Guid.NewGuid():N}";

        // Send to both agents simultaneously
        var novaTask = fixture.SendMessageAsync(
            "nova", "What are the best pizzas?", fixture.WebChannel, novaChatId);
        var boltTask = fixture.SendMessageAsync(
            "bolt", "What is 15 * 4?", fixture.WebChannel, boltChatId);

        await Task.WhenAll(novaTask, boltTask);

        // Wait for both responses
        var novaResponse = await fixture.WebChannel.WaitForResponseAsync(novaChatId);
        var boltResponse = await fixture.WebChannel.WaitForResponseAsync(boltChatId);

        // Nova should talk about pizza, not math
        novaResponse.Content.Should().ContainAny("pizza", "Margherita", "New York");
        novaResponse.Content.Should().NotContain("60");

        // Bolt should talk about math, not pizza
        boltResponse.Content.Should().Contain("60");
        boltResponse.Content.Should().NotContainAny("pizza", "Margherita");

        // Responses should be on correct chat IDs
        novaResponse.ChatId.Should().Be(novaChatId);
        boltResponse.ChatId.Should().Be(boltChatId);
    }

    public Task InitializeAsync()
    {
        fixture.WebChannel.Reset();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;
}
