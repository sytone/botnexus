using BotNexus.Tests.E2E.Infrastructure;
using FluentAssertions;

namespace BotNexus.Tests.E2E.Tests;

/// <summary>Scenario 3: Cross-agent handoff — Nova → Quill note save → Quill recall.</summary>
[Collection(MultiAgentE2eCollection.Name)]
public sealed class CrossAgentHandoffTests(MultiAgentFixture fixture) : IAsyncLifetime
{
    [Fact]
    public async Task Nova_ProducesList_Quill_SavesAndRecalls()
    {
        var chatId = $"handoff-{Guid.NewGuid():N}";

        // Step 1: Ask Nova for California pizza recommendations
        await fixture.SendMessageAsync(
            "nova",
            "What pizzas should I try in California?",
            fixture.WebChannel,
            chatId);

        var novaResponse = await fixture.WebChannel.WaitForResponseAsync(chatId);
        novaResponse.Content.Should().Contain("California");
        var novaContent = novaResponse.Content;

        fixture.WebChannel.Reset();

        // Step 2: Tell Quill to save Nova's pizza list
        await fixture.SendMessageAsync(
            "quill",
            $"Save Nova's pizza list to my notes: {novaContent}",
            fixture.WebChannel,
            chatId);

        var saveResponse = await fixture.WebChannel.WaitForResponseAsync(chatId);
        saveResponse.Content.Should().Contain("saved");

        fixture.WebChannel.Reset();

        // Step 3: Ask Quill to show notes
        await fixture.SendMessageAsync(
            "quill",
            "Show my notes",
            fixture.WebChannel,
            chatId);

        var recallResponse = await fixture.WebChannel.WaitForResponseAsync(chatId);
        recallResponse.Content.Should().Contain("notes");
        // The saved content should reference the original Nova response
        recallResponse.Content.Should().ContainAny("California", "Pizzeria Mozza", "Tony's Pizza");
    }

    public Task InitializeAsync()
    {
        fixture.WebChannel.Reset();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;
}
