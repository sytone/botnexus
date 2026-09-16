using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class SkillsExplorerStartupTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly IGatewayRestClient _restClient = Substitute.For<IGatewayRestClient>();
    private readonly IPortalLoadService _portalLoad = Substitute.For<IPortalLoadService>();

    public SkillsExplorerStartupTests()
    {
        _ctx.Services.AddSingleton(_restClient);
        _ctx.Services.AddSingleton(_portalLoad);
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        _restClient.GetSkillsAsync(null, Arg.Any<CancellationToken>())
            .Returns(new WorkspaceResponseDto("directory", "", [], null, null, null, null, null));
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void Mounting_after_later_portal_failure_loads_from_configured_rest_client()
    {
        _restClient.ApiBaseUrl.Returns("http://localhost/api/");
        _portalLoad.IsReady.Returns(false);
        _portalLoad.IsLoading.Returns(false);
        _portalLoad.LoadError.Returns("Portal failed to load: hub unavailable");

        var cut = _ctx.Render<SkillsExplorerPanel>();

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("No skills yet"));
        _restClient.Received(1).GetSkillsAsync(null, Arg.Any<CancellationToken>());
        cut.Markup.ShouldNotContain("Loading skills");
    }

    [Fact]
    public void Mounting_before_later_portal_failure_loads_when_failure_reports_rest_is_configured()
    {
        string? apiBaseUrl = null;
        _restClient.ApiBaseUrl.Returns(_ => apiBaseUrl);
        _portalLoad.IsReady.Returns(false);
        _portalLoad.IsLoading.Returns(true);
        _portalLoad.LoadError.Returns((string?)null);
        var cut = _ctx.Render<SkillsExplorerPanel>();
        cut.Markup.ShouldContain("Loading skills");
        _restClient.DidNotReceive().GetSkillsAsync(null, Arg.Any<CancellationToken>());

        apiBaseUrl = "http://localhost/api/";
        _portalLoad.IsLoading.Returns(false);
        _portalLoad.LoadError.Returns("Portal failed to load: hub unavailable");
        _portalLoad.OnReadyChanged += Raise.Event<Action>();

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("No skills yet"));
        _restClient.Received(1).GetSkillsAsync(null, Arg.Any<CancellationToken>());
        cut.Markup.ShouldNotContain("Loading skills");
    }

    [Fact]
    public void Terminal_failure_before_rest_configuration_clears_loading_with_explicit_error()
    {
        _restClient.ApiBaseUrl.Returns((string?)null);
        _portalLoad.IsReady.Returns(false);
        _portalLoad.IsLoading.Returns(false);
        _portalLoad.LoadError.Returns("Portal failed to load before REST configuration");

        var cut = _ctx.Render<SkillsExplorerPanel>();

        cut.Markup.ShouldContain("Unable to load skills because the portal REST client is not configured.");
        cut.Markup.ShouldNotContain("Loading skills");
        _restClient.DidNotReceive().GetSkillsAsync(null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Ordinary_ready_initialization_loads_root_once()
    {
        _restClient.ApiBaseUrl.Returns("http://localhost/api/");
        _portalLoad.IsReady.Returns(true);

        var cut = _ctx.Render<SkillsExplorerPanel>();

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("No skills yet"));
        _restClient.Received(1).GetSkillsAsync(null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Repeated_readiness_events_start_at_most_one_root_load()
    {
        var pending = new TaskCompletionSource<WorkspaceResponseDto?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _restClient.GetSkillsAsync(null, Arg.Any<CancellationToken>()).Returns(_ => pending.Task);
        string? apiBaseUrl = null;
        _restClient.ApiBaseUrl.Returns(_ => apiBaseUrl);
        _portalLoad.IsReady.Returns(false);
        _portalLoad.IsLoading.Returns(true);
        var cut = _ctx.Render<SkillsExplorerPanel>();

        apiBaseUrl = "http://localhost/api/";
        _portalLoad.OnReadyChanged += Raise.Event<Action>();
        _portalLoad.OnReadyChanged += Raise.Event<Action>();

        _restClient.Received(1).GetSkillsAsync(null, Arg.Any<CancellationToken>());
        pending.SetResult(new WorkspaceResponseDto("directory", "", [], null, null, null, null, null));
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("No skills yet"));
    }

    [Fact]
    public void Readiness_after_disposal_does_not_load_or_mutate_component()
    {
        var rootLoadCount = 0;
        _restClient.GetSkillsAsync(null, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            rootLoadCount++;
            return new WorkspaceResponseDto("directory", "", [], null, null, null, null, null);
        });
        string? apiBaseUrl = null;
        _restClient.ApiBaseUrl.Returns(_ => apiBaseUrl);
        _portalLoad.IsReady.Returns(false);
        _portalLoad.IsLoading.Returns(true);
        var cut = _ctx.Render<SkillsExplorerPanel>();
        rootLoadCount.ShouldBe(0);

        cut.Instance.Dispose();
        apiBaseUrl = "http://localhost/api/";
        _portalLoad.OnReadyChanged += Raise.Event<Action>();

        rootLoadCount.ShouldBe(0);
    }
}
