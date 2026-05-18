using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Pages;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Tests that the Configuration page:
/// - Displays effective state (cron shows enabled even when missing from raw)
/// - Save workflow only persists user-dirtied sections, not default-injected sections
/// </summary>
public sealed class ConfigurationPageTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void Cron_panel_shows_enabled_from_effective_config_even_when_raw_has_no_cron()
    {
        var handler = new FakeConfigApiHandler(
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

        var cut = _ctx.Render<Configuration>(parameters =>
            parameters.Add(p => p.Section, "cron"));

        cut.WaitForAssertion(() =>
        {
            // The enabled checkbox should be present with checked attribute (effective cron.enabled = true)
            var checkbox = cut.Find("input[type='checkbox']");
            checkbox.ShouldNotBeNull();
            checkbox.HasAttribute("checked").ShouldBeTrue();
        });
    }

    [Fact]
    public void SaveAll_does_not_persist_sections_only_present_in_effective_defaults()
    {
        var handler = new FakeConfigApiHandler(
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

        var cut = _ctx.Render<Configuration>(parameters =>
            parameters.Add(p => p.Section, "gateway"));

        cut.WaitForAssertion(() => cut.Markup.ShouldNotContain("Loading"));

        // Simulate user editing the gateway section only
        // (gateway is in raw, so it's eligible for save)
        // Find any input and change it to trigger dirty
        var inputs = cut.FindAll("input[type='text']");
        if (inputs.Count > 0)
        {
            inputs[0].Change("http://localhost:9999");
        }

        // Click save
        var saveBtn = cut.Find("button.primary");
        saveBtn.Click();

        cut.WaitForAssertion(() =>
        {
            // Gateway section should be saved (it's in raw and was dirtied)
            handler.SavedSections.ShouldContain("gateway");
            // Cron section should NOT be saved (only in effective, user didn't touch it)
            handler.SavedSections.ShouldNotContain("cron");
        });
    }

    [Fact]
    public void SaveAll_persists_section_when_user_explicitly_edits_default_only_section()
    {
        var handler = new FakeConfigApiHandler(
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

        // Navigate to cron panel and edit it
        var cut = _ctx.Render<Configuration>(parameters =>
            parameters.Add(p => p.Section, "cron"));

        cut.WaitForAssertion(() => cut.Markup.ShouldNotContain("Loading"));

        // Toggle the enabled checkbox to dirty the cron section
        var checkbox = cut.Find("input[type='checkbox']");
        checkbox.Change(false);

        // Click save
        var saveBtn = cut.Find("button.primary");
        saveBtn.Click();

        cut.WaitForAssertion(() =>
        {
            // Cron SHOULD be saved because user explicitly edited it
            handler.SavedSections.ShouldContain("cron");
        });
    }

    private void ConfigureServices(FakeConfigApiHandler handler)
    {
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://gateway.test")
        };

        _ctx.Services.AddSingleton(new PlatformConfigService(httpClient));
        _ctx.Services.AddSingleton(new CronApiClient(httpClient));
        _ctx.JSInterop.SetupVoid("", _ => true);
    }

    internal sealed class FakeConfigApiHandler : HttpMessageHandler
    {
        private readonly JsonObject _effective;
        private readonly JsonObject _raw;

        public List<string> SavedSections { get; } = [];

        public FakeConfigApiHandler(JsonObject effective, JsonObject raw)
        {
            _effective = effective;
            _raw = raw;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            if (path == "/api/config" && request.Method == HttpMethod.Get)
                return JsonResponse(_effective);

            if (path == "/api/config/raw" && request.Method == HttpMethod.Get)
                return JsonResponse(_raw);

            if (path == "/api/cron" && request.Method == HttpMethod.Get)
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("[]", Encoding.UTF8, "application/json")
                };

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
