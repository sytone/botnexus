using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile.Pages;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Mobile config-parity tests for the schema-driven mobile <see cref="Settings"/> page
/// (config-parity PBI 6/6 of #1579, issue #1615 -- the payoff PBI). The mobile Settings page
/// consumes the SAME shared <see cref="SchemaForm"/> renderer the desktop Configuration page uses,
/// fed by <c>GET /api/config/schema</c>, so there is no mobile-specific field code. These assert the
/// page renders entirely from <see cref="SchemaForm"/>, surfaces config sections from the schema, and
/// round-trips saves through the existing <c>PUT /api/config/{section}</c> endpoint (the same hot-reload
/// path the desktop uses). Mirrors <c>ConfigurationPageSchemaFormTests</c> for parity.
/// </summary>
public sealed class MobileSettingsPageTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    public void Dispose() => _ctx.Dispose();

    // Minimal authentic-shaped schema envelope mirroring ConfigSchemaBuilder output: the real builder
    // emits the whole PlatformConfig tree, so we include representative top-level sections (gateway +
    // nested, providers/channels dicts, cron, apiKey) to prove the shared renderer surfaces them.
    private static JsonObject BuildSchema() => new()
    {
        ["schemaVersion"] = "1.0",
        ["root"] = "PlatformConfig",
        ["schema"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["version"] = Scalar("integer", "number", "Config schema version"),
                ["apiKey"] = Secret("Global API Key"),
                ["gateway"] = Obj("Gateway", new JsonObject
                {
                    ["listenUrl"] = Scalar("string", "text", "Listen URL"),
                    ["logLevel"] = Scalar("string", "select", "Log Level"),
                    ["world"] = Obj("World Identity", new JsonObject
                    {
                        ["id"] = Scalar("string", "text", "World ID"),
                        ["name"] = Scalar("string", "text", "World Name"),
                    }),
                }),
                ["providers"] = Dict("Providers", new JsonObject
                {
                    ["enabled"] = Scalar("boolean", "toggle", "Enabled"),
                    ["apiKey"] = Secret("API Key"),
                }),
                ["channels"] = Dict("Channels", new JsonObject
                {
                    ["type"] = Scalar("string", "text", "Channel Type"),
                    ["enabled"] = Scalar("boolean", "toggle", "Enabled"),
                }),
                ["cron"] = Obj("Cron", new JsonObject
                {
                    ["enabled"] = Scalar("boolean", "toggle", "Enabled"),
                    ["tickIntervalSeconds"] = Scalar("integer", "number", "Tick Interval"),
                }),
            },
        },
    };

    private static JsonObject SampleConfig() => new()
    {
        ["version"] = 1,
        ["apiKey"] = "***",
        ["gateway"] = new JsonObject
        {
            ["listenUrl"] = "http://localhost:5005",
            ["logLevel"] = "Information",
            ["world"] = new JsonObject { ["id"] = "w1", ["name"] = "World" },
        },
        ["providers"] = new JsonObject
        {
            ["openai"] = new JsonObject { ["enabled"] = true, ["apiKey"] = "***" },
        },
        ["channels"] = new JsonObject
        {
            ["signalr"] = new JsonObject { ["type"] = "signalr", ["enabled"] = true },
        },
        ["cron"] = new JsonObject { ["enabled"] = true, ["tickIntervalSeconds"] = 60 },
    };

    [Fact]
    public void Page_renders_shared_SchemaForm_not_mobile_specific_fields()
    {
        ConfigureServices(new FakeConfigApiHandler(BuildSchema(), SampleConfig()));

        var cut = _ctx.Render<Settings>();

        cut.WaitForAssertion(() =>
        {
            // The mobile page must render through the SAME shared schema renderer as desktop
            // (no mobile-specific field code): AC #2 + AC #4 of issue #1615.
            cut.Find("[data-testid='schema-form']");
        });
    }

    [Fact]
    public void Page_exposes_config_sections_from_schema()
    {
        ConfigureServices(new FakeConfigApiHandler(BuildSchema(), SampleConfig()));

        var cut = _ctx.Render<Settings>();

        cut.WaitForAssertion(() =>
        {
            // Top-level scalar (version) and the secret global apiKey.
            cut.Find("[data-testid='field-version'] input");
            cut.Find("[data-testid='field-apiKey'] input");
            // Gateway nested scalars.
            cut.Find("[data-testid='field-gateway.listenUrl'] input");
            cut.Find("[data-testid='field-gateway.world.id'] input");
            // Provider + channel dictionary entries.
            cut.Find("[data-testid='field-providers.openai.apiKey'] input");
            cut.Find("[data-testid='field-channels.signalr.type'] input");
            // Cron global settings.
            cut.Find("[data-testid='field-cron.tickIntervalSeconds'] input");
        });
    }

    [Fact]
    public void Editing_a_schema_field_enables_save()
    {
        ConfigureServices(new FakeConfigApiHandler(BuildSchema(), SampleConfig()));

        var cut = _ctx.Render<Settings>();
        cut.WaitForAssertion(() => cut.Find("[data-testid='schema-form']"));

        var save = cut.Find("button.primary");
        save.HasAttribute("disabled").ShouldBeTrue();

        cut.Find("[data-testid='field-gateway.listenUrl'] input").Change("http://localhost:9999");

        cut.WaitForAssertion(() =>
        {
            cut.Find("button.primary").HasAttribute("disabled").ShouldBeFalse();
        });
    }

    [Fact]
    public void Saving_edits_round_trips_through_put_config_section()
    {
        // AC #3 + AC #5 of issue #1615: mobile save must go through the existing per-section
        // PUT /api/config/{section} endpoint (the same hot-reload-without-restart path the desktop
        // uses -- no mobile-specific save code).
        var handler = new FakeConfigApiHandler(BuildSchema(), SampleConfig());
        ConfigureServices(handler);

        var cut = _ctx.Render<Settings>();
        cut.WaitForAssertion(() => cut.Find("[data-testid='schema-form']"));

        // Edit a materialised section, then save.
        cut.Find("[data-testid='field-gateway.listenUrl'] input").Change("http://localhost:9999");
        cut.WaitForAssertion(() => cut.Find("button.primary").HasAttribute("disabled").ShouldBeFalse());
        cut.Find("button.primary").Click();

        // The edited "gateway" section must have been PUT back to the config API.
        cut.WaitForAssertion(() => handler.SavedSections.ShouldContain("gateway"));
    }

    private void ConfigureServices(FakeConfigApiHandler handler)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://gateway.test") };
        _ctx.Services.AddSingleton(new PlatformConfigService(httpClient));
        // #1893: the shared SchemaForm injects IModelOptionsProvider; register an empty stub.
        _ctx.Services.AddSingleton<IModelOptionsProvider>(new EmptyModelOptionsProvider());
        _ctx.JSInterop.SetupVoid("", _ => true);
    }

    private sealed class EmptyModelOptionsProvider : IModelOptionsProvider
    {
        public Task<IReadOnlyList<ModelOption>> GetModelsAsync(string provider)
            => Task.FromResult<IReadOnlyList<ModelOption>>([]);
    }

    private static JsonObject Scalar(string type, string widget, string label) =>
        new() { ["type"] = type, ["x-ui-widget"] = widget, ["x-ui-label"] = label };

    private static JsonObject Secret(string label) =>
        new() { ["type"] = "string", ["x-ui-widget"] = "secret", ["x-ui-secret"] = true, ["x-ui-label"] = label };

    private static JsonObject Obj(string label, JsonObject properties) =>
        new() { ["type"] = "object", ["x-ui-label"] = label, ["properties"] = properties };

    private static JsonObject Dict(string label, JsonObject valueProperties) =>
        new()
        {
            ["type"] = "object",
            ["x-ui-label"] = label,
            ["additionalProperties"] = new JsonObject { ["type"] = "object", ["properties"] = valueProperties },
        };

    internal sealed class FakeConfigApiHandler : HttpMessageHandler
    {
        private readonly JsonObject _schema;
        private readonly JsonObject _config;

        public List<string> SavedSections { get; } = [];

        public FakeConfigApiHandler(JsonObject schema, JsonObject config)
        {
            _schema = schema;
            _config = config;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            if (path == "/api/config/schema" && request.Method == HttpMethod.Get)
                return Task.FromResult(Json(_schema));
            if (path == "/api/config" && request.Method == HttpMethod.Get)
                return Task.FromResult(Json(_config));
            if (path == "/api/config/raw" && request.Method == HttpMethod.Get)
                return Task.FromResult(Json(_config));
            if (path.StartsWith("/api/config/", StringComparison.Ordinal) && request.Method == HttpMethod.Put)
            {
                SavedSections.Add(Uri.UnescapeDataString(path["/api/config/".Length..]));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"message\":\"ok\"}", Encoding.UTF8, "application/json"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Json(JsonObject obj) =>
            new(HttpStatusCode.OK) { Content = new StringContent(obj.ToJsonString(), Encoding.UTF8, "application/json") };
    }
}
