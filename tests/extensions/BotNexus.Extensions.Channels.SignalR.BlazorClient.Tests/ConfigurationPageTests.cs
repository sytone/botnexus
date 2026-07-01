using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Pages;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Tests for the schema-driven Configuration page save workflow (config-parity PBI 4/6 of #1579).
/// The page now renders the generic SchemaForm fed by GET /api/config/schema instead of the eight
/// hand-written panels. These assert the save workflow still only persists sections the user
/// dirtied and that exist on disk (raw) -- default-injected-only sections must not be written back.
/// Field-coverage parity is covered by ConfigurationPageSchemaFormTests; widget behaviour by
/// SchemaFormTests.
/// </summary>
public sealed class ConfigurationPageTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void SaveAll_does_not_persist_sections_only_present_in_effective_defaults()
    {
        var handler = new FakeConfigApiHandler(
            schema: BuildSchema(),
            effective: new JsonObject
            {
                ["gateway"] = new JsonObject { ["listenUrl"] = "http://localhost:5000" },
                ["cron"] = new JsonObject { ["enabled"] = true, ["tickIntervalSeconds"] = 60 }
            },
            raw: new JsonObject
            {
                ["gateway"] = new JsonObject { ["listenUrl"] = "http://localhost:5000" }
            });
        ConfigureServices(handler);

        var cut = _ctx.Render<Configuration>();
        cut.WaitForAssertion(() => cut.Find("[data-testid='schema-form']"));

        // Edit a gateway field (gateway is in raw, so it is eligible for save).
        cut.Find("[data-testid='field-gateway.listenUrl'] input").Change("http://localhost:9999");

        cut.Find("button.primary").Click();

        cut.WaitForAssertion(() =>
        {
            // Gateway section is saved (in raw and dirtied).
            handler.SavedSections.ShouldContain("gateway");
            // Cron section is NOT saved (only present in effective defaults, not raw).
            handler.SavedSections.ShouldNotContain("cron");
        });
    }

    [Fact]
    public void SaveAll_persists_section_when_it_exists_in_raw_after_edit()
    {
        var handler = new FakeConfigApiHandler(
            schema: BuildSchema(),
            effective: new JsonObject
            {
                ["gateway"] = new JsonObject { ["listenUrl"] = "http://localhost:5000" },
                ["cron"] = new JsonObject { ["enabled"] = true, ["tickIntervalSeconds"] = 60 }
            },
            raw: new JsonObject
            {
                ["gateway"] = new JsonObject { ["listenUrl"] = "http://localhost:5000" },
                ["cron"] = new JsonObject { ["enabled"] = true, ["tickIntervalSeconds"] = 60 }
            });
        ConfigureServices(handler);

        var cut = _ctx.Render<Configuration>();
        cut.WaitForAssertion(() => cut.Find("[data-testid='schema-form']"));

        // Toggle a cron field; cron exists in raw so it must be persisted.
        cut.Find("[data-testid='field-cron.enabled'] input").Change(false);

        cut.Find("button.primary").Click();

        cut.WaitForAssertion(() =>
        {
            handler.SavedSections.ShouldContain("cron");
        });
    }

    [Fact]
    public void Save_button_disabled_until_an_edit_is_made()
    {
        var handler = new FakeConfigApiHandler(
            schema: BuildSchema(),
            effective: new JsonObject
            {
                ["gateway"] = new JsonObject { ["listenUrl"] = "http://localhost:5000" }
            },
            raw: new JsonObject
            {
                ["gateway"] = new JsonObject { ["listenUrl"] = "http://localhost:5000" }
            });
        ConfigureServices(handler);

        var cut = _ctx.Render<Configuration>();
        cut.WaitForAssertion(() => cut.Find("[data-testid='schema-form']"));

        cut.Find("button.primary").HasAttribute("disabled").ShouldBeTrue();

        cut.Find("[data-testid='field-gateway.listenUrl'] input").Change("http://localhost:8888");

        cut.WaitForAssertion(() => cut.Find("button.primary").HasAttribute("disabled").ShouldBeFalse());
    }

    private void ConfigureServices(FakeConfigApiHandler handler)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://gateway.test") };
        _ctx.Services.AddSingleton(new PlatformConfigService(httpClient));
        _ctx.JSInterop.SetupVoid("", _ => true);
    }

    // Minimal schema covering the gateway + cron fields these tests edit.
    private static JsonObject BuildSchema() => new()
    {
        ["schemaVersion"] = "1.0",
        ["root"] = "PlatformConfig",
        ["schema"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["gateway"] = new JsonObject
                {
                    ["type"] = "object",
                    ["x-ui-label"] = "Gateway",
                    ["properties"] = new JsonObject
                    {
                        ["listenUrl"] = Scalar("string", "text", "Listen URL"),
                    },
                },
                ["cron"] = new JsonObject
                {
                    ["type"] = "object",
                    ["x-ui-label"] = "Cron",
                    ["properties"] = new JsonObject
                    {
                        ["enabled"] = Scalar("boolean", "toggle", "Enabled"),
                        ["tickIntervalSeconds"] = Scalar("integer", "number", "Tick Interval"),
                    },
                },
            },
        },
    };

    private static JsonObject Scalar(string type, string widget, string label) =>
        new() { ["type"] = type, ["x-ui-widget"] = widget, ["x-ui-label"] = label };

    internal sealed class FakeConfigApiHandler : HttpMessageHandler
    {
        private readonly JsonObject _schema;
        private readonly JsonObject _effective;
        private readonly JsonObject _raw;
        public List<string> SavedSections { get; } = [];

        public FakeConfigApiHandler(JsonObject schema, JsonObject effective, JsonObject raw)
        {
            _schema = schema;
            _effective = effective;
            _raw = raw;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path == "/api/config/schema" && request.Method == HttpMethod.Get)
                return Task.FromResult(JsonResponse(_schema));
            if (path == "/api/config" && request.Method == HttpMethod.Get)
                return Task.FromResult(JsonResponse(_effective));
            if (path == "/api/config/raw" && request.Method == HttpMethod.Get)
                return Task.FromResult(JsonResponse(_raw));
            if (path.StartsWith("/api/config/", StringComparison.Ordinal) && request.Method == HttpMethod.Put)
            {
                var section = Uri.UnescapeDataString(path["/api/config/".Length..]);
                SavedSections.Add(section);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"message\":\"ok\"}", Encoding.UTF8, "application/json")
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage JsonResponse(JsonObject obj) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(obj.ToJsonString(), Encoding.UTF8, "application/json")
            };
    }
}
