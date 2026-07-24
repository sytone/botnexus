using System.Net;
using System.Text.Json;
using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Pages;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Tests the portal iframe host route <c>/tools/{id}</c> (#2234, slice 3 of #2231): sandboxed
/// embedding by default, the per-tool sandbox opt-out, and the graceful embed-refusal fallback
/// that replaces a broken blank frame with an "open in new tab" link.
/// </summary>
public sealed class ToolHostPageTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly ToolHostMockHandler _handler = new();

    public ToolHostPageTests()
    {
        var httpClient = new HttpClient(_handler) { BaseAddress = new Uri("http://localhost/") };
        _ctx.Services.AddSingleton(httpClient);
        _ctx.Services.AddScoped<ToolsApiClient>();
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void Embeddable_tool_renders_sandboxed_iframe_with_url()
    {
        SetupTool(id: "grafana", url: "https://grafana.example/dashboard", sandboxEnabled: true);

        var cut = _ctx.Render<ToolHost>(p => p.Add(c => c.Id, "grafana"));
        cut.WaitForState(() => cut.Markup.Contains("tool-host-iframe"));

        var iframe = cut.Find("[data-testid='tool-host-iframe']");
        Assert.Equal("https://grafana.example/dashboard", iframe.GetAttribute("src"));
        // Sandboxed by default: the sandbox attribute must be present and restrict to a safe set.
        var sandbox = iframe.GetAttribute("sandbox");
        Assert.NotNull(sandbox);
        Assert.Contains("allow-scripts", sandbox);
    }

    [Fact]
    public void Sandbox_opt_out_renders_iframe_without_sandbox_attribute()
    {
        SetupTool(id: "trusted", url: "https://trusted.example/", sandboxEnabled: false);

        var cut = _ctx.Render<ToolHost>(p => p.Add(c => c.Id, "trusted"));
        cut.WaitForState(() => cut.Markup.Contains("tool-host-iframe"));

        var iframe = cut.Find("[data-testid='tool-host-iframe']");
        // Opt-out: no sandbox attribute at all, so the framed site runs unrestricted.
        Assert.False(iframe.HasAttribute("sandbox"));
    }

    [Fact]
    public void Refusal_shows_open_in_new_tab_fallback_not_blank_frame()
    {
        SetupTool(id: "denied", url: "https://denied.example/", sandboxEnabled: true);

        var cut = _ctx.Render<ToolHost>(p => p.Add(c => c.Id, "denied"));
        cut.WaitForState(() => cut.Markup.Contains("tool-host-iframe"));

        // Simulate the browser blocking the frame (X-Frame-Options / frame-ancestors): the
        // watchdog invokes MarkRefused because no usable load ever arrives.
        cut.InvokeAsync(() => cut.Instance.MarkRefused());
        cut.WaitForState(() => cut.Markup.Contains("tool-host-refused"));

        Assert.Contains("This site can't be embedded", cut.Markup);
        var link = cut.Find("[data-testid='tool-host-open-external']");
        Assert.Equal("https://denied.example/", link.GetAttribute("href"));
        Assert.Equal("_blank", link.GetAttribute("target"));
        // No broken frame should remain once we fall back.
        Assert.Empty(cut.FindAll("[data-testid='tool-host-iframe']"));
    }

    [Fact]
    public void Load_before_timeout_keeps_frame_and_ignores_late_refusal()
    {
        SetupTool(id: "fast", url: "https://fast.example/", sandboxEnabled: true);

        var cut = _ctx.Render<ToolHost>(p => p.Add(c => c.Id, "fast"));
        cut.WaitForState(() => cut.Markup.Contains("tool-host-iframe"));

        // Embeddable site paints: the iframe raises load, promoting to Ready.
        cut.Find("[data-testid='tool-host-iframe']").TriggerEvent("onload", new EventArgs());

        // A late watchdog firing must NOT bounce a working frame into the fallback.
        cut.InvokeAsync(() => cut.Instance.MarkRefused());

        Assert.NotEmpty(cut.FindAll("[data-testid='tool-host-iframe']"));
        Assert.DoesNotContain("tool-host-refused", cut.Markup);
    }

    [Fact]
    public void Unknown_tool_shows_not_found()
    {
        // No response registered -> 404.
        var cut = _ctx.Render<ToolHost>(p => p.Add(c => c.Id, "missing"));
        cut.WaitForState(() => cut.Markup.Contains("tool-host-notfound"));

        Assert.Contains("Tool not found", cut.Markup);
    }

    private void SetupTool(string id, string url, bool sandboxEnabled)
    {
        _handler.SetupResponse("GET", $"/api/tools/{id}", JsonSerializer.Serialize(new
        {
            id,
            name = $"Tool {id}",
            url,
            icon = "🔧",
            order = 0,
            sandboxEnabled
        }));
    }

    private sealed class ToolHostMockHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, HttpResponseMessage> _responses = new(StringComparer.OrdinalIgnoreCase);

        public void SetupResponse(string method, string pathSuffix, string jsonContent)
        {
            _responses[$"{method}:{pathSuffix}"] = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(jsonContent, System.Text.Encoding.UTF8, "application/json")
            };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.PathAndQuery ?? "";
            var methodKey = $"{request.Method.Method}:{path}";

            foreach (var (key, response) in _responses.OrderByDescending(kv => kv.Key.Length))
            {
                if (methodKey.Contains(key, StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(response);
            }

            // Default: 404 so unknown ids exercise the not-found path.
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}
