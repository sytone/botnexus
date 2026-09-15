using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Pages;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class SecretsConfigurationTests : IDisposable
{
    private const string Sentinel = "sentinel-super-secret-value";
    private readonly BunitContext _ctx = new();

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void Secrets_section_lists_metadata_without_rendering_secret_content()
    {
        var handler = new SecretsApiHandler();
        Configure(handler);

        var cut = _ctx.Render<Configuration>();
        cut.WaitForAssertion(() => cut.Find(".config-sidebar-item[data-section='secrets']"));
        cut.Find(".config-sidebar-item[data-section='secrets']").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.ShouldContain("EXAMPLE_TOKEN");
            cut.Markup.ShouldContain("27 bytes");
            cut.Markup.ShouldContain("Created");
            cut.Markup.ShouldContain("Modified");
            cut.Markup.ShouldNotContain(Sentinel);
            cut.Markup.ShouldNotContain("sentinel-super");
            cut.Markup.ShouldNotContain(new string('*', Sentinel.Length));
        });
    }

    [Fact]
    public void Overwrite_form_starts_empty_and_sends_the_complete_new_value()
    {
        var handler = new SecretsApiHandler();
        Configure(handler);

        var cut = _ctx.Render<Configuration>();
        cut.WaitForAssertion(() => cut.Find(".config-sidebar-item[data-section='secrets']"));
        cut.Find(".config-sidebar-item[data-section='secrets']").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid='secret-value']"));

        var value = cut.Find("[data-testid='secret-value']");
        value.GetAttribute("value").ShouldBeNullOrEmpty();
        cut.Markup.ShouldNotContain(Sentinel);
        cut.Markup.ShouldNotContain(new string('*', Sentinel.Length));

        cut.Find("[data-testid='secret-key']").Change("EXAMPLE_TOKEN");
        cut.Find("[data-testid='secret-value']").Change("complete-new-value");
        cut.Find("[data-testid='secret-save']").Click();

        cut.WaitForAssertion(() => handler.Writes.ShouldBe([("EXAMPLE_TOKEN", "complete-new-value")]));
    }

    [Fact]
    public void Deleting_a_secret_calls_the_delete_endpoint_and_refreshes_the_list()
    {
        var handler = new SecretsApiHandler();
        Configure(handler);

        var cut = _ctx.Render<Configuration>();
        cut.WaitForAssertion(() => cut.Find(".config-sidebar-item[data-section='secrets']"));
        cut.Find(".config-sidebar-item[data-section='secrets']").Click();
        cut.WaitForAssertion(() => cut.Find("button[aria-label='Delete EXAMPLE_TOKEN']"));
        cut.Find("button[aria-label='Delete EXAMPLE_TOKEN']").Click();

        cut.WaitForAssertion(() => handler.Deletes.ShouldBe(["EXAMPLE_TOKEN"]));
    }

    [Fact]
    public void Invalid_key_rejection_is_shown_to_the_operator()
    {
        var handler = new SecretsApiHandler { RejectWrites = true };
        Configure(handler);

        var cut = _ctx.Render<Configuration>();
        cut.WaitForAssertion(() => cut.Find(".config-sidebar-item[data-section='secrets']"));
        cut.Find(".config-sidebar-item[data-section='secrets']").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid='secret-key']"));
        cut.Find("[data-testid='secret-key']").Change("../bad");
        cut.Find("[data-testid='secret-value']").Change("value");
        cut.Find("[data-testid='secret-save']").Click();

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Keys must match"));
    }

    private void Configure(SecretsApiHandler handler)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://gateway.test") };
        _ctx.Services.AddSingleton(new PlatformConfigService(httpClient));
        _ctx.Services.AddSingleton<IModelOptionsProvider>(new EmptyModelOptionsProvider());
        _ctx.JSInterop.SetupVoid("", _ => true);
    }

    private sealed class EmptyModelOptionsProvider : IModelOptionsProvider
    {
        public Task<IReadOnlyList<ModelOption>> GetModelsAsync(string provider) =>
            Task.FromResult<IReadOnlyList<ModelOption>>([]);
    }

    private sealed class SecretsApiHandler : HttpMessageHandler
    {
        public bool RejectWrites { get; init; }
        public List<(string Key, string Value)> Writes { get; } = [];
        public List<string> Deletes { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path == "/api/config/schema")
                return Json("{\"schema\":{\"type\":\"object\",\"properties\":{\"gateway\":{\"type\":\"object\",\"x-ui-label\":\"Gateway\",\"properties\":{}}}}}");
            if (path == "/api/config/snapshot")
                return Json("{\"revision\":\"REV-1\",\"config\":{\"gateway\":{}}}");
            if (path == "/api/config")
                return Json("{\"gateway\":{}}");
            if (path == "/api/secrets" && request.Method == HttpMethod.Get)
                return Json("[{\"key\":\"EXAMPLE_TOKEN\",\"createdUtc\":\"2026-09-15T01:02:03Z\",\"modifiedUtc\":\"2026-09-15T04:05:06Z\",\"sizeBytes\":27}]");
            if (path.StartsWith("/api/secrets/", StringComparison.Ordinal) && request.Method == HttpMethod.Put)
            {
                if (RejectWrites)
                    return Json("{\"error\":\"'../bad' is not a valid secret key. Keys must match ^[A-Za-z0-9._-]{1,128}$.\"}", HttpStatusCode.BadRequest);

                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
                var value = body["Value"]?.GetValue<string>() ?? body["value"]?.GetValue<string>();
                value.ShouldNotBeNull();
                Writes.Add((Uri.UnescapeDataString(path["/api/secrets/".Length..]), value));
                return Json("{\"key\":\"EXAMPLE_TOKEN\",\"createdUtc\":\"2026-09-15T01:02:03Z\",\"modifiedUtc\":\"2026-09-15T04:05:06Z\",\"sizeBytes\":18}");
            }
            if (path.StartsWith("/api/secrets/", StringComparison.Ordinal) && request.Method == HttpMethod.Delete)
            {
                Deletes.Add(Uri.UnescapeDataString(path["/api/secrets/".Length..]));
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
