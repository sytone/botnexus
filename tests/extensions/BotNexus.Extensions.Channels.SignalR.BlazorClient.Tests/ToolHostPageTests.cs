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

    // The client cannot detect a refusal: a blocked frame raises the same load event a working
    // one does, and reading into it throws SecurityError either way. A probe was written to try,
    // and could not tell them apart - SABnzbd showed a blank white panel while the page believed
    // it had loaded. So the gateway reads the headers and the host asks BEFORE framing.
    [Fact]
    public void A_server_side_refusal_shows_the_fallback_without_ever_framing()
    {
        SetupTool(id: "sab", url: "http://sab.example/", sandboxEnabled: true);
        SetupEmbeddable("sab", embeddable: false, reason: "X-Frame-Options: SameOrigin", isChecked: true);

        var cut = _ctx.Render<ToolHost>(p => p.Add(c => c.Id, "sab"));
        cut.WaitForState(() => cut.Markup.Contains("tool-host-refused"));

        // No frame is ever created, so there is no blank panel to sit in front of.
        Assert.Empty(cut.FindAll("[data-testid='tool-host-iframe']"));
        Assert.Equal("http://sab.example/", cut.Find("[data-testid='tool-host-open-external']").GetAttribute("href"));
    }

    [Fact]
    public void The_refusal_names_the_header_responsible()
    {
        SetupTool(id: "unraid", url: "http://unraid.example/", sandboxEnabled: true);
        SetupEmbeddable("unraid", embeddable: false,
            reason: "Content-Security-Policy: frame-ancestors 'self'", isChecked: true);

        var cut = _ctx.Render<ToolHost>(p => p.Add(c => c.Id, "unraid"));
        cut.WaitForState(() => cut.Markup.Contains("tool-host-refused"));

        Assert.Contains("frame-ancestors",
            cut.Find("[data-testid='tool-host-refused-reason']").TextContent);
    }

    // Unreachable from the gateway does not mean unreachable from the browser - they may be on
    // different networks, and a self-signed certificate fails the gateway's check while the
    // browser is perfectly happy. An unknown verdict must not become a refusal.
    [Fact]
    public void An_unchecked_verdict_still_attempts_the_frame()
    {
        SetupTool(id: "proxmox", url: "https://proxmox.example:8006/", sandboxEnabled: true);
        SetupEmbeddable("proxmox", embeddable: true, reason: null, isChecked: false);

        var cut = _ctx.Render<ToolHost>(p => p.Add(c => c.Id, "proxmox"));
        cut.WaitForState(() => cut.Markup.Contains("tool-host-iframe"));

        Assert.DoesNotContain("tool-host-refused", cut.Markup);
    }

    private void SetupTool(string id, string url, bool sandboxEnabled)
    {
        _handler.SetupResponse("GET", $"/api/tools/{id}", JsonSerializer.Serialize(new
        {
            id,
            name = $"Tool {id}",
            url,
            icon = "\U0001F527",
            order = 0,
            sandboxEnabled
        }));

        // The host asks the gateway whether the site permits framing before it tries. Default to
        // yes so the existing cases still reach the frame.
        SetupEmbeddable(id, embeddable: true, reason: null, isChecked: true);
    }

    private void SetupEmbeddable(string id, bool embeddable, string? reason, bool isChecked)
        => _handler.SetupResponse("GET", $"/api/tools/{id}/embeddable", JsonSerializer.Serialize(new
        {
            embeddable,
            reason,
            @checked = isChecked,
        }));

    private sealed class ToolHostMockHandler : HttpMessageHandler
    {
        // Bodies, not responses. Handing out the same HttpResponseMessage twice fails the second
        // caller with ObjectDisposedException, because the first disposes the content it read -
        // which is exactly what happened when the host began asking about embeddability as well
        // as fetching the tool.
        private readonly Dictionary<string, string> _bodies = new(StringComparer.OrdinalIgnoreCase);

        public void SetupResponse(string method, string pathSuffix, string jsonContent)
            => _bodies[$"{method}:{pathSuffix}"] = jsonContent;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.PathAndQuery ?? "";
            var methodKey = $"{request.Method.Method}:{path}";

            // Longest key first, so /api/tools/x/embeddable is not swallowed by /api/tools/x.
            foreach (var (key, body) in _bodies.OrderByDescending(kv => kv.Key.Length))
            {
                if (methodKey.Contains(key, StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
                    });
                }
            }

            // Default: 404 so unknown ids exercise the not-found path.
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
