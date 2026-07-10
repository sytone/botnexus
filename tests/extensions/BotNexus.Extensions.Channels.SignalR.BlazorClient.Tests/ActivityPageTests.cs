using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Pages;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// bUnit tests for the Activity landing page (root route) added for #1888. Verifies the loading /
/// error gate and that the dashboard renders once the portal is ready.
/// </summary>
public sealed class ActivityPageTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly IPortalLoadService _portalLoad;
    private readonly IClientStateStore _store;
    private readonly IGatewayRestClient _rest;

    public ActivityPageTests()
    {
        _portalLoad = Substitute.For<IPortalLoadService>();
        _store = Substitute.For<IClientStateStore>();
        _rest = Substitute.For<IGatewayRestClient>();

        _portalLoad.IsReady.Returns(false);
        _portalLoad.IsLoading.Returns(true);
        _portalLoad.LoadError.Returns((string?)null);
        _portalLoad.InitializeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _store.Agents.Returns(new Dictionary<string, AgentState>().AsReadOnly());
        _store.GetAgent(Arg.Any<string>()).Returns((AgentState?)null);
        _rest.GetAllConversationsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ConversationSummaryDto>>(Array.Empty<ConversationSummaryDto>()));

        _ctx.Services.AddSingleton(_portalLoad);
        _ctx.Services.AddSingleton(_store);
        _ctx.Services.AddSingleton(_rest);
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void Shows_connecting_spinner_when_not_ready()
    {
        _portalLoad.IsReady.Returns(false);

        var cut = _ctx.Render<Activity>();

        cut.Find(".portal-loading");
        Assert.Contains("Connecting", cut.Markup);
    }

    [Fact]
    public void Shows_error_when_load_error_set()
    {
        _portalLoad.IsReady.Returns(false);
        _portalLoad.LoadError.Returns("Connection refused");

        var cut = _ctx.Render<Activity>();

        cut.Find(".portal-load-error");
        Assert.Contains("Connection refused", cut.Markup);
    }

    [Fact]
    public void Renders_dashboard_when_ready()
    {
        _portalLoad.IsReady.Returns(true);

        var cut = _ctx.Render<Activity>();

        cut.Find("[data-testid='activity-dashboard']");
        Assert.Empty(cut.FindAll(".portal-loading"));
    }
}
