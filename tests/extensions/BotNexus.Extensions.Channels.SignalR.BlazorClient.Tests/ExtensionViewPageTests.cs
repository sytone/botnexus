using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

using ExtensionViewPage = BotNexus.Extensions.Channels.SignalR.BlazorClient.Pages.ExtensionView;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Pins the portal-hosted view for extension-served pages.
/// </summary>
/// <remarks>
/// The point of this page is that the portal shell survives: an extension serves its own document
/// while the sidebar, header and theme stay put. So the assertions are that the frame points at
/// the CONTRIBUTED path (not a guess derived from the id), and that an id nothing provides
/// degrades to a notice rather than an empty frame pointing nowhere.
/// </remarks>
public sealed class ExtensionViewPageTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly IPortalPreferencesService _prefs = Substitute.For<IPortalPreferencesService>();
    private string _json = "[]";

    public ExtensionViewPageTests()
    {
        _prefs.Current.Returns(new PortalPreferences { Theme = PortalTheme.Dark });
        _ctx.Services.AddSingleton(_prefs);
        _ctx.Services.AddSingleton(new NavOrderApiClient(
            new HttpClient(new JsonHandler(() => _json)) { BaseAddress = new Uri("http://localhost/") }));
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    private const string AgentBuilder = """
        [
          { "id": "agent-builder", "label": "Agent Builder", "path": "/agent-builder",
            "icon": "tools", "order": 65, "external": true, "extensionId": "botnexus-agent-builder" }
        ]
        """;

    private IRenderedComponent<ExtensionViewPage> RenderFor(string viewId)
    {
        var cut = _ctx.Render<ExtensionViewPage>(p => p.Add(c => c.ViewId, viewId));
        cut.WaitForState(() => cut.FindAll("[data-testid='extension-view-loading']").Count == 0);
        return cut;
    }

    [Fact]
    public void Frames_the_contributed_path()
    {
        _json = AgentBuilder;

        var cut = RenderFor("agent-builder");

        var frame = Assert.Single(cut.FindAll("[data-testid='extension-view-frame']"));
        Assert.Equal("/agent-builder", frame.GetAttribute("src"));
        Assert.Equal("Agent Builder", frame.GetAttribute("title"));
    }

    // The path comes from the contribution, never from the id: an extension may serve a view at a
    // path that has nothing to do with its nav key, and guessing would silently frame the wrong URL.
    [Fact]
    public void Frames_a_path_that_differs_from_the_view_id()
    {
        _json = """
            [
              { "id": "builder", "label": "Builder", "path": "/some/other/place",
                "order": 65, "external": true, "extensionId": "x" }
            ]
            """;

        var cut = RenderFor("builder");

        Assert.Equal("/some/other/place",
            Assert.Single(cut.FindAll("[data-testid='extension-view-frame']")).GetAttribute("src"));
    }

    // A stale bookmark, or a plugin uninstalled since the link was shared, is a normal thing to
    // happen - a notice, not an error page and not an empty frame.
    [Fact]
    public void An_unknown_view_id_reports_it_and_frames_nothing()
    {
        _json = AgentBuilder;

        var cut = RenderFor("no-such-view");

        Assert.Empty(cut.FindAll("[data-testid='extension-view-frame']"));
        Assert.Contains("no-such-view", Assert.Single(cut.FindAll("[data-testid='extension-view-unknown']")).TextContent);
    }

    [Fact]
    public void No_contributions_at_all_reports_the_view_as_unavailable()
    {
        _json = "[]";

        var cut = RenderFor("agent-builder");

        Assert.Empty(cut.FindAll("[data-testid='extension-view-frame']"));
        Assert.Single(cut.FindAll("[data-testid='extension-view-unknown']"));
    }

    // A hosted view styles itself from prefers-color-scheme, as any standalone web app would.
    // color-scheme on the iframe ELEMENT propagates into the embedded document and drives that
    // query, so the portal theme reaches the view without the extension knowing the portal exists.
    [Theory]
    [InlineData("dark", "dark")]
    [InlineData("light", "light")]
    public void The_frame_carries_the_portal_colour_scheme(string theme, string expected)
    {
        _prefs.Current.Returns(new PortalPreferences { Theme = theme });
        _json = AgentBuilder;

        var cut = RenderFor("agent-builder");

        var frame = Assert.Single(cut.FindAll("[data-testid='extension-view-frame']"));
        Assert.Equal(expected, frame.GetAttribute("data-theme-scheme"));
        Assert.Contains($"color-scheme: {expected}", frame.GetAttribute("style"));
    }

    // Non-vacuity: the same component must render BOTH schemes, or the assertion above would pass
    // for a frame with the value hard-coded.
    [Fact]
    public void The_colour_scheme_differs_between_themes()
    {
        _json = AgentBuilder;

        _prefs.Current.Returns(new PortalPreferences { Theme = PortalTheme.Dark });
        var dark = RenderFor("agent-builder").Find("[data-testid='extension-view-frame']").GetAttribute("data-theme-scheme");

        _prefs.Current.Returns(new PortalPreferences { Theme = PortalTheme.Light });
        var light = RenderFor("agent-builder").Find("[data-testid='extension-view-frame']").GetAttribute("data-theme-scheme");

        Assert.NotEqual(dark, light);
    }

    private sealed class JsonHandler(Func<string> json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json(), System.Text.Encoding.UTF8, "application/json"),
            });
    }
}
