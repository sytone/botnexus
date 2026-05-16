using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class CanvasPanelTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly ClientStateStore _store = new();

    public CanvasPanelTests()
    {
        _store.SeedAgents([new AgentSummary("agent-1", "Alpha")]);
        _ctx.Services.AddSingleton<IClientStateStore>(_store);
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void Shows_empty_state_when_agent_has_not_published_canvas_html()
    {
        var cut = _ctx.Render<CanvasPanel>(parameters => parameters.Add(x => x.AgentId, "agent-1"));

        cut.Markup.ShouldContain("Canvas output will appear here");
        cut.FindAll("iframe").ShouldBeEmpty();
    }

    [Fact]
    public void Renders_iframe_with_sandboxed_srcdoc_when_canvas_html_exists()
    {
        var agent = _store.GetAgent("agent-1")!;
        agent.CanvasHtml = "<html><body><h1>Canvas</h1></body></html>";

        var cut = _ctx.Render<CanvasPanel>(parameters => parameters.Add(x => x.AgentId, "agent-1"));

        var frame = cut.Find("iframe[data-testid='canvas-iframe']");
        var sandbox = frame.GetAttribute("sandbox");
        var srcdoc = frame.GetAttribute("srcdoc");

        Assert.NotNull(sandbox);
        Assert.NotNull(srcdoc);
        sandbox.ShouldNotContain("allow-same-origin");
        sandbox.ShouldNotContain("allow-top-navigation");
        srcdoc.ShouldContain("<h1>Canvas</h1>");
    }

    [Fact]
    public void Clears_iframe_and_restores_empty_state_when_canvas_html_is_removed()
    {
        var agent = _store.GetAgent("agent-1")!;
        agent.CanvasHtml = "<html><body>Initial</body></html>";

        var cut = _ctx.Render<CanvasPanel>(parameters => parameters.Add(x => x.AgentId, "agent-1"));
        cut.Find("iframe[data-testid='canvas-iframe']");

        agent.CanvasHtml = null;
        cut.Render();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("iframe[data-testid='canvas-iframe']").ShouldBeEmpty();
            cut.Markup.ShouldContain("Canvas output will appear here");
        });
    }

    [Fact]
    public void Rapid_successive_updates_render_latest_canvas_html_only()
    {
        var agent = _store.GetAgent("agent-1")!;
        var cut = _ctx.Render<CanvasPanel>(parameters => parameters.Add(x => x.AgentId, "agent-1"));

        agent.CanvasHtml = "<html><body>first</body></html>";
        cut.Render();
        agent.CanvasHtml = "<html><body>second</body></html>";
        cut.Render();
        agent.CanvasHtml = "<html><body>final</body></html>";
        cut.Render();

        cut.WaitForAssertion(() =>
        {
            var frame = cut.Find("iframe[data-testid='canvas-iframe']");
            var srcdoc = frame.GetAttribute("srcdoc");
            if (srcdoc is null)
                throw new InvalidOperationException("Expected canvas iframe srcdoc to be present.");

            srcdoc.ShouldContain("final");
            srcdoc.ShouldNotContain("first");
            srcdoc.ShouldNotContain("second");
        });
    }

    [Fact]
    public void App_css_contains_canvas_mobile_hooks()
    {
        var cssPath = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "extensions",
            "BotNexus.Extensions.Channels.SignalR.BlazorClient",
            "wwwroot",
            "css",
            "app.css");

        var css = File.ReadAllText(cssPath);

        css.ShouldContain(".canvas-panel");
        css.ShouldContain(".canvas-empty-state");
        css.ShouldContain("-webkit-overflow-scrolling: touch;");
    }

    [Fact]
    public void Canvas_panel_uses_restricted_iframe_sandbox_policy()
    {
        var componentPath = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "extensions",
            "BotNexus.Extensions.Channels.SignalR.BlazorClient",
            "Components",
            "CanvasPanel.razor");

        var component = File.ReadAllText(componentPath);

        component.ShouldContain("sandbox=");
        component.ShouldNotContain("allow-same-origin");
        component.ShouldNotContain("allow-top-navigation");
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "BotNexus.slnx")))
                return current.FullName;

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Unable to locate BotNexus.slnx from test base directory.");
    }
}
