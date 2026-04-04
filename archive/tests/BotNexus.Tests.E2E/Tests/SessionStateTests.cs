using BotNexus.Tests.E2E.Infrastructure;
using FluentAssertions;

namespace BotNexus.Tests.E2E.Tests;

/// <summary>Scenario 2: Session state — Quill saves notes and recalls them.</summary>
[Collection(MultiAgentE2eCollection.Name)]
public sealed class SessionStateTests(MultiAgentFixture fixture) : IAsyncLifetime
{
    [Fact]
    public async Task Quill_RemembersThenRecalls_FavouritePizzas()
    {
        var chatId = $"session-{Guid.NewGuid():N}";

        // Tell Quill to remember
        await fixture.SendMessageAsync(
            "quill",
            "Remember: my favourite pizzas are margherita, pepperoni, hawaiian",
            fixture.WebChannel,
            chatId);

        var saveResponse = await fixture.WebChannel.WaitForResponseAsync(chatId);
        saveResponse.Content.Should().Contain("saved");

        fixture.WebChannel.Reset();

        // Ask Quill to recall
        await fixture.SendMessageAsync(
            "quill",
            "What are my favourite pizzas?",
            fixture.WebChannel,
            chatId);

        var recallResponse = await fixture.WebChannel.WaitForResponseAsync(chatId);
        recallResponse.Content.Should().ContainAny("margherita", "pepperoni", "hawaiian");
    }

    public Task InitializeAsync()
    {
        fixture.WebChannel.Reset();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;
}
