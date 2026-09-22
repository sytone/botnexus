using System.Net;
using System.Text;
using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class ExtensionRepositoriesSectionTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void Empty_state_shows_trust_warning_and_requires_ack_before_submit()
    {
        var handler = new FakeHandler("[]");
        var cut = Render(handler);
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("No extension repositories are registered."));
        cut.Markup.ShouldContain("full gateway trust");
        cut.Find("[data-testid='repository-add']").HasAttribute("disabled").ShouldBeTrue();
        cut.Find("[data-testid='trust-ack']").Change(true);
        cut.Find("[data-testid='repository-add']").HasAttribute("disabled").ShouldBeFalse();
    }

    [Fact]
    public void Populated_state_shows_truthful_status_and_disabled_sync()
    {
        var json = """[ {"id":"tools","repositoryUrl":"https://example.test/tools.git","requestedRef":"main","enabled":true,"updatesEnabled":true,"reconciliationStatus":"not-yet-reconciled","syncAvailable":false } ]""";
        var handler = new FakeHandler(json);
        var cut = Render(handler);
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("tools"));
        cut.Markup.ShouldContain("Not yet reconciled");
        cut.Markup.ShouldContain("Unavailable until reconciliation");
        cut.Find("[data-testid='sync-tools']").HasAttribute("disabled").ShouldBeTrue();
        cut.Find("[data-testid='toggle-tools']").Click();
        cut.WaitForAssertion(() => handler.Methods.ShouldContain("PUT"));
    }

    [Fact]
    public void Validation_error_is_shown_without_posting()
    {
        var handler = new FakeHandler("[]");
        var cut = Render(handler);
        cut.WaitForAssertion(() => cut.Find("[data-testid='trust-ack']").Change(true));
        cut.Find("[data-testid='repository-id']").Change("Bad ID");
        cut.Find("[data-testid='repository-url']").Change("not-a-url");
        cut.Find("[data-testid='repository-ref']").Change("");
        cut.Find("[data-testid='repository-add']").Click();
        cut.Markup.ShouldContain("role=\"alert\"");
        handler.Methods.ShouldBe(["GET"]);
    }

    private IRenderedComponent<ExtensionRepositoriesSection> Render(FakeHandler handler)
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://gateway.test") };
        _ctx.Services.AddSingleton(new PlatformConfigService(client));
        return _ctx.Render<ExtensionRepositoriesSection>();
    }

    private sealed class FakeHandler(string listJson) : HttpMessageHandler
    {
        public List<string> Methods { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Methods.Add(request.Method.Method);
            var body = request.Method == HttpMethod.Get ? listJson : """{}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
