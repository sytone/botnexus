using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Tests that PlatformConfigService correctly routes display reads to /api/config (effective)
/// and raw/edit reads to /api/config/raw.
/// </summary>
public sealed class PlatformConfigServiceTests
{
    [Fact]
    public async Task LoadAsync_calls_effective_config_endpoint()
    {
        var handler = new ConfigApiHandler();
        handler.SetEffective(new JsonObject { ["cron"] = new JsonObject { ["enabled"] = true } });
        var svc = CreateService(handler);

        var result = await svc.LoadAsync();

        result.ShouldNotBeNull();
        handler.LastPath.ShouldBe("/api/config");
    }

    [Fact]
    public async Task LoadRawAsync_calls_raw_config_endpoint()
    {
        var handler = new ConfigApiHandler();
        handler.SetRaw(new JsonObject { ["gateway"] = new JsonObject { ["listenUrl"] = "http://localhost:5000" } });
        var svc = CreateService(handler);

        var result = await svc.LoadRawAsync();

        result.ShouldNotBeNull();
        handler.LastPath.ShouldBe("/api/config/raw");
    }

    [Fact]
    public async Task LoadAsync_returns_effective_cron_enabled_even_when_raw_has_no_cron()
    {
        var handler = new ConfigApiHandler();
        handler.SetEffective(new JsonObject
        {
            ["gateway"] = new JsonObject { ["listenUrl"] = "http://localhost:5000" },
            ["cron"] = new JsonObject { ["enabled"] = true, ["tickIntervalSeconds"] = 60 }
        });
        handler.SetRaw(new JsonObject
        {
            ["gateway"] = new JsonObject { ["listenUrl"] = "http://localhost:5000" }
            // Note: no cron section in raw
        });
        var svc = CreateService(handler);

        var effective = await svc.LoadAsync();
        var raw = await svc.LoadRawAsync();

        // Effective shows cron enabled (from defaults)
        effective!["cron"]!["enabled"]!.GetValue<bool>().ShouldBeTrue();
        // Raw has no cron section
        raw!["cron"].ShouldBeNull();
    }

    [Fact]
    public async Task SaveSectionAsync_sends_put_to_section_endpoint()
    {
        var handler = new ConfigApiHandler();
        var svc = CreateService(handler);

        var (success, _) = await svc.SaveSectionAsync("cron", new JsonObject { ["enabled"] = false });

        success.ShouldBeTrue();
        handler.LastMethod.ShouldBe(HttpMethod.Put);
        handler.LastPath.ShouldBe("/api/config/cron");
    }

    private static PlatformConfigService CreateService(ConfigApiHandler handler)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://gateway.test") };
        return new PlatformConfigService(httpClient);
    }

    internal sealed class ConfigApiHandler : HttpMessageHandler
    {
        private JsonObject? _effective;
        private JsonObject? _raw;

        public string LastPath { get; private set; } = string.Empty;
        public HttpMethod LastMethod { get; private set; } = HttpMethod.Get;

        public void SetEffective(JsonObject obj) => _effective = obj;
        public void SetRaw(JsonObject obj) => _raw = obj;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastPath = request.RequestUri?.AbsolutePath ?? string.Empty;
            LastMethod = request.Method;

            if (LastPath == "/api/config" && request.Method == HttpMethod.Get && _effective is not null)
                return Respond(_effective);

            if (LastPath == "/api/config/raw" && request.Method == HttpMethod.Get && _raw is not null)
                return Respond(_raw);

            if (LastPath.StartsWith("/api/config/", StringComparison.Ordinal) && request.Method == HttpMethod.Put)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"message\":\"ok\"}", Encoding.UTF8, "application/json")
                });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> Respond(JsonObject obj)
        {
            var json = obj.ToJsonString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}
