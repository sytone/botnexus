using BotNexus.Tests.E2E.Infrastructure;
using FluentAssertions;

namespace BotNexus.Tests.E2E.Tests;

/// <summary>Scenario 5: Agent-to-agent chain — Bolt → Sage → Quill.</summary>
[Collection(MultiAgentE2eCollection.Name)]
public sealed class AgentChainTests(MultiAgentFixture fixture) : IAsyncLifetime
{
    [Fact]
    public async Task Bolt_Computes_Sage_Summarizes_Quill_Saves()
    {
        var chatId = $"chain-{Guid.NewGuid():N}";

        // Step 1: Ask Bolt to compute
        await fixture.SendMessageAsync("bolt", "What is 15 * 4?", fixture.WebChannel, chatId);

        var boltResponse = await fixture.WebChannel.WaitForResponseAsync(chatId);
        boltResponse.Content.Should().Contain("60");

        fixture.WebChannel.Reset();

        // Step 2: Tell Sage to summarize Bolt's result
        await fixture.SendMessageAsync(
            "sage",
            "Summarize: Bolt said the answer is 60",
            fixture.WebChannel,
            chatId);

        var sageResponse = await fixture.WebChannel.WaitForResponseAsync(chatId);
        sageResponse.Content.Should().StartWith("Summary:");
        sageResponse.Content.Should().Contain("Bolt");

        fixture.WebChannel.Reset();

        // Step 3: Tell Quill to save Sage's summary
        await fixture.SendMessageAsync(
            "quill",
            $"Save Sage's summary: {sageResponse.Content}",
            fixture.WebChannel,
            chatId);

        var quillSaveResponse = await fixture.WebChannel.WaitForResponseAsync(chatId);
        quillSaveResponse.Content.Should().Contain("saved");

        fixture.WebChannel.Reset();

        // Step 4: Verify the chain by asking Quill to show notes
        await fixture.SendMessageAsync("quill", "Show my notes", fixture.WebChannel, chatId);

        var quillRecallResponse = await fixture.WebChannel.WaitForResponseAsync(chatId);
        quillRecallResponse.Content.Should().Contain("notes");
        quillRecallResponse.Content.Should().ContainAny("Summary", "Bolt", "60");
    }

    public Task InitializeAsync()
    {
        fixture.WebChannel.Reset();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;
}
