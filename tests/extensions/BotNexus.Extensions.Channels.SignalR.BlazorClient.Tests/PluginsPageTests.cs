using System.Net;
using System.Text.Json;
using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Pages;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;

// The page component's simple name collides with the BotNexus.Extensions.Plugins namespace, which
// this test project sees transitively. Aliasing keeps the page's name matching its route without
// forcing every reference through a fully-qualified name.
using PluginsPage = BotNexus.Extensions.Channels.SignalR.BlazorClient.Pages.Plugins;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Tests the routed portal plugins page (#2687, slice 8 of #2623): the installed-plugin row set,
/// the version / update / trust columns, the per-plugin auto-update toggle, and the route
/// parameter that makes a selected plugin addressable.
/// </summary>
/// <remarks>
/// The unknown-id case has its own test because it is the clause that has been got wrong before:
/// an id with no matching plugin must degrade to the unselected list with a non-fatal notice, not
/// to an error page or an empty screen.
/// </remarks>
public sealed class PluginsPageTests : IDisposable
{
    private const string AlphaName = "alpha-plugin";
    private const string BetaName = "beta-plugin";

    private readonly BunitContext _ctx = new();
    private readonly PluginsMockHandler _handler = new();

    public PluginsPageTests()
    {
        var httpClient = new HttpClient(_handler) { BaseAddress = new Uri("http://localhost/") };
        _ctx.Services.AddSingleton(httpClient);
        _ctx.Services.AddScoped<PluginsApiClient>();
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    // ── AC1: name, installed version and trust state per plugin ──────────────

    [Fact]
    public void Lists_each_installed_plugin_with_name_version_and_trust_state()
    {
        SetupTwoPlugins();

        var cut = RenderPage();

        var rows = cut.FindAll("[data-testid='plugin-row']");
        Assert.Equal(2, rows.Count);
        Assert.Equal(AlphaName, rows[0].GetAttribute("data-plugin-id"));
        Assert.Equal(BetaName, rows[1].GetAttribute("data-plugin-id"));

        var versions = cut.FindAll("[data-testid='plugin-version']");
        Assert.Equal("1.4.0", versions[0].TextContent.Trim());
        // Beta advertises no manifest version, so the row falls back to the short revision rather
        // than rendering an empty version cell.
        Assert.Equal("abcdef123456", versions[1].TextContent.Trim());

        var trust = cut.FindAll("[data-testid='plugin-trust-state']");
        Assert.Equal("Verified", trust[0].TextContent.Trim());
        Assert.Equal("Modified", trust[1].TextContent.Trim());
    }

    [Fact]
    public void Shows_empty_state_when_no_plugins_are_installed()
    {
        _handler.SetupResponse("GET", "/api/plugins", "[]");

        var cut = _ctx.Render<PluginsPage>();
        cut.WaitForState(() => cut.Markup.Contains("plugins-empty"));

        Assert.Contains("No plugins installed", cut.Markup);
        Assert.Empty(cut.FindAll("[data-testid='plugin-row']"));
    }

    // ── AC2: update availability shown per plugin ────────────────────────────

    [Fact]
    public void Shows_update_availability_per_plugin()
    {
        SetupTwoPlugins();

        var cut = RenderPage();

        var updates = cut.FindAll("[data-testid='plugin-update-state']");
        Assert.Equal("Up to date", updates[0].TextContent.Trim());
        Assert.Contains("Update available", updates[1].TextContent);
        // The available revision is named, so "an update exists" is actionable rather than bare.
        Assert.Contains("fedcba654321", updates[1].TextContent);
    }

    [Fact]
    public void Renders_pinned_and_unchecked_update_states_distinctly()
    {
        // Row() returns a JSON document as a string, so it must be re-parsed before being placed
        // into an array - serialising the raw strings would yield a list of quoted strings, not
        // plugin objects, and the page would bind nothing.
        _handler.SetupResponse("GET", "/api/plugins", JsonSerializer.Serialize(new[]
        {
            JsonSerializer.Deserialize<JsonElement>(Row(AlphaName, updateState: 3, updatesEnabled: false)),
            JsonSerializer.Deserialize<JsonElement>(Row(BetaName, updateState: 0)),
        }));

        var cut = RenderPage();

        var updates = cut.FindAll("[data-testid='plugin-update-state']");
        Assert.Equal("Pinned - not checked", updates[0].TextContent.Trim());
        // "Unknown" must not read as "up to date" - claiming currency without probing would be
        // the exact collapse the three-state model exists to prevent.
        Assert.Equal("Not checked", updates[1].TextContent.Trim());
    }

    // ── AC3: auto-update preference is toggleable and persists ───────────────

    [Fact]
    public void Auto_update_toggle_reflects_the_persisted_preference()
    {
        SetupTwoPlugins();

        var cut = RenderPage();

        var toggles = cut.FindAll("[data-testid='plugin-autoupdate-toggle']");
        Assert.Equal(2, toggles.Count);
        Assert.True(toggles[0].HasAttribute("checked"));
        Assert.False(toggles[1].HasAttribute("checked"));
    }

    [Fact]
    public void Toggling_auto_update_writes_the_preference_to_the_gateway()
    {
        SetupTwoPlugins();
        _handler.SetupResponse(
            "PUT",
            $"/api/plugins/{AlphaName}/update-preference",
            Row(AlphaName, updatesEnabled: false, updateState: 3));

        var cut = RenderPage();
        cut.FindAll("[data-testid='plugin-autoupdate-toggle']")[0].Change(false);

        cut.WaitForState(() => _handler.Requests.Any(r => r.StartsWith("PUT:", StringComparison.Ordinal)));

        var write = Assert.Single(_handler.Requests, r => r.StartsWith("PUT:", StringComparison.Ordinal));
        Assert.Equal($"PUT:/api/plugins/{AlphaName}/update-preference", write);
        Assert.Contains("\"updatesEnabled\":false", _handler.LastRequestBody);
    }

    [Fact]
    public void Toggling_auto_update_rerenders_the_row_from_the_persisted_response()
    {
        SetupTwoPlugins();
        _handler.SetupResponse(
            "PUT",
            $"/api/plugins/{AlphaName}/update-preference",
            Row(AlphaName, updatesEnabled: false, updateState: 3));

        var cut = RenderPage();
        cut.FindAll("[data-testid='plugin-autoupdate-toggle']")[0].Change(false);

        cut.WaitForState(() =>
            cut.FindAll("[data-testid='plugin-update-state']")[0].TextContent.Contains("Pinned"));

        // The row shows what the gateway stored, not what was clicked.
        Assert.False(cut.FindAll("[data-testid='plugin-autoupdate-toggle']")[0].HasAttribute("checked"));
        Assert.Contains("is pinned", cut.Find("[data-testid='plugins-status']").TextContent);
    }

    [Fact]
    public void Failed_preference_write_reports_an_error_and_leaves_the_row_unchanged()
    {
        SetupTwoPlugins();
        _handler.SetupStatus("PUT", $"/api/plugins/{AlphaName}/update-preference", HttpStatusCode.InternalServerError);

        var cut = RenderPage();
        cut.FindAll("[data-testid='plugin-autoupdate-toggle']")[0].Change(false);

        cut.WaitForState(() => cut.FindAll("[data-testid='plugins-status']").Count > 0);

        Assert.Contains("Could not save", cut.Find("[data-testid='plugins-status']").TextContent);
        // A rejected write must not leave the row showing the preference the gateway never stored.
        Assert.True(cut.FindAll("[data-testid='plugin-autoupdate-toggle']")[0].HasAttribute("checked"));
    }

    // ── AC4: routed at /plugins and /plugins/{PluginId} ──────────────────────

    [Fact]
    public void Route_parameter_selects_the_addressed_plugin()
    {
        SetupTwoPlugins();

        var cut = _ctx.Render<PluginsPage>(p => p.Add(c => c.PluginId, BetaName));
        cut.WaitForState(() => cut.FindAll("[data-testid='plugin-detail']").Count > 0);

        Assert.Equal(BetaName, cut.Find("[data-testid='plugin-detail-name']").TextContent.Trim());
        Assert.Contains("https://example.com/beta.git", cut.Find("[data-testid='plugin-detail-source']").TextContent);
        Assert.Contains("abcdef123456", cut.Find("[data-testid='plugin-detail-revision']").TextContent);
        Assert.Empty(cut.FindAll("[data-testid='plugin-unknown-notice']"));
    }

    [Fact]
    public void No_route_parameter_renders_the_list_with_nothing_selected()
    {
        SetupTwoPlugins();

        var cut = RenderPage();

        Assert.Equal(2, cut.FindAll("[data-testid='plugin-row']").Count);
        Assert.Empty(cut.FindAll("[data-testid='plugin-detail']"));
        Assert.Empty(cut.FindAll("[data-testid='plugin-unknown-notice']"));
    }

    [Fact]
    public void Each_row_links_to_its_own_addressable_route()
    {
        SetupTwoPlugins();

        var cut = RenderPage();

        var links = cut.FindAll("[data-testid='plugin-link']");
        Assert.Equal($"plugins/{AlphaName}", links[0].GetAttribute("href"));
        Assert.Equal($"plugins/{BetaName}", links[1].GetAttribute("href"));
    }

    [Fact]
    public void Unknown_plugin_id_falls_back_to_the_unselected_list_with_a_non_fatal_notice()
    {
        SetupTwoPlugins();

        var cut = _ctx.Render<PluginsPage>(p => p.Add(c => c.PluginId, "does-not-exist"));
        cut.WaitForState(() => cut.FindAll("[data-testid='plugin-unknown-notice']").Count > 0);

        var notice = cut.Find("[data-testid='plugin-unknown-notice']");
        Assert.Contains("does-not-exist", notice.TextContent);
        Assert.Contains("is not installed", notice.TextContent);

        // Non-fatal: the full list still renders, nothing is selected, and no error surface appears.
        Assert.Equal(2, cut.FindAll("[data-testid='plugin-row']").Count);
        Assert.Empty(cut.FindAll("[data-testid='plugin-detail']"));
        Assert.Empty(cut.FindAll("[data-testid='plugins-error']"));
        Assert.Equal("status", notice.GetAttribute("role"));
    }

    [Fact]
    public void Transport_failure_reports_an_error_rather_than_an_empty_list()
    {
        _handler.SetupStatus("GET", "/api/plugins", HttpStatusCode.InternalServerError);

        var cut = _ctx.Render<PluginsPage>();
        cut.WaitForState(() => cut.FindAll("[data-testid='plugins-error']").Count > 0);

        Assert.Contains("Failed to load plugins", cut.Find("[data-testid='plugins-error']").TextContent);
        Assert.Empty(cut.FindAll("[data-testid='plugins-empty']"));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private IRenderedComponent<PluginsPage> RenderPage()
    {
        var cut = _ctx.Render<PluginsPage>();
        cut.WaitForState(() => cut.FindAll("[data-testid='plugins-loading']").Count == 0);
        return cut;
    }

    private void SetupTwoPlugins() =>
        _handler.SetupResponse("GET", "/api/plugins", JsonSerializer.Serialize(new[]
        {
            JsonSerializer.Deserialize<JsonElement>(Row(
                AlphaName,
                manifestVersion: "1.4.0",
                trustState: 1,
                updateState: 1)),
            JsonSerializer.Deserialize<JsonElement>(Row(
                BetaName,
                manifestVersion: null,
                trustState: 2,
                updateState: 2,
                updatesEnabled: false,
                availableVersion: "fedcba654321987")),
        }));

    private static string Row(
        string name,
        string? manifestVersion = "1.0.0",
        int trustState = 0,
        int updateState = 0,
        bool updatesEnabled = true,
        string? availableVersion = null) =>
        JsonSerializer.Serialize(new
        {
            name,
            source = $"https://example.com/{name.Split('-')[0]}.git",
            reference = "main",
            resolvedVersion = "abcdef123456789",
            manifestVersion,
            updatesEnabled,
            installedAtUtc = "2026-08-01T00:00:00Z",
            fileCount = 7,
            trustState,
            trustDetail = "All recorded files are present.",
            updateState,
            availableVersion,
            updateProbeError = (string?)null,
        });

    private sealed class PluginsMockHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _responses =
            new(StringComparer.OrdinalIgnoreCase);

        public List<string> Requests { get; } = [];

        public string LastRequestBody { get; private set; } = string.Empty;

        public void SetupResponse(string method, string path, string jsonContent) =>
            _responses[$"{method}:{path}"] = (HttpStatusCode.OK, jsonContent);

        public void SetupStatus(string method, string path, HttpStatusCode status) =>
            _responses[$"{method}:{path}"] = (status, "{}");

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var key = $"{request.Method.Method}:{path}";
            Requests.Add(key);

            if (request.Content is not null)
            {
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            if (!_responses.TryGetValue(key, out var configured))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
                };
            }

            return new HttpResponseMessage(configured.Status)
            {
                Content = new StringContent(configured.Body, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }
}
