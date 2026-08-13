using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Pages;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Tests for the schema-driven Configuration page save workflow (config-parity PBI 4/6 of #1579,
/// save semantics rewritten by #2059).
/// The page renders the generic SchemaForm fed by GET /api/config/schema instead of the eight
/// hand-written panels. These assert the save workflow persists exactly what the user edited: the
/// batch addresses the dirtied paths and nothing else, so an untouched section is never rewritten
/// from a stale snapshot, and a section absent from the file on disk can still be materialized by
/// editing it.
/// Field-coverage parity is covered by ConfigurationPageSchemaFormTests; widget behaviour by
/// SchemaFormTests.
/// </summary>
public sealed class ConfigurationPageTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    public void Dispose() => _ctx.Dispose();

    /// <summary>
    /// Editing one section must not enlist any other section in the save.
    /// </summary>
    /// <remarks>
    /// Re-points the former <c>SaveAll_does_not_persist_sections_only_present_in_effective_defaults</c>
    /// onto the #2059 contract. Its intent - an untouched section must not be written back - is
    /// preserved and STRENGTHENED: the old assertion allowed the section to escape only because it
    /// was missing from the raw document, so an untouched section that DID exist on disk was still
    /// clobbered. The new assertion is on the batch itself, so it holds regardless of what is on
    /// disk.
    /// </remarks>
    [Fact]
    public void Save_addresses_only_the_edited_path_and_no_other_section()
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

        // Navigate to the Gateway section first -- the sidebar (#1892) renders one section at a
        // time and sections sort alphabetically here (no x-ui-order), so "cron" is the default.
        cut.Find(".config-sidebar-item[data-section='gateway']").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid='field-gateway.listenUrl'] input"));
        cut.Find("[data-testid='field-gateway.listenUrl'] input").Change("http://localhost:9999");

        cut.Find("button.primary").Click();

        cut.WaitForAssertion(() => handler.Patches.ShouldNotBeEmpty());

        var paths = handler.Patches[0]["operations"]!.AsArray()
            .Select(o => o!["path"]!.GetValue<string>()).ToList();

        paths.ShouldBe(["gateway.listenUrl"]);
        paths.ShouldNotContain(p => p.StartsWith("cron", StringComparison.Ordinal));

        // And no whole-section PUT accompanied it: a section-wide write IS the defect.
        handler.SavedSections.ShouldBeEmpty();
    }

    /// <summary>
    /// An edited field persists whether or not its section already exists in the file on disk.
    /// </summary>
    /// <remarks>
    /// Re-points the former <c>SaveAll_persists_section_when_it_exists_in_raw_after_edit</c>. The
    /// original only asserted the in-raw case, because the old implementation could not persist an
    /// absent section at all. This runs BOTH cases: the "absent from raw" half is precisely the
    /// materialization clause of #2059, and would have failed before this change.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Save_persists_an_edited_field_whether_or_not_its_section_is_on_disk(bool sectionInRaw)
    {
        var raw = new JsonObject
        {
            ["gateway"] = new JsonObject { ["listenUrl"] = "http://localhost:5000" }
        };
        if (sectionInRaw)
            raw["cron"] = new JsonObject { ["enabled"] = true, ["tickIntervalSeconds"] = 60 };

        var handler = new FakeConfigApiHandler(
            schema: BuildSchema(),
            effective: new JsonObject
            {
                ["gateway"] = new JsonObject { ["listenUrl"] = "http://localhost:5000" },
                ["cron"] = new JsonObject { ["enabled"] = true, ["tickIntervalSeconds"] = 60 }
            },
            raw: raw);
        ConfigureServices(handler);

        var cut = _ctx.Render<Configuration>();
        cut.WaitForAssertion(() => cut.Find("[data-testid='schema-form']"));

        cut.Find(".config-sidebar-item[data-section='cron']").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid='field-cron.enabled'] input"));
        cut.Find("[data-testid='field-cron.enabled'] input").Change(false);

        cut.Find("button.primary").Click();

        cut.WaitForAssertion(() => handler.Patches.ShouldNotBeEmpty());

        handler.Patches[0]["operations"]!.AsArray()
            .Select(o => o!["path"]!.GetValue<string>())
            .ShouldBe(["cron.enabled"]);
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

        // Sections sort alphabetically (no x-ui-order); navigate to Gateway before editing its field.
        cut.Find(".config-sidebar-item[data-section='gateway']").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid='field-gateway.listenUrl'] input"));
        cut.Find("[data-testid='field-gateway.listenUrl'] input").Change("http://localhost:8888");

        cut.WaitForAssertion(() => cut.Find("button.primary").HasAttribute("disabled").ShouldBeFalse());
    }

    private void ConfigureServices(FakeConfigApiHandler handler)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://gateway.test") };
        _ctx.Services.AddSingleton(new PlatformConfigService(httpClient));
        // #1893: SchemaForm injects IModelOptionsProvider; these save-workflow tests need no models.
        _ctx.Services.AddSingleton<IModelOptionsProvider>(new EmptyModelOptionsProvider());
        _ctx.JSInterop.SetupVoid("", _ => true);
    }

    private sealed class EmptyModelOptionsProvider : IModelOptionsProvider
    {
        public Task<IReadOnlyList<ModelOption>> GetModelsAsync(string provider)
            => Task.FromResult<IReadOnlyList<ModelOption>>([]);
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

        /// <summary>Patch request bodies observed, in order (#2059).</summary>
        public List<JsonObject> Patches { get; } = [];

        public FakeConfigApiHandler(JsonObject schema, JsonObject effective, JsonObject raw)
        {
            _schema = schema;
            _effective = effective;
            _raw = raw;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path == "/api/config/schema" && request.Method == HttpMethod.Get)
                return JsonResponse(_schema);
            if (path == "/api/config/snapshot" && request.Method == HttpMethod.Get)
                return JsonResponse(new JsonObject { ["revision"] = "REV-1", ["config"] = _raw.DeepClone() });
            if (path == "/api/config" && request.Method == HttpMethod.Get)
                return JsonResponse(_effective);
            if (path == "/api/config/raw" && request.Method == HttpMethod.Get)
                return JsonResponse(_raw);
            if (path == "/api/config" && request.Method == HttpMethod.Patch)
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                Patches.Add(JsonNode.Parse(body)!.AsObject());
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
                var section = Uri.UnescapeDataString(path["/api/config/".Length..]);
                SavedSections.Add(section);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"message\":\"ok\"}", Encoding.UTF8, "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage JsonResponse(JsonObject obj) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(obj.ToJsonString(), Encoding.UTF8, "application/json")
            };
    }
}
