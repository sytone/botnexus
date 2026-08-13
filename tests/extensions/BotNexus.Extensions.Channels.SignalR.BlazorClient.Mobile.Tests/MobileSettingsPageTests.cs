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
    public void Saving_edits_patches_only_the_edited_path_with_the_loaded_revision()
    {
        // Re-points the former "round trips through PUT /api/config/{section}" test onto the save
        // contract issue #2059 replaced it with. The original intent -- mobile must save through the
        // shared client seam, with no mobile-specific save code -- is preserved and STRENGTHENED:
        // it is no longer enough that the edited section was written; the batch must contain ONLY
        // the edited path and must quote the revision the page loaded, which is what stops a
        // one-field edit from reverting a concurrent change elsewhere.
        var handler = new FakeConfigApiHandler(BuildSchema(), SampleConfig());
        ConfigureServices(handler);

        var cut = _ctx.Render<Settings>();
        cut.WaitForAssertion(() => cut.Find("[data-testid='schema-form']"));

        cut.Find("[data-testid='field-gateway.listenUrl'] input").Change("http://localhost:9999");
        cut.WaitForAssertion(() => cut.Find("button.primary").HasAttribute("disabled").ShouldBeFalse());
        cut.Find("button.primary").Click();

        cut.WaitForAssertion(() => handler.Patches.ShouldNotBeEmpty());

        var patch = handler.Patches[0];
        patch["expectedRevision"]!.GetValue<string>().ShouldBe(FakeConfigApiHandler.Revision);

        var paths = patch["operations"]!.AsArray().Select(o => o!["path"]!.GetValue<string>()).ToList();
        paths.ShouldBe(["gateway.listenUrl"]);

        // No section-wide PUT may accompany the patch: a whole-section write is the defect.
        handler.SavedSections.ShouldBeEmpty();
    }

    /// <summary>
    /// A save rejected as a conflict must NOT be reported as success and must not clear the dirty
    /// state - the operator's edits are still unsaved (#2059).
    /// </summary>
    [Fact]
    public void Conflicting_save_reports_a_conflict_and_keeps_the_edits_pending()
    {
        var handler = new FakeConfigApiHandler(BuildSchema(), SampleConfig()) { ConflictOnPatch = true };
        ConfigureServices(handler);

        var cut = _ctx.Render<Settings>();
        cut.WaitForAssertion(() => cut.Find("[data-testid='schema-form']"));

        cut.Find("[data-testid='field-gateway.listenUrl'] input").Change("http://localhost:9999");
        cut.WaitForAssertion(() => cut.Find("button.primary").HasAttribute("disabled").ShouldBeFalse());
        cut.Find("button.primary").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.ShouldContain("changed elsewhere");
            // Still dirty: the Save button stays enabled so the edits can be re-applied.
            cut.Find("button.primary").HasAttribute("disabled").ShouldBeFalse();
        });
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
        internal const string Revision = "REV-1";

        private readonly JsonObject _schema;
        private readonly JsonObject _config;

        public List<string> SavedSections { get; } = [];

        /// <summary>Patch request bodies observed, in order (#2059).</summary>
        public List<JsonObject> Patches { get; } = [];

        /// <summary>When set, the patch endpoint answers 409 to exercise the conflict path.</summary>
        public bool ConflictOnPatch { get; init; }

        public FakeConfigApiHandler(JsonObject schema, JsonObject config)
        {
            _schema = schema;
            _config = config;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            if (path == "/api/config/schema" && request.Method == HttpMethod.Get)
                return Json(_schema);
            if (path == "/api/config/snapshot" && request.Method == HttpMethod.Get)
                return Json(new JsonObject { ["revision"] = Revision, ["config"] = _config.DeepClone() });
            if (path == "/api/config" && request.Method == HttpMethod.Get)
                return Json(_config);
            if (path == "/api/config/raw" && request.Method == HttpMethod.Get)
                return Json(_config);
            if (path == "/api/config" && request.Method == HttpMethod.Patch)
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                Patches.Add(JsonNode.Parse(body)!.AsObject());

                if (ConflictOnPatch)
                {
                    return new HttpResponseMessage(HttpStatusCode.Conflict)
                    {
                        Content = new StringContent(
                            "{\"success\":false,\"revision\":\"REV-2\",\"errors\":[\"stale\"]}",
                            Encoding.UTF8,
                            "application/json"),
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"success\":true,\"revision\":\"REV-2\",\"errors\":[]}",
                        Encoding.UTF8,
                        "application/json"),
                };
            }
            if (path.StartsWith("/api/config/", StringComparison.Ordinal) && request.Method == HttpMethod.Put)
            {
                SavedSections.Add(Uri.UnescapeDataString(path["/api/config/".Length..]));
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"message\":\"ok\"}", Encoding.UTF8, "application/json"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(JsonObject obj) =>
            new(HttpStatusCode.OK) { Content = new StringContent(obj.ToJsonString(), Encoding.UTF8, "application/json") };
    }
}
