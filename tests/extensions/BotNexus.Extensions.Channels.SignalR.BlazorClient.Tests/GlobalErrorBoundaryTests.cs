using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class GlobalErrorBoundaryTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly IClientStateStore _store = Substitute.For<IClientStateStore>();
    private readonly IGatewayRestClient _restClient = Substitute.For<IGatewayRestClient>();
    private readonly IChannelErrorReporter _errorReporter = Substitute.For<IChannelErrorReporter>();

    public GlobalErrorBoundaryTests()
    {
        _store.ActiveAgentId.Returns("agent-1");
        _errorReporter.ReportAsync(Arg.Any<ChannelErrorReportDto>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        _ctx.Services.AddSingleton(_store);
        _ctx.Services.AddSingleton(_restClient);
        _ctx.Services.AddSingleton(_errorReporter);
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void WhenNoError_RendersChildContent()
    {
        var cut = _ctx.Render<GlobalErrorBoundary>(builder =>
            builder.AddChildContent("<span data-testid=\"child-content\">Hello</span>"));

        Assert.NotNull(cut.Find("[data-testid='child-content']"));
        Assert.Empty(cut.FindAll("[data-testid='error-boundary']"));
    }

    [Fact]
    public void WhenErrorThrown_ShowsErrorBoundaryBanner()
    {
        var cut = _ctx.Render<GlobalErrorBoundary>(builder =>
            builder.AddChildContent<ThrowingComponent>());

        // Error boundary should render the error banner
        cut.WaitForAssertion(() =>
            Assert.NotNull(cut.Find("[data-testid='error-boundary']")));
    }

    [Fact]
    public void ShowDetails_ToggleExpandsCollapse()
    {
        var cut = _ctx.Render<GlobalErrorBoundary>(builder =>
            builder.AddChildContent<ThrowingComponent>());

        cut.WaitForAssertion(() =>
            Assert.NotNull(cut.Find("[data-testid='error-boundary']")));

        // Detail should be hidden initially
        Assert.Empty(cut.FindAll("[data-testid='error-detail']"));

        // Toggle show details
        cut.Find(".error-boundary-toggle").Click();

        // Detail should now be visible
        cut.WaitForAssertion(() =>
            Assert.NotNull(cut.Find("[data-testid='error-detail']")));
    }

    /// <summary>A component that always throws during rendering for testing the error boundary.</summary>
    private sealed class ThrowingComponent : Microsoft.AspNetCore.Components.ComponentBase
    {
        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
            => throw new InvalidOperationException("Test error from ThrowingComponent");
    }
}
