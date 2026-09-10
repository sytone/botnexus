using System.Net;
using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;

using PluginsPage = BotNexus.Extensions.Channels.SignalR.BlazorClient.Pages.Plugins;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Tests the marketplace browse view on the plugins page: the configured repositories, the plugins
/// they offer, and installing one from the listing.
/// </summary>
/// <remarks>
/// The two claims these tests exist to defend are that a plugin carrying a gateway extension says
/// so BEFORE the install button is pressed, and that a repository which could not be read still
/// lists what it last offered rather than going blank.
/// </remarks>
public sealed class PluginsPageBrowseTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly BrowseMockHandler _handler = new();

    public PluginsPageBrowseTests()
    {
        var httpClient = new HttpClient(_handler) { BaseAddress = new Uri("http://localhost/") };
        _ctx.Services.AddSingleton(httpClient);
        _ctx.Services.AddScoped<PluginsApiClient>();
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _handler.SetupResponse("GET", "/api/plugins", "[]");
        _handler.SetupResponse("GET", "/api/plugins/sources", "[]");
    }

    public void Dispose() => _ctx.Dispose();

    private IRenderedComponent<PluginsPage> RenderPage() => _ctx.Render<PluginsPage>();

    private static string Offering(
        string name,
        string url = "https://github.com/acme/alpha.git",
        string? version = "1.0.0",
        string? description = null,
        bool carriesExtension = false,
        string? error = null,
        string? versionWarning = null) =>
        $$"""
        {
          "name": "{{name}}",
          "url": "{{url}}",
          "version": {{Json(version)}},
          "description": {{Json(description)}},
          "reference": null,
          "carriesExtension": {{(carriesExtension ? "true" : "false")}},
          "error": {{Json(error)}},
          "versionWarning": {{Json(versionWarning)}}
        }
        """;

    private static string Source(
        string name,
        string? kind = "plugin",
        string? lastError = null,
        params string[] offerings) =>
        $$"""
        {
          "name": "{{name}}",
          "url": "https://github.com/acme/{{name}}.git",
          "reference": null,
          "addedAtUtc": "2026-01-01T00:00:00+00:00",
          "lastRefreshedAtUtc": "2026-01-02T00:00:00+00:00",
          "lastError": {{Json(lastError)}},
          "kind": {{Json(kind)}},
          "offerings": [{{string.Join(",", offerings)}}]
        }
        """;

    private static string Json(string? value) => value is null ? "null" : $"\"{value}\"";

    /// <summary>Collapses rendered whitespace so an assertion can read prose as it appears.</summary>
    private static string Normalise(string text) =>
        string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string InstalledPlugin(string name, string manifestVersion = "1.0.0") =>
        $$"""
        {
          "name": "{{name}}",
          "source": "https://github.com/acme/{{name}}.git",
          "reference": null,
          "resolvedVersion": "abcdef123456",
          "manifestVersion": "{{manifestVersion}}",
          "updatesEnabled": true,
          "installedAtUtc": "2026-01-01T00:00:00+00:00",
          "fileCount": 1,
          "trustState": 1,
          "trustDetail": "ok",
          "updateState": 1,
          "navHidden": false
        }
        """;

    private void SetupSources(params string[] sources) =>
        _handler.SetupResponse("GET", "/api/plugins/sources", $"[{string.Join(",", sources)}]");

    // ── Telling the two URL boxes apart ──────────────────────────────────────

    /// <summary>
    /// The install box and the repositories box look alike, and pasting a catalog into the wrong
    /// one is an easy slip that fails confusingly. Each says what it does, so the choice is
    /// visible before the click rather than explained by the error afterwards.
    /// </summary>
    [Fact]
    public void Each_url_box_says_what_it_does()
    {
        var cut = RenderPage();

        var install = cut.Find("[data-testid='plugin-install-hint']").TextContent;
        var sources = cut.Find("[data-testid='plugin-sources-hint']").TextContent;

        Assert.Contains("install", install, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("catalog", sources, StringComparison.OrdinalIgnoreCase);
        // The distinction that matters: adding a repository does not install.
        Assert.Contains("installs nothing", sources, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_two_url_boxes_do_not_share_a_placeholder()
    {
        var cut = RenderPage();

        var install = cut.Find("[data-testid='plugin-install-source']").GetAttribute("placeholder");
        var source = cut.Find("[data-testid='plugin-source-url']").GetAttribute("placeholder");

        Assert.NotEqual(install, source);
    }

    [Fact]
    public void The_two_url_boxes_have_distinct_accessible_names()
    {
        var cut = RenderPage();

        var install = cut.Find("[data-testid='plugin-install-source']").GetAttribute("aria-label");
        var source = cut.Find("[data-testid='plugin-source-url']").GetAttribute("aria-label");

        Assert.NotEqual(install, source);
        Assert.False(string.IsNullOrWhiteSpace(install));
        Assert.False(string.IsNullOrWhiteSpace(source));
    }

    // ── Listing ──────────────────────────────────────────────────────────────

    [Fact]
    public void Shows_an_empty_state_when_no_repositories_are_configured()
    {
        var cut = RenderPage();

        Assert.NotNull(cut.Find("[data-testid='plugin-sources-empty']"));
        Assert.Empty(cut.FindAll("[data-testid='plugin-source']"));
    }

    [Fact]
    public void Lists_each_repository_with_what_it_is()
    {
        SetupSources(
            Source("alpha", offerings: Offering("alpha-plugin")),
            Source("catalog-repo", kind: "catalog", offerings: Offering("beta-plugin")));

        var cut = RenderPage();

        var sources = cut.FindAll("[data-testid='plugin-source']");
        Assert.Equal(2, sources.Count);

        var kinds = cut.FindAll("[data-testid='plugin-source-kind']");
        Assert.Equal("Single plugin", kinds[0].TextContent.Trim());
        Assert.Equal("Catalog", kinds[1].TextContent.Trim());
    }

    [Fact]
    public void Lists_the_plugins_a_repository_offers()
    {
        SetupSources(Source("alpha", offerings:
        [
            Offering("alpha-plugin", version: "2.1.0", description: "Does alpha things."),
            Offering("beta-plugin"),
        ]));

        var cut = RenderPage();

        Assert.Equal(2, cut.FindAll("[data-testid='plugin-offering']").Count);
        Assert.Equal("alpha-plugin", cut.FindAll("[data-testid='plugin-offering-name']")[0].TextContent.Trim());
        Assert.Equal("2.1.0", cut.FindAll("[data-testid='plugin-offering-version']")[0].TextContent.Trim());
        Assert.Equal(
            "Does alpha things.",
            cut.Find("[data-testid='plugin-offering-description']").TextContent.Trim());
    }

    /// <summary>
    /// The fact that most changes the decision is shown on the listing, before Install is pressed -
    /// not held back until the consent prompt that appears after the gateway has already fetched it.
    /// </summary>
    [Fact]
    public void A_plugin_that_carries_an_extension_says_so_before_it_is_installed()
    {
        SetupSources(Source("alpha", offerings:
        [
            Offering("code-plugin", carriesExtension: true),
            Offering("skills-plugin"),
        ]));

        var cut = RenderPage();

        var badges = cut.FindAll("[data-testid='plugin-offering-carries-extension']");
        var badge = Assert.Single(badges);
        Assert.Contains("extension", badge.TextContent, StringComparison.OrdinalIgnoreCase);

        // It sits on the code plugin's row, not merely somewhere on the page.
        var rows = cut.FindAll("[data-testid='plugin-offering']");
        Assert.Equal("code-plugin", rows[0].GetAttribute("data-offering-name"));
        Assert.NotEmpty(rows[0].QuerySelectorAll("[data-testid='plugin-offering-carries-extension']"));
        Assert.Empty(rows[1].QuerySelectorAll("[data-testid='plugin-offering-carries-extension']"));
    }

    /// <summary>
    /// A source that could not be read keeps its previous offerings, so the error must sit
    /// alongside the listing rather than replacing it - an unreachable repository can still be
    /// installed from.
    /// </summary>
    [Fact]
    public void A_repository_that_could_not_be_read_shows_its_error_and_still_lists_what_it_had()
    {
        SetupSources(Source("alpha", lastError: "network down", offerings: Offering("alpha-plugin")));

        var cut = RenderPage();

        Assert.Contains("network down", cut.Find("[data-testid='plugin-source-error']").TextContent);
        Assert.Single(cut.FindAll("[data-testid='plugin-offering']"));
        Assert.Single(cut.FindAll("[data-testid='plugin-offering-install']"));
    }

    [Fact]
    public void A_repository_that_lists_nothing_says_so()
    {
        SetupSources(Source("alpha"));

        var cut = RenderPage();

        Assert.NotNull(cut.Find("[data-testid='plugin-source-no-offerings']"));
    }

    [Fact]
    public void An_entry_that_could_not_be_read_shows_its_error_and_cannot_be_installed()
    {
        SetupSources(Source("alpha", kind: "catalog", offerings:
            Offering("broken", error: "repository not found")));

        var cut = RenderPage();

        Assert.Contains("repository not found", cut.Find("[data-testid='plugin-offering-error']").TextContent);
        Assert.True(cut.Find("[data-testid='plugin-offering-install']").HasAttribute("disabled"));
    }

    // ── Already installed ────────────────────────────────────────────────────

    [Fact]
    public void An_offering_that_is_already_installed_offers_no_install_button()
    {
        _handler.SetupResponse("GET", "/api/plugins", $"[{InstalledPlugin("alpha-plugin")}]");
        SetupSources(Source("alpha", offerings:
        [
            Offering("alpha-plugin"),
            Offering("beta-plugin"),
        ]));

        var cut = RenderPage();

        Assert.Single(cut.FindAll("[data-testid='plugin-offering-installed']"));
        var install = Assert.Single(cut.FindAll("[data-testid='plugin-offering-install']"));

        // The remaining button belongs to the plugin that is NOT installed.
        Assert.Equal(
            "beta-plugin",
            install.Closest("[data-testid='plugin-offering']")!.GetAttribute("data-offering-name"));
    }

    // ── A catalog pinned at the wrong ref ────────────────────────────────────

    /// <summary>
    /// A stale pin is not an error: the entry reads and installs. But a catalog version IS the git
    /// ref an install resolves, so pointing at an old one quietly installs the previous release.
    /// </summary>
    [Fact]
    public void A_stale_catalog_pin_is_shown_without_blocking_the_entry()
    {
        _handler.SetupResponse("GET", "/api/plugins", "[]");
        SetupSources(Source("alpha", kind: "catalog", offerings: Offering(
            "alpha-plugin",
            versionWarning: "the catalog pins 'v1.2.0' but 'v1.2.1' has been released")));

        var cut = RenderPage();

        var warning = cut.Find("[data-testid='plugin-offering-pin-warning']");
        Assert.Contains("v1.2.1", warning.TextContent);
        // Still installable, and not reported as an unreadable entry.
        Assert.NotNull(cut.Find("[data-testid='plugin-offering-install']"));
        Assert.Empty(cut.FindAll("[data-testid='plugin-offering-error']"));
    }

    [Fact]
    public void An_entry_with_a_sound_pin_shows_no_warning()
    {
        _handler.SetupResponse("GET", "/api/plugins", "[]");
        SetupSources(Source("alpha", kind: "catalog", offerings: Offering("alpha-plugin")));

        var cut = RenderPage();

        Assert.Empty(cut.FindAll("[data-testid='plugin-offering-pin-warning']"));
    }

    [Fact]
    public void A_pin_warning_and_a_read_error_are_shown_as_different_things()
    {
        _handler.SetupResponse("GET", "/api/plugins", "[]");
        SetupSources(Source("alpha", kind: "catalog", offerings: Offering(
            "broken",
            error: "no .botnexus-plugin/plugin.json in the entry's repository",
            versionWarning: "the catalog pins 'v9.9.9', which is not a tag or branch")));

        var cut = RenderPage();

        Assert.NotNull(cut.Find("[data-testid='plugin-offering-error']"));
        Assert.NotNull(cut.Find("[data-testid='plugin-offering-pin-warning']"));
        // An unreadable entry still cannot be installed, warning or not.
        Assert.True(cut.Find("[data-testid='plugin-offering-install']").HasAttribute("disabled"));
    }

    // ── Version state against what is installed ──────────────────────────────

    private void SetupInstalledAndOffered(string installedVersion, string offeredVersion)
    {
        _handler.SetupResponse("GET", "/api/plugins", $"[{InstalledPlugin("alpha-plugin", installedVersion)}]");
        SetupSources(Source("alpha", offerings: Offering("alpha-plugin", version: offeredVersion)));
    }

    [Fact]
    public void An_offering_newer_than_the_installed_version_offers_the_update()
    {
        SetupInstalledAndOffered(installedVersion: "1.2.0", offeredVersion: "1.2.1");

        var cut = RenderPage();

        Assert.Contains("1.2.1", cut.Find("[data-testid='plugin-offering-update']").TextContent);
        Assert.Empty(cut.FindAll("[data-testid='plugin-offering-installed']"));
    }

    /// <summary>
    /// The case this state exists for: a catalog still pinning an older version than the one
    /// running. Calling that an update would send the operator to install a downgrade.
    /// </summary>
    [Fact]
    public void An_offering_older_than_the_installed_version_is_not_called_an_update()
    {
        SetupInstalledAndOffered(installedVersion: "1.2.1", offeredVersion: "1.2.0");

        var cut = RenderPage();

        Assert.Empty(cut.FindAll("[data-testid='plugin-offering-update']"));
        var behind = cut.Find("[data-testid='plugin-offering-behind']");
        Assert.Contains("1.2.0", behind.TextContent);
        // The row must say the SOURCE is the stale side, not the plugin.
        Assert.Contains("source", behind.TextContent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Versions_that_cannot_be_ordered_claim_no_direction()
    {
        SetupInstalledAndOffered(installedVersion: "1.2.0", offeredVersion: "1.2.1-beta");

        var cut = RenderPage();

        Assert.Empty(cut.FindAll("[data-testid='plugin-offering-update']"));
        Assert.Empty(cut.FindAll("[data-testid='plugin-offering-behind']"));
        Assert.NotNull(cut.Find("[data-testid='plugin-offering-differs']"));
    }

    [Fact]
    public void A_matching_version_still_reads_as_plainly_installed()
    {
        SetupInstalledAndOffered(installedVersion: "1.2.1", offeredVersion: "1.2.1");

        var cut = RenderPage();

        Assert.NotNull(cut.Find("[data-testid='plugin-offering-installed']"));
        Assert.Empty(cut.FindAll("[data-testid='plugin-offering-update']"));
        Assert.Empty(cut.FindAll("[data-testid='plugin-offering-behind']"));
    }

    /// <summary>
    /// Every installed state must withhold the Install button: install refuses a plugin that is
    /// already present, so offering it would produce a failure the operator could not have foreseen.
    /// </summary>
    [Theory]
    [InlineData("1.2.0", "1.2.1")]
    [InlineData("1.2.1", "1.2.0")]
    [InlineData("1.2.0", "1.2.1-beta")]
    [InlineData("1.2.1", "1.2.1")]
    public void No_installed_state_offers_the_install_button(string installed, string offered)
    {
        SetupInstalledAndOffered(installed, offered);

        var cut = RenderPage();

        Assert.Empty(cut.FindAll("[data-testid='plugin-offering-install']"));
    }

    [Fact]
    public void A_plugin_that_is_not_installed_is_unaffected_by_version_comparison()
    {
        _handler.SetupResponse("GET", "/api/plugins", "[]");
        SetupSources(Source("alpha", offerings: Offering("alpha-plugin", version: "9.9.9")));

        var cut = RenderPage();

        Assert.NotNull(cut.Find("[data-testid='plugin-offering-install']"));
        Assert.Empty(cut.FindAll("[data-testid='plugin-offering-update']"));
    }

    // ── Installing from the listing ──────────────────────────────────────────

    [Fact]
    public void Installing_an_offering_posts_its_own_url()
    {
        SetupSources(Source("alpha", offerings:
            Offering("alpha-plugin", url: "https://github.com/acme/alpha-plugin.git")));
        _handler.SetupResponse("POST", "/api/plugins/install",
            """{"outcome":"Installed","name":"alpha-plugin","restartRequired":false}""");

        var cut = RenderPage();
        cut.Find("[data-testid='plugin-offering-install']").Click();

        Assert.Contains("POST:/api/plugins/install", _handler.Requests);
        Assert.Contains("https://github.com/acme/alpha-plugin.git", _handler.LastRequestBody);
    }

    [Fact]
    public void Installing_a_code_plugin_that_the_gateway_refuses_raises_the_consent_prompt()
    {
        SetupSources(Source("alpha", offerings: Offering("code-plugin", carriesExtension: true)));
        _handler.SetupJson("POST", "/api/plugins/install", HttpStatusCode.BadRequest,
            """
            {"error":"This plugin carries a gateway extension.",
             "errors":[{"field":"extension.consent","message":"Acknowledge the carried extension."}]}
            """);

        var cut = RenderPage();
        cut.Find("[data-testid='plugin-offering-install']").Click();

        Assert.NotNull(cut.Find("[data-testid='plugin-consent-prompt']"));
    }

    // ── Adding ───────────────────────────────────────────────────────────────

    [Fact]
    public void Adding_a_repository_posts_the_url_and_reloads_the_list()
    {
        _handler.SetupResponse("POST", "/api/plugins/sources",
            Source("acme-alpha", offerings: Offering("alpha-plugin")));

        var cut = RenderPage();
        cut.Find("[data-testid='plugin-source-url']").Change("https://github.com/acme/alpha.git");
        cut.Find("[data-testid='plugin-source-add']").Click();

        Assert.Contains("POST:/api/plugins/sources", _handler.Requests);
        Assert.Contains("https://github.com/acme/alpha.git", _handler.LastRequestBody);
        // The list is re-read rather than patched in memory, so what is shown is what is stored.
        Assert.Equal(2, _handler.Requests.Count(r => r == "GET:/api/plugins/sources"));
    }

    /// <summary>
    /// A source stored but unreadable is a success carrying an error. Reporting it as a plain
    /// failure would invite the operator to add it again and hit a duplicate conflict.
    /// </summary>
    [Fact]
    public void Adding_a_repository_that_cannot_be_read_reports_it_as_added_with_the_reason()
    {
        _handler.SetupResponse("POST", "/api/plugins/sources",
            Source("acme-alpha", kind: null, lastError: "could not resolve host"));

        var cut = RenderPage();
        cut.Find("[data-testid='plugin-source-url']").Change("https://github.com/acme/alpha.git");
        cut.Find("[data-testid='plugin-source-add']").Click();

        var status = cut.Find("[data-testid='plugins-status']").TextContent;
        Assert.Contains("Added", status);
        Assert.Contains("could not resolve host", status);
    }

    [Fact]
    public void A_rejected_repository_reports_the_gateways_reason()
    {
        _handler.SetupJson("POST", "/api/plugins/sources", HttpStatusCode.BadRequest,
            """{"error":"The URL must be an absolute http:// or https:// repository address."}""");

        var cut = RenderPage();
        cut.Find("[data-testid='plugin-source-url']").Change("file:///etc");
        cut.Find("[data-testid='plugin-source-add']").Click();

        Assert.Contains("absolute http", cut.Find("[data-testid='plugins-status']").TextContent);
    }

    // ── Removing ─────────────────────────────────────────────────────────────

    [Fact]
    public void Removing_a_repository_asks_first_and_says_installed_plugins_are_unaffected()
    {
        SetupSources(Source("alpha", offerings: Offering("alpha-plugin")));

        var cut = RenderPage();
        cut.Find("[data-testid='plugin-source-remove']").Click();

        var confirm = cut.Find("[data-testid='plugin-source-remove-confirm']");
        // Whitespace is normalised because the sentence wraps in the markup, so the rendered text
        // carries a newline and indentation between words that read as adjacent on screen.
        Assert.Contains("stay installed", Normalise(confirm.TextContent), StringComparison.OrdinalIgnoreCase);
        // Nothing is deleted until the question is answered.
        Assert.DoesNotContain(_handler.Requests, r => r.StartsWith("DELETE:", StringComparison.Ordinal));
    }

    [Fact]
    public void Confirming_removal_deletes_the_source()
    {
        SetupSources(Source("alpha", offerings: Offering("alpha-plugin")));
        _handler.SetupResponse("DELETE", "/api/plugins/sources/alpha", """{"removed":"alpha"}""");

        var cut = RenderPage();
        cut.Find("[data-testid='plugin-source-remove']").Click();
        cut.Find("[data-testid='plugin-source-remove-confirm-btn']").Click();

        Assert.Contains("DELETE:/api/plugins/sources/alpha", _handler.Requests);
    }

    [Fact]
    public void Cancelling_removal_deletes_nothing()
    {
        SetupSources(Source("alpha", offerings: Offering("alpha-plugin")));

        var cut = RenderPage();
        cut.Find("[data-testid='plugin-source-remove']").Click();
        cut.Find("[data-testid='plugin-source-remove-cancel']").Click();

        Assert.Empty(cut.FindAll("[data-testid='plugin-source-remove-confirm']"));
        Assert.DoesNotContain(_handler.Requests, r => r.StartsWith("DELETE:", StringComparison.Ordinal));
    }

    // ── Refreshing ───────────────────────────────────────────────────────────

    [Fact]
    public void Refreshing_one_repository_re_reads_it()
    {
        SetupSources(Source("alpha", offerings: Offering("alpha-plugin")));
        _handler.SetupResponse("POST", "/api/plugins/sources/alpha/refresh",
            Source("alpha", offerings: Offering("alpha-plugin", version: "2.0.0")));

        var cut = RenderPage();
        cut.Find("[data-testid='plugin-source-refresh']").Click();

        Assert.Contains("POST:/api/plugins/sources/alpha/refresh", _handler.Requests);
    }

    [Fact]
    public void Refreshing_a_single_repository_does_not_say_repositories()
    {
        SetupSources(Source("alpha", offerings: Offering("alpha-plugin")));
        _handler.SetupResponse("POST", "/api/plugins/sources/refresh",
            $"[{Source("alpha", offerings: Offering("alpha-plugin"))}]");

        var cut = RenderPage();
        cut.Find("[data-testid='plugin-sources-refresh-all']").Click();

        var status = cut.Find("[data-testid='plugins-status']").TextContent;
        Assert.Contains("1 repository.", status);
        Assert.DoesNotContain("repositories", status);
    }

    [Fact]
    public void Refreshing_several_repositories_uses_the_plural()
    {
        SetupSources(Source("alpha", offerings: Offering("alpha-plugin")));
        _handler.SetupResponse("POST", "/api/plugins/sources/refresh",
            $"[{Source("alpha", offerings: Offering("alpha-plugin"))},{Source("beta", offerings: Offering("beta-plugin"))}]");

        var cut = RenderPage();
        cut.Find("[data-testid='plugin-sources-refresh-all']").Click();

        Assert.Contains("2 repositories.", cut.Find("[data-testid='plugins-status']").TextContent);
    }

    [Fact]
    public void Refreshing_all_reports_how_many_could_not_be_read()
    {
        SetupSources(Source("alpha", offerings: Offering("alpha-plugin")));
        _handler.SetupResponse("POST", "/api/plugins/sources/refresh",
            $"[{Source("alpha", lastError: "gone", offerings: Offering("alpha-plugin"))}]");

        var cut = RenderPage();
        cut.Find("[data-testid='plugin-sources-refresh-all']").Click();

        var status = cut.Find("[data-testid='plugins-status']").TextContent;
        Assert.Contains("1 could not be read", status);
    }

    // ── Independence from the installed list ─────────────────────────────────

    /// <summary>
    /// The page's main job is showing what is installed. A repository listing that fails to load
    /// must not take that with it.
    /// </summary>
    [Fact]
    public void Repositories_failing_to_load_still_leaves_the_installed_list_rendered()
    {
        _handler.SetupResponse("GET", "/api/plugins", $"[{InstalledPlugin("alpha-plugin")}]");
        _handler.SetupStatus("GET", "/api/plugins/sources", HttpStatusCode.InternalServerError);

        var cut = RenderPage();

        Assert.Single(cut.FindAll("[data-testid='plugin-row']"));
        // The reason belongs to the repositories card, not to the page-level status bar where the
        // operator's own actions answer.
        Assert.NotNull(cut.Find("[data-testid='plugin-sources-error']"));
        Assert.Empty(cut.FindAll("[data-testid='plugins-status']"));
    }

    private sealed class BrowseMockHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _responses =
            new(StringComparer.OrdinalIgnoreCase);

        public List<string> Requests { get; } = [];

        public string LastRequestBody { get; private set; } = string.Empty;

        public void SetupResponse(string method, string path, string jsonContent) =>
            _responses[$"{method}:{path}"] = (HttpStatusCode.OK, jsonContent);

        public void SetupJson(string method, string path, HttpStatusCode status, string jsonContent) =>
            _responses[$"{method}:{path}"] = (status, jsonContent);

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

            // A fresh response per call: one shared instance would be disposed by the first reader
            // and throw for every later one.
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
