using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// bUnit tests for the per-conversation override picker (PBI5, issue #1706). Covers rendering in
/// the default (no override) state, hydrating from an existing override, saving a new override
/// through the REST client, and clearing the override back to the agent default.
/// </summary>
public sealed class ConversationOverridePanelTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly IGatewayRestClient _restClient = Substitute.For<IGatewayRestClient>();

    public ConversationOverridePanelTests()
        => _ctx.Services.AddSingleton(_restClient);

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void Renders_default_empty_state()
    {
        var cut = _ctx.Render<ConversationOverridePanel>(p => p.Add(x => x.ConversationId, "c1"));

        cut.Find("[data-testid='conversation-override-panel']").ShouldNotBeNull();
        cut.Find("[data-testid='override-model']").GetAttribute("value").ShouldBeNullOrEmpty();
    }

    [Fact]
    public void Hydrates_from_existing_override()
    {
        var cut = _ctx.Render<ConversationOverridePanel>(p => p
            .Add(x => x.ConversationId, "c1")
            .Add(x => x.InitialModel, "claude-opus-4")
            .Add(x => x.InitialThinking, "high")
            .Add(x => x.InitialContextWindow, 128000));

        cut.Find("[data-testid='override-model']").GetAttribute("value").ShouldBe("claude-opus-4");
    }

    [Fact]
    public async Task Save_invokes_rest_client_with_entered_values()
    {
        var cut = _ctx.Render<ConversationOverridePanel>(p => p
            .Add(x => x.ConversationId, "c1")
            .Add(x => x.InitialModel, "claude-opus-4")
            .Add(x => x.InitialThinking, "high"));

        await cut.Find("[data-testid='override-save']").ClickAsync(new());

        await _restClient.Received(1).SetConversationOverrideAsync(
            "c1",
            Arg.Is<SetConversationOverrideRequestDto>(r => r.Model == "claude-opus-4" && r.Thinking == "high"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Clear_invokes_rest_client_clear_endpoint()
    {
        var cut = _ctx.Render<ConversationOverridePanel>(p => p
            .Add(x => x.ConversationId, "c1")
            .Add(x => x.InitialModel, "claude-opus-4"));

        await cut.Find("[data-testid='override-clear']").ClickAsync(new());

        await _restClient.Received(1).ClearConversationOverrideAsync("c1", Arg.Any<CancellationToken>());
    }
}
