using System.Net;
using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;

using PluginsPage = BotNexus.Extensions.Channels.SignalR.BlazorClient.Pages.Plugins;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Tests the marketplace controls on the portal plugins page: install from a repository URL,
/// the carried-extension consent prompt, the restart notice, and per-row update / remove.
/// </summary>
/// <remarks>
/// The consent tests are the load-bearing ones. Consent must be asked for in RESPONSE to the
/// gateway refusing, never as a checkbox ticked in advance - whether a source carries code is
/// only knowable after it has been fetched. So the page has to treat that one refusal as a
/// question rather than an error, keep the operator's input while asking it, and re-issue the
/// install with acknowledgement only when the operator says yes.
/// </remarks>
public sealed class PluginsPageInstallTests : IDisposable
{
    private const string Source = "https://github.com/owner/repo.git";

    private readonly BunitContext _ctx = new();
    private readonly RecordingHandler _handler = new();

    public PluginsPageInstallTests()
    {
        var httpClient = new HttpClient(_handler) { BaseAddress = new Uri("http://localhost/") };
        _ctx.Services.AddSingleton(httpClient);
        _ctx.Services.AddScoped<PluginsApiClient>();
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _handler.SetupResponse("GET", "/api/plugins", "[]");
    }

    public void Dispose() => _ctx.Dispose();

    private IRenderedComponent<PluginsPage> RenderPage()
    {
        var cut = _ctx.Render<PluginsPage>();
        cut.WaitForState(() => cut.FindAll("[data-testid='plugins-loading']").Count == 0);
        return cut;
    }

    private static void EnterSource(IRenderedComponent<PluginsPage> cut, string source, string? reference = null)
    {
        cut.Find("[data-testid='plugin-install-source']").Change(source);
        if (reference is not null)
        {
            cut.Find("[data-testid='plugin-install-reference']").Change(reference);
        }
    }

    private const string InstalledBody = """
        {"outcome":"Installed","name":"repo","resolvedVersion":"abc123","restartRequired":false}
        """;

    [Fact]
    public void Install_button_is_disabled_until_a_source_is_entered()
    {
        var cut = RenderPage();

        Assert.True(cut.Find("[data-testid='plugin-install-submit']").HasAttribute("disabled"));

        EnterSource(cut, Source);

        Assert.False(cut.Find("[data-testid='plugin-install-submit']").HasAttribute("disabled"));
    }

    [Fact]
    public void Install_posts_the_source_and_reference()
    {
        _handler.SetupResponse("POST", "/api/plugins/install", InstalledBody);
        var cut = RenderPage();

        EnterSource(cut, Source, "v1.0.0");
        cut.Find("[data-testid='plugin-install-submit']").Click();

        var post = _handler.Calls.Single(c => c.Method == "POST");
        Assert.Contains(Source, post.Body);
        Assert.Contains("v1.0.0", post.Body);
        Assert.Contains("\"acknowledgeCarriedExtension\":false", post.Body);
    }

    [Fact]
    public void A_successful_install_reports_it_and_reloads_the_list()
    {
        _handler.SetupResponse("POST", "/api/plugins/install", InstalledBody);
        var cut = RenderPage();

        EnterSource(cut, Source);
        cut.Find("[data-testid='plugin-install-submit']").Click();

        cut.WaitForState(() => cut.FindAll("[data-testid='plugins-status']").Count > 0);
        Assert.Contains("Installed", cut.Find("[data-testid='plugins-status']").TextContent);
        Assert.Equal(2, _handler.Calls.Count(c => c is { Method: "GET", Path: "/api/plugins" }));
    }

    // A refusal for want of consent must not render as a failure: it is a question.
    [Fact]
    public void A_carried_extension_refusal_asks_for_consent_rather_than_reporting_an_error()
    {
        _handler.SetupError("POST", "/api/plugins/install", HttpStatusCode.BadRequest, """
            {"error":"Plugin 'code-plugin' carries a gateway extension, which runs code in the gateway process at full trust.",
             "errors":[{"field":"extension.consent","message":"consent required"}]}
            """);
        var cut = RenderPage();

        EnterSource(cut, Source);
        cut.Find("[data-testid='plugin-install-submit']").Click();

        cut.WaitForState(() => cut.FindAll("[data-testid='plugin-consent-prompt']").Count > 0);
        Assert.Contains("runs code in the gateway process",
            cut.Find("[data-testid='plugin-consent-message']").TextContent);
        Assert.Empty(cut.FindAll("[data-testid='plugins-status'].error"));
    }

    // Answering the question must not mean retyping the URL.
    [Fact]
    public void The_entered_source_survives_a_consent_prompt()
    {
        _handler.SetupError("POST", "/api/plugins/install", HttpStatusCode.BadRequest, """
            {"error":"carries a gateway extension","errors":[{"field":"extension.consent","message":"x"}]}
            """);
        var cut = RenderPage();

        EnterSource(cut, Source);
        cut.Find("[data-testid='plugin-install-submit']").Click();
        cut.WaitForState(() => cut.FindAll("[data-testid='plugin-consent-prompt']").Count > 0);

        Assert.Equal(Source, cut.Find("[data-testid='plugin-install-source']").GetAttribute("value"));
    }

    [Fact]
    public void Confirming_consent_re_issues_the_install_with_acknowledgement()
    {
        _handler.SetupError("POST", "/api/plugins/install", HttpStatusCode.BadRequest, """
            {"error":"carries a gateway extension","errors":[{"field":"extension.consent","message":"x"}]}
            """);
        var cut = RenderPage();

        EnterSource(cut, Source);
        cut.Find("[data-testid='plugin-install-submit']").Click();
        cut.WaitForState(() => cut.FindAll("[data-testid='plugin-consent-prompt']").Count > 0);

        _handler.SetupResponse("POST", "/api/plugins/install", """
            {"outcome":"Installed","name":"code-plugin","restartRequired":true}
            """);
        cut.Find("[data-testid='plugin-consent-confirm']").Click();

        cut.WaitForState(() => _handler.Calls.Count(c => c.Method == "POST") == 2);
        var second = _handler.Calls.Last(c => c.Method == "POST");
        Assert.Contains("\"acknowledgeCarriedExtension\":true", second.Body);
    }

    // Installed and working are different claims; a deployed extension is inert until restart.
    [Fact]
    public void A_deployed_extension_surfaces_the_restart_notice()
    {
        _handler.SetupResponse("POST", "/api/plugins/install", """
            {"outcome":"Installed","name":"code-plugin","restartRequired":true}
            """);
        var cut = RenderPage();

        EnterSource(cut, Source);
        cut.Find("[data-testid='plugin-install-submit']").Click();

        cut.WaitForState(() => cut.FindAll("[data-testid='plugin-restart-notice']").Count > 0);
        Assert.Contains("Restart the gateway",
            cut.Find("[data-testid='plugin-restart-notice']").TextContent);
    }

    [Fact]
    public void Cancelling_consent_dismisses_the_prompt_without_installing()
    {
        _handler.SetupError("POST", "/api/plugins/install", HttpStatusCode.BadRequest, """
            {"error":"carries a gateway extension","errors":[{"field":"extension.consent","message":"x"}]}
            """);
        var cut = RenderPage();

        EnterSource(cut, Source);
        cut.Find("[data-testid='plugin-install-submit']").Click();
        cut.WaitForState(() => cut.FindAll("[data-testid='plugin-consent-prompt']").Count > 0);

        cut.Find("[data-testid='plugin-consent-cancel']").Click();

        Assert.Empty(cut.FindAll("[data-testid='plugin-consent-prompt']"));
        Assert.Equal(1, _handler.Calls.Count(c => c.Method == "POST"));
    }

    // An ordinary failure IS an error, and must not be mistaken for a consent question.
    [Fact]
    public void An_ordinary_failure_reports_an_error_and_asks_nothing()
    {
        _handler.SetupError("POST", "/api/plugins/install", HttpStatusCode.BadRequest, """
            {"error":"Plugin manifest not found.","errors":[{"field":"manifest","message":"missing"}]}
            """);
        var cut = RenderPage();

        EnterSource(cut, Source);
        cut.Find("[data-testid='plugin-install-submit']").Click();

        cut.WaitForState(() => cut.FindAll("[data-testid='plugins-status']").Count > 0);
        Assert.Contains("manifest not found", cut.Find("[data-testid='plugins-status']").TextContent);
        Assert.Empty(cut.FindAll("[data-testid='plugin-consent-prompt']"));
    }

    // Removal deletes recorded content and any extension the plugin deployed. One click must not
    // be enough.
    [Fact]
    public void Remove_requires_a_second_confirming_click()
    {
        SetupOneInstalledPlugin();
        var cut = RenderPage();

        cut.Find("[data-testid='plugin-remove-btn']").Click();

        Assert.DoesNotContain(_handler.Calls, c => c.Method == "DELETE");
        Assert.Single(cut.FindAll("[data-testid='plugin-remove-confirm']"));
    }

    [Fact]
    public void Confirming_remove_deletes_the_plugin()
    {
        SetupOneInstalledPlugin();
        _handler.SetupResponse("DELETE", "/api/plugins/alpha", """
            {"outcome":"Removed","name":"alpha"}
            """);
        var cut = RenderPage();

        cut.Find("[data-testid='plugin-remove-btn']").Click();
        cut.Find("[data-testid='plugin-remove-confirm']").Click();

        cut.WaitForState(() => _handler.Calls.Any(c => c.Method == "DELETE"));
        Assert.Equal("/api/plugins/alpha", _handler.Calls.Single(c => c.Method == "DELETE").Path);
    }

    private void SetupOneInstalledPlugin() =>
        _handler.SetupResponse("GET", "/api/plugins", """
            [{"name":"alpha","source":"https://example.com/a.git","resolvedVersion":"abc123",
              "manifestVersion":"1.0.0","updatesEnabled":true,"installedAtUtc":"2026-08-27T00:00:00Z",
              "fileCount":3,"trustState":1,"updateState":1}]
            """);

    /// <summary>Records every call so a re-issued install can be told from the first one.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _responses =
            new(StringComparer.OrdinalIgnoreCase);

        public List<(string Method, string Path, string Body)> Calls { get; } = [];

        public void SetupResponse(string method, string path, string json) =>
            _responses[$"{method}:{path}"] = (HttpStatusCode.OK, json);

        public void SetupError(string method, string path, HttpStatusCode status, string json) =>
            _responses[$"{method}:{path}"] = (status, json);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            Calls.Add((request.Method.Method, path, body));

            if (!_responses.TryGetValue($"{request.Method.Method}:{path}", out var configured))
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
