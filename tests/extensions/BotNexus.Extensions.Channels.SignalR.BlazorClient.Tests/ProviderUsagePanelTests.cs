using System.Net;
using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class ProviderUsagePanelTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    public ProviderUsagePanelTests()
    {
        _ctx.Services.AddSingleton(new HttpClient(new UsageHandler())
        {
            BaseAddress = new Uri("http://localhost/"),
        });
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task Opening_activates_modal_focus_with_the_dialog_and_opener()
    {
        var cut = _ctx.Render<ProviderUsagePanel>();
        var opener = new ElementReference("usage-opener");

        await cut.InvokeAsync(() => cut.Instance.OpenAsync(opener));

        var invocation = Assert.Single(
            _ctx.JSInterop.Invocations,
            call => call.Identifier == "BotNexus.modalFocus.activate");
        Assert.Equal(2, invocation.Arguments.Count);
        Assert.IsType<ElementReference>(invocation.Arguments[0]);
        Assert.Equal(opener, Assert.IsType<ElementReference>(invocation.Arguments[1]));
        Assert.Equal("dialog", cut.Find("[data-testid='provider-usage-panel']").GetAttribute("role"));
    }

    [Fact]
    public async Task Escape_closes_the_modal_and_restores_focus_to_the_opener()
    {
        var cut = _ctx.Render<ProviderUsagePanel>();
        var opener = new ElementReference("usage-opener");
        await cut.InvokeAsync(() => cut.Instance.OpenAsync(opener));

        cut.Find("[data-testid='provider-usage-panel']").KeyDown(Key.Escape);

        Assert.Empty(cut.FindAll("[data-testid='provider-usage-panel']"));
        var invocation = Assert.Single(
            _ctx.JSInterop.Invocations,
            call => call.Identifier == "BotNexus.modalFocus.deactivate");
        Assert.True(Assert.IsType<bool>(invocation.Arguments[1]));
    }

    [Fact]
    public async Task Close_button_deactivates_modal_focus_and_restores_the_opener()
    {
        var cut = _ctx.Render<ProviderUsagePanel>();
        await cut.InvokeAsync(() => cut.Instance.OpenAsync(new ElementReference("usage-opener")));

        cut.Find("[data-testid='usage-close']").Click();

        Assert.Empty(cut.FindAll("[data-testid='provider-usage-panel']"));
        Assert.Contains(
            _ctx.JSInterop.Invocations,
            call => call.Identifier == "BotNexus.modalFocus.deactivate"
                && call.Arguments.Count == 2
                && call.Arguments[1] is true);
    }

    [Fact]
    public async Task Disposal_deactivates_focus_without_restoring_a_stale_opener()
    {
        var cut = _ctx.Render<ProviderUsagePanel>();
        await cut.InvokeAsync(() => cut.Instance.OpenAsync(new ElementReference("usage-opener")));

        await cut.Instance.DisposeAsync();

        Assert.Contains(
            _ctx.JSInterop.Invocations,
            call => call.Identifier == "BotNexus.modalFocus.deactivate"
                && call.Arguments.Count == 2
                && call.Arguments[1] is false);
    }

    private sealed class UsageHandler : HttpMessageHandler
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
                        "provider": "example",
                        "observedAtUtc": "2026-09-15T00:00:00Z",
                        "limits": [],
                        "burn": {
                          "requests": 1,
                          "failures": 0,
                          "inputTokens": 10,
                          "outputTokens": 5,
                          "models": []
                        }
                      }]
                    }
                    """,
                    System.Text.Encoding.UTF8,
                    "application/json"),
            });
    }
}
