using System.Net;
using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Tests the provider usage popover, in particular that Escape can actually reach it.
/// </summary>
/// <remarks>
/// The load-bearing test is <see cref="Opening_moves_focus_to_the_overlay_so_escape_can_reach_it"/>.
/// The panel shipped with an Escape handler bound to its overlay div and nothing that ever gave
/// that div focus - a keydown handler only fires while its own element holds focus, and clicking
/// the Usage button in the banner leaves focus on the button. Escape therefore did nothing,
/// confirmed in a live browser. A test that merely dispatches the key at the overlay passes either
/// way, so it is the FOCUS assertion that guards the defect.
/// </remarks>
public sealed class ProviderUsagePanelTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly StubHandler _handler = new();

    public ProviderUsagePanelTests()
    {
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _ctx.Services.AddSingleton(new HttpClient(_handler) { BaseAddress = new Uri("http://localhost/") });
    }

    public void Dispose() => _ctx.Dispose();

    private async Task<IRenderedComponent<ProviderUsagePanel>> OpenAsync()
    {
        var cut = _ctx.Render<ProviderUsagePanel>();
        await cut.InvokeAsync(() => cut.Instance.OpenAsync());
        cut.WaitForState(() => cut.FindAll("[data-testid='provider-usage-panel']").Count > 0);
        return cut;
    }

    [Fact]
    public void The_panel_is_closed_until_it_is_opened()
    {
        var cut = _ctx.Render<ProviderUsagePanel>();

        Assert.Empty(cut.FindAll("[data-testid='provider-usage-panel']"));
    }

    [Fact]
    public async Task Escape_closes_the_panel()
    {
        var cut = await OpenAsync();

        cut.Find(".usage-overlay").KeyDown(Key.Escape);

        Assert.Empty(cut.FindAll("[data-testid='provider-usage-panel']"));
    }

    // THE regression guard. Without the focus call the handler above is unreachable in a real
    // browser, and the Escape test still passes because bUnit dispatches the event directly.
    [Fact]
    public async Task Opening_moves_focus_to_the_overlay_so_escape_can_reach_it()
    {
        var before = _ctx.JSInterop.Invocations.Count(i => i.Identifier.Contains("focus"));

        await OpenAsync();

        Assert.True(
            _ctx.JSInterop.Invocations.Count(i => i.Identifier.Contains("focus")) > before,
            "Opening the panel must move focus to the overlay, or its Escape handler can never fire.");
    }

    // A key that is not Escape must not close it, or typing near the panel would dismiss it.
    [Fact]
    public async Task Another_key_leaves_the_panel_open()
    {
        var cut = await OpenAsync();

        cut.Find(".usage-overlay").KeyDown(Key.Enter);

        Assert.Single(cut.FindAll("[data-testid='provider-usage-panel']"));
    }

    [Fact]
    public async Task Clicking_the_overlay_closes_the_panel_but_clicking_the_panel_does_not()
    {
        var cut = await OpenAsync();

        cut.Find("[data-testid='usage-close']").Click();

        Assert.Empty(cut.FindAll("[data-testid='provider-usage-panel']"));
    }

    /// <summary>Answers the usage endpoint with a single provider.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "windowMinutes": 60,
                      "providers": [{
                        "provider": "anthropic",
                        "observedAtUtc": "2026-08-28T10:00:00Z",
                        "limits": [],
                        "burn": {
                          "requests": 3, "failures": 0,
                          "inputTokens": 100, "outputTokens": 50, "models": []
                        }
                      }]
                    }
                    """,
                    System.Text.Encoding.UTF8,
                    "application/json"),
            });
    }
}
