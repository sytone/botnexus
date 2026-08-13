using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Pages;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services.SlashCommands;
using BotNexus.Gateway.Tools;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// #2975 AC2: the link the <c>canvas</c> tool emits must actually select the Canvas pane.
/// </summary>
/// <remarks>
/// <para>The existing <c>Canvas_query_parameter_activates_canvas_tab</c> asserts the panel honours a
/// HAND-WRITTEN <c>?tab=canvas</c>. That cannot detect the failure this issue is most exposed to:
/// the tool emitting a link whose query, casing or route shape drifts from what
/// <c>AgentPanel.ApplyTabFromUri</c> parses. Two literals agreeing by coincidence is not a contract.</para>
/// <para>So this test drives the panel with the string <see cref="CanvasDeepLink.TryBuild"/> actually
/// produced. The producer and the consumer are pinned to each other, and a change to either side's
/// convention reddens it.</para>
/// </remarks>
public sealed class AgentPanelCanvasDeepLinkTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly ClientStateStore _store = new();
    private readonly IPortalLoadService _portalLoad = Substitute.For<IPortalLoadService>();

    public AgentPanelCanvasDeepLinkTests()
    {
        _portalLoad.IsReady.Returns(true);
        _portalLoad.IsLoading.Returns(false);
        _portalLoad.LoadError.Returns((string?)null);
        _portalLoad.InitializeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        _store.SeedAgents([new AgentSummary("agent-1", "Alpha")]);
        _store.SeedConversations("agent-1", [
            new ConversationSummaryDto(
                ConversationId: "conv-1",
                AgentId: "agent-1",
                Title: "General",
                IsDefault: true,
                Status: "Active",
                ActiveSessionId: null,
                BindingCount: 0,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow)
        ]);
        _store.SelectView("agent-1", string.Empty, SelectionSource.UserClick);

        _ctx.Services.AddSingleton<IClientStateStore>(_store);
        _ctx.Services.AddSingleton(_portalLoad);
        _ctx.Services.AddSingleton(Substitute.For<IAgentInteractionService>());
        _ctx.Services.AddSingleton<ISlashCommandDispatcher>(sp =>
            new SlashCommandDispatcher(sp.GetRequiredService<IAgentInteractionService>()));
        _ctx.Services.AddSingleton(Substitute.For<IPortalPreferencesService>());
        _ctx.Services.AddSingleton(Substitute.For<IGatewayRestClient>());
        _ctx.Services.AddSingleton(new HttpClient { BaseAddress = new Uri("http://localhost/") });
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void Emitted_deep_link_activates_the_canvas_tab()
    {
        Assert.True(CanvasDeepLink.TryBuild("http://localhost", "agent-1", "conv-1", out var link));

        _ctx.Services.GetRequiredService<NavigationManager>().NavigateTo(link);

        var cut = _ctx.Render<Home>(parameters => parameters
            .Add(p => p.AgentId, "agent-1")
            .Add(p => p.ConversationId, "conv-1"));

        cut.WaitForAssertion(() =>
        {
            Assert.NotNull(cut.Find(".agent-panel-tab.active[data-tab='canvas']"));
            Assert.NotNull(cut.Find("[data-testid='canvas-panel']"));
        });
    }
}
