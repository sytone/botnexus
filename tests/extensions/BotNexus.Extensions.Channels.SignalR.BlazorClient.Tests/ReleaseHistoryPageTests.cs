using System.Net;
using System.Text;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Pages;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class ReleaseHistoryPageTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void Renders_loading_state_before_manifest_completes()
    {
        var completion = new TaskCompletionSource<HttpResponseMessage>();
        RegisterClient(new DelegateHandler((_, _) => completion.Task));

        var cut = _ctx.Render<ReleaseHistory>();

        cut.Find("[data-testid='release-history-loading']");
    }

    [Fact]
    public void Renders_empty_state_for_manifest_without_releases()
    {
        RegisterJson("""{"schemaVersion":"1.0.0","releases":[]}""");

        var cut = _ctx.Render<ReleaseHistory>();

        cut.WaitForAssertion(() => cut.Find("[data-testid='release-history-empty']"));
    }

    [Fact]
    public async Task Retry_loads_history_after_an_error()
    {
        var attempts = 0;
        RegisterClient(new DelegateHandler((_, _) =>
        {
            attempts++;
            return Task.FromResult(attempts == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : JsonResponse("""{"schemaVersion":"1.0.0","releases":[]}"""));
        }));

        var cut = _ctx.Render<ReleaseHistory>();
        cut.WaitForAssertion(() => cut.Find("[data-testid='release-history-error']"));

        await cut.InvokeAsync(() => cut.Find("button").Click());

        cut.WaitForAssertion(() => cut.Find("[data-testid='release-history-empty']"));
        Assert.Equal(2, attempts);
    }

    [Fact]
    public void Renders_error_state_for_invalid_manifest()
    {
        RegisterJson("""{"schemaVersion":"2.0.0","releases":[]}""");

        var cut = _ctx.Render<ReleaseHistory>();

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='release-history-error']");
            Assert.DoesNotContain("2.0.0", cut.Markup, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Renders_newest_versions_categories_and_public_links_in_manifest_order()
    {
        RegisterJson("""
            {
              "schemaVersion": "1.0.0",
              "releases": [
                {
                  "version": "2.0.0",
                  "tag": "v2.0.0",
                  "commit": "2222222222222222222222222222222222222222",
                  "releasedAt": "2026-09-20",
                  "releaseUrl": "https://github.com/Sytone/botnexus/releases/tag/v2.0.0",
                  "documentationUrl": "https://sytone.github.io/botnexus/releases/v2.0.0/",
                  "categories": [{"name":"Features","changes":[{"summary":"Add fictional capability","documentationUrls":["https://sytone.github.io/botnexus/user-guide/fictional"]}]}]
                },
                {
                  "version": "1.9.0",
                  "tag": "v1.9.0",
                  "commit": "1111111111111111111111111111111111111111",
                  "releasedAt": "2026-09-10",
                  "releaseUrl": "https://github.com/Sytone/botnexus/releases/tag/v1.9.0",
                  "documentationUrl": "https://sytone.github.io/botnexus/releases/v1.9.0/",
                  "categories": []
                }
              ]
            }
            """);

        var cut = _ctx.Render<ReleaseHistory>();

        cut.WaitForAssertion(() =>
        {
            var releases = cut.FindAll("[data-testid='release-history-entry']");
            Assert.Equal(2, releases.Count);
            Assert.Contains("2.0.0", releases[0].TextContent, StringComparison.Ordinal);
            Assert.Contains("1.9.0", releases[1].TextContent, StringComparison.Ordinal);
            Assert.Contains("Features", releases[0].TextContent, StringComparison.Ordinal);
            Assert.Equal(
                "https://github.com/Sytone/botnexus/releases/tag/v2.0.0",
                releases[0].QuerySelector("a[data-testid='release-link']")?.GetAttribute("href"));
            Assert.Equal(
                "https://sytone.github.io/botnexus/user-guide/fictional",
                releases[0].QuerySelector("a[data-testid='capability-link']")?.GetAttribute("href"));
        });
    }

    [Fact]
    public void Rejects_semantically_misordered_versions()
    {
        RegisterJson("""
            {"schemaVersion":"1.0.0","releases":[
              {"version":"1.9.0","tag":"v1.9.0","commit":"1111111111111111111111111111111111111111","releasedAt":"2026-09-10","releaseUrl":"https://github.com/Sytone/botnexus/releases/tag/v1.9.0","documentationUrl":"https://sytone.github.io/botnexus/releases/v1.9.0/","categories":[]},
              {"version":"2.0.0","tag":"v2.0.0","commit":"2222222222222222222222222222222222222222","releasedAt":"2026-09-20","releaseUrl":"https://github.com/Sytone/botnexus/releases/tag/v2.0.0","documentationUrl":"https://sytone.github.io/botnexus/releases/v2.0.0/","categories":[]}
            ]}
            """);

        var cut = _ctx.Render<ReleaseHistory>();

        cut.WaitForAssertion(() => cut.Find("[data-testid='release-history-error']"));
    }

    [Theory]
    [InlineData("https://example.invalid/release")]
    [InlineData("https://sytone.github.io/unrelated/release")]
    public void Rejects_non_public_links(string unsafeUrl)
    {
        RegisterJson($$"""
            {"schemaVersion":"1.0.0","releases":[
              {"version":"2.0.0","tag":"v2.0.0","commit":"2222222222222222222222222222222222222222","releasedAt":"2026-09-20","releaseUrl":"{{unsafeUrl}}","documentationUrl":"https://sytone.github.io/botnexus/releases/v2.0.0/","categories":[]}
            ]}
            """);

        var cut = _ctx.Render<ReleaseHistory>();

        cut.WaitForAssertion(() => cut.Find("[data-testid='release-history-error']"));
    }

    [Fact]
    public async Task Retains_last_validated_history_when_refresh_fails()
    {
        var attempts = 0;
        RegisterClient(new DelegateHandler((_, _) =>
        {
            attempts++;
            return Task.FromResult(attempts == 1
                ? JsonResponse(SingleReleaseJson("2.0.0", "2222222222222222222222222222222222222222"))
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }));

        var cut = _ctx.Render<ReleaseHistory>();
        cut.WaitForAssertion(() => cut.Find("[data-testid='release-history-entry']"));

        await cut.InvokeAsync(() => cut.Find("[data-testid='release-history-refresh']").Click());

        cut.WaitForAssertion(() => cut.Find("[data-testid='release-history-refresh-error']"));
        Assert.Single(cut.FindAll("[data-testid='release-history-entry']"));
    }

    [Fact]
    public void Orders_numeric_prerelease_identifiers_using_semver_rules()
    {
        RegisterJson("""
            {"schemaVersion":"1.0.0","releases":[
              {"version":"1.0.0-alpha.10","tag":"v1.0.0-alpha.10","commit":"2222222222222222222222222222222222222222","releasedAt":"2026-09-20","releaseUrl":"https://github.com/Sytone/botnexus/releases/tag/v1.0.0-alpha.10","documentationUrl":"https://sytone.github.io/botnexus/releases/v1.0.0-alpha.10/","categories":[]},
              {"version":"1.0.0-alpha.2","tag":"v1.0.0-alpha.2","commit":"1111111111111111111111111111111111111111","releasedAt":"2026-09-10","releaseUrl":"https://github.com/Sytone/botnexus/releases/tag/v1.0.0-alpha.2","documentationUrl":"https://sytone.github.io/botnexus/releases/v1.0.0-alpha.2/","categories":[]}
            ]}
            """);

        var cut = _ctx.Render<ReleaseHistory>();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid='release-history-entry']").Count));
    }

    [Fact]
    public void Renders_running_release_and_remote_distance()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            schemaVersion = "1.0.0",
            releases = System.Text.Json.JsonDocument.Parse(SingleReleaseJson("2.0.0", "2222222222222222222222222222222222222222")).RootElement.GetProperty("releases"),
            sourceStatus = SourceStatus(remoteBehind: 14, releaseAhead: 3)
        });
        RegisterJson(json);

        var cut = _ctx.Render<ReleaseHistory>();

        cut.WaitForAssertion(() =>
        {
            var status = cut.Find("[data-testid='release-source-status']").TextContent;
            Assert.Contains("3 commits ahead of release 2.0.0", status, StringComparison.Ordinal);
            Assert.Contains("14 commits behind origin/main", status, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Uses_same_origin_gateway_endpoint()
    {
        Uri? requestedUri = null;
        RegisterClient(new DelegateHandler((request, _) =>
        {
            requestedUri = request.RequestUri;
            return Task.FromResult(JsonResponse("""{"schemaVersion":"1.0.0","releases":[]}"""));
        }));

        var cut = _ctx.Render<ReleaseHistory>();

        cut.WaitForAssertion(() => Assert.Equal("http://localhost/api/release-history", requestedUri?.ToString()));
    }

    [Fact]
    public void Normalizes_canonical_markdown_summary_to_plain_text()
    {
        RegisterJson(SingleReleaseJson("2.0.0", "2222222222222222222222222222222222222222", "**portal:** Add [fictional guide](https://sytone.github.io/botnexus/user-guide/fictional)"));

        var cut = _ctx.Render<ReleaseHistory>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("portal: Add fictional guide", cut.Markup, StringComparison.Ordinal);
            Assert.DoesNotContain("**portal:**", cut.Markup, StringComparison.Ordinal);
        });
    }

    private static string SingleReleaseJson(string version, string commit, string summary = "Add fictional capability") => $$"""
        {"schemaVersion":"1.0.0","releases":[
          {"version":"{{version}}","tag":"v{{version}}","commit":"{{commit}}","releasedAt":"2026-09-20","releaseUrl":"https://github.com/Sytone/botnexus/releases/tag/v{{version}}","documentationUrl":"https://sytone.github.io/botnexus/releases/v{{version}}/","categories":[{"name":"Features","changes":[{"summary":"{{summary}}","documentationUrls":[]}]}]}
        ]}
        """;

    private static HttpResponseMessage JsonResponse(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var root = document.RootElement;
        var response = root.TryGetProperty("sourceStatus", out _)
            ? json
            : System.Text.Json.JsonSerializer.Serialize(new
            {
                schemaVersion = root.GetProperty("schemaVersion").GetString(),
                releases = root.GetProperty("releases"),
                sourceStatus = SourceStatus()
            });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(response, Encoding.UTF8, "application/json")
        };
    }

    private void RegisterJson(string json) =>
        RegisterClient(new DelegateHandler((_, _) => Task.FromResult(JsonResponse(json))));

    private static object SourceStatus(int remoteBehind = 0, int releaseAhead = 0) => new
    {
        runningCommit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        runningCommitShort = "aaaaaaa",
        checkoutHead = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        checkoutHeadShort = "aaaaaaa",
        latestReleaseVersion = "2.0.0",
        latestReleaseCommit = "2222222222222222222222222222222222222222",
        releaseDistance = new
        {
            targetCommit = "2222222222222222222222222222222222222222",
            targetCommitShort = "2222222",
            ahead = releaseAhead,
            behind = 0
        },
        remoteName = "origin",
        remoteBranch = "main",
        remoteHead = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
        remoteDistance = new
        {
            targetCommit = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            targetCommitShort = "bbbbbbb",
            ahead = 0,
            behind = remoteBehind
        },
        remoteRefreshError = (string?)null
    };

    private void RegisterClient(HttpMessageHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        _ctx.Services.AddSingleton(http);
        _ctx.Services.AddSingleton(new ReleaseHistoryClient(http));
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }
}
