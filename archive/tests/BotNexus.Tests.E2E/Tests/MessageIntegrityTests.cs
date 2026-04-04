using BotNexus.Tests.E2E.Infrastructure;
using FluentAssertions;

namespace BotNexus.Tests.E2E.Tests;

/// <summary>Scenario 6: Message integrity — Echo reproduces exact input on both channels.</summary>
[Collection(MultiAgentE2eCollection.Name)]
public sealed class MessageIntegrityTests(MultiAgentFixture fixture) : IAsyncLifetime
{
    [Fact]
    public async Task Echo_ReproducesExactText_ViaWebChannel()
    {
        var chatId = $"integrity-web-{Guid.NewGuid():N}";
        var formattedText = "**Bold text**, *italic*, `code block`, line1\nline2\n- item1\n- item2";

        await fixture.SendMessageAsync("echo", formattedText, fixture.WebChannel, chatId);

        var response = await fixture.WebChannel.WaitForResponseAsync(chatId);
        response.Content.Should().Be(formattedText);
    }

    [Fact]
    public async Task Echo_ReproducesExactText_ViaApiChannel()
    {
        var chatId = $"integrity-api-{Guid.NewGuid():N}";
        var formattedText = "**Bold text**, *italic*, `code block`, line1\nline2\n- item1\n- item2";

        await fixture.SendMessageAsync("echo", formattedText, fixture.ApiChannel, chatId);

        var response = await fixture.ApiChannel.WaitForResponseAsync(chatId);
        response.Content.Should().Be(formattedText);
    }

    [Fact]
    public async Task Echo_PreservesUnicodeAndSpecialCharacters()
    {
        var chatId = $"integrity-unicode-{Guid.NewGuid():N}";
        var specialText = "Hello 🌍! Ñoño café résumé naïve — \"quotes\" & <angles>";

        await fixture.SendMessageAsync("echo", specialText, fixture.WebChannel, chatId);

        var response = await fixture.WebChannel.WaitForResponseAsync(chatId);
        response.Content.Should().Be(specialText);
    }

    public Task InitializeAsync()
    {
        fixture.WebChannel.Reset();
        fixture.ApiChannel.Reset();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;
}
