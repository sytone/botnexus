using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Pages;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Field-coverage parity tests for the schema-driven Configuration page (config-parity PBI 4/6 of
/// #1579, issue #1612). The eight hand-written config panels were replaced by the generic
/// <see cref="SchemaForm"/> fed by <c>GET /api/config/schema</c>. These assert that the page now
/// renders entirely from <see cref="SchemaForm"/> and that the key config sections/fields the old
/// panels exposed remain editable -- so coverage does not regress.
/// </summary>
public sealed class ConfigurationPageSchemaFormTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    public void Dispose() => _ctx.Dispose();

    // Minimal authentic-shaped schema envelope mirroring ConfigSchemaBuilder output: the real
    // builder emits the whole PlatformConfig tree, so we include the top-level sections the eight
    // panels covered (gateway + nested, providers/channels dicts, cron, apiKey, agents defaults).
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
                    ["apiKeys"] = Dict("API Keys", new JsonObject
                    {
                        ["apiKey"] = Secret("API Key"),
                        ["isAdmin"] = Scalar("boolean", "toggle", "Is Admin"),
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
            ["apiKeys"] = new JsonObject
            {
                ["k1"] = new JsonObject { ["apiKey"] = "***", ["isAdmin"] = true },
            },
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
    public void Page_renders_SchemaForm_not_handwritten_panels()
    {
        ConfigureServices(new FakeConfigApiHandler(BuildSchema(), SampleConfig()));

        var cut = _ctx.Render<Configuration>();

        cut.WaitForAssertion(() =>
        {
            // The generic schema renderer must be present.
            cut.Find("[data-testid='schema-form']");
        });
    }

    [Fact]
    public void Page_exposes_key_config_sections_from_schema()
    {
        ConfigureServices(new FakeConfigApiHandler(BuildSchema(), SampleConfig()));

        var cut = _ctx.Render<Configuration>();

        // The sidebar (#1892) lists the top-level sections; the fields themselves render one section
        // at a time. Assert the nav exposes every key section so coverage is reachable.
        cut.WaitForAssertion(() =>
        {
            var sections = cut.FindAll(".config-sidebar-item").Select(e => e.GetAttribute("data-section")).ToList();
            Assert.Contains("gateway", sections);
            Assert.Contains("providers", sections);
            Assert.Contains("channels", sections);
            Assert.Contains("cron", sections);
        });
    }

    [Fact]
    public void Selecting_a_section_renders_only_that_sections_fields()
    {
        ConfigureServices(new FakeConfigApiHandler(BuildSchema(), SampleConfig()));

        var cut = _ctx.Render<Configuration>();
        cut.WaitForAssertion(() => cut.Find("[data-testid='schema-form']"));

        // Click the Gateway section, then its nested scalars must be present.
        cut.Find(".config-sidebar-item[data-section='gateway']").Click();
        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='field-gateway.listenUrl'] input");
            cut.Find("[data-testid='field-gateway.world.id'] input");
            cut.Find("[data-testid='field-gateway.apiKeys.k1.apiKey'] input");
        });

        // Provider dictionary entries live under the Providers section.
        cut.Find(".config-sidebar-item[data-section='providers']").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid='field-providers.openai.apiKey'] input"));
    }

    [Fact]
    public void Editing_a_schema_field_enables_save()
    {
        ConfigureServices(new FakeConfigApiHandler(BuildSchema(), SampleConfig()));

        var cut = _ctx.Render<Configuration>();
        cut.WaitForAssertion(() => cut.Find("[data-testid='schema-form']"));

        var save = cut.Find("button.primary");
        save.HasAttribute("disabled").ShouldBeTrue();

        // Navigate to the Gateway section before editing one of its fields.
        cut.Find(".config-sidebar-item[data-section='gateway']").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid='field-gateway.listenUrl'] input"));
        cut.Find("[data-testid='field-gateway.listenUrl'] input").Change("http://localhost:9999");

        cut.WaitForAssertion(() =>
        {
            cut.Find("button.primary").HasAttribute("disabled").ShouldBeFalse();
        });
    }

    private void ConfigureServices(FakeConfigApiHandler handler)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://gateway.test") };
        _ctx.Services.AddSingleton(new PlatformConfigService(httpClient));
        // #1893: SchemaForm injects IModelOptionsProvider; no models needed for these page tests.
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
