using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace BotNexus.Integration.ExtensionBoot.Tests;

/// <summary>
/// The extension-boot smoke gate (issue #2220).
///
/// Extension-assembly-load regressions (Service Bus config identity, Whisper.net,
/// IFileSystem type-identity divergence) shipped green because no gate booted the
/// gateway with the FULL extension set deployed: `botnexus validate` / `doctor config`
/// and unit tests never load extensions, and the daily Docker boot check uses only
/// default/config/wildcard config (extensions are never deployed), so the isolated
/// ExtensionAssemblyLoadContext path was never taken.
///
/// This gate boots the gateway through the real CLI with every extension deployed and
/// asserts:
///   * GET /health returns healthy, and
///   * GET /api/extensions/health reports zero extension-load failures - and on
///     failure it names the offending assembly/type rather than only a timeout.
///
/// A future ExtensionAssemblyLoadContext regression fails here, in the PR validation
/// path, before it can reach production.
/// </summary>
[Collection(ExtensionBootCollection.Name)]
public sealed class ExtensionBootSmokeTests
{
    private readonly ExtensionBootFixture _fx;

    public ExtensionBootSmokeTests(ExtensionBootFixture fx) => _fx = fx;

    [SkippableFact]
    public void GatewayBootedWithExtensionsDeployed()
    {
        Skip.If(ShouldSkip(), SkipReason());

        // DeployExtensions writes every manifest-bearing extension into {home}/extensions.
        var deployRoot = Path.Combine(_fx.Home, "extensions");
        Directory.Exists(deployRoot)
            .ShouldBeTrue($"extensions were not deployed to {deployRoot}.\n{_fx.GatewayOutput()}");

        var deployed = Directory.GetDirectories(deployRoot);
        deployed.Length.ShouldBeGreaterThan(0,
            $"no extensions were deployed to {deployRoot} - the boot gate would not exercise the load path.");
    }

    [SkippableFact]
    public async Task GatewayHealthEndpointReturnsHealthy()
    {
        Skip.If(ShouldSkip(), SkipReason());

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var resp = await http.GetAsync($"{_fx.GatewayBaseUrl}/health");
        resp.StatusCode.ShouldBe(HttpStatusCode.OK,
            $"GET /health returned {(int)resp.StatusCode}.\n{_fx.GatewayOutput()}");
    }

    [SkippableFact]
    public async Task ExtensionHealthEndpointReportsNoLoadFailures()
    {
        Skip.If(ShouldSkip(), SkipReason());

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var resp = await http.GetAsync($"{_fx.GatewayBaseUrl}/api/extensions/health");

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        // On any extension-load failure the endpoint returns 503 and the payload lists
        // the offending extensions with their actual load error (naming the missing or
        // diverged assembly). Surface that verbatim instead of a generic timeout.
        if (resp.StatusCode != HttpStatusCode.OK)
        {
            var failures = root.TryGetProperty("failed", out var failedElement)
                ? string.Join("\n", failedElement.EnumerateArray().Select(FormatFailure))
                : body;
            Assert.Fail(
                $"Extension load failed during gateway boot (GET /api/extensions/health returned {(int)resp.StatusCode}):\n{failures}");
        }

        root.GetProperty("status").GetString().ShouldBe("ok");
        root.GetProperty("failedCount").GetInt32().ShouldBe(0);
        root.GetProperty("loadedCount").GetInt32().ShouldBeGreaterThan(0,
            "no extensions loaded - the gate would pass vacuously without exercising the load path.");
    }

    private static string FormatFailure(JsonElement failure)
    {
        var id = failure.TryGetProperty("id", out var idEl) ? idEl.GetString() : "<unknown>";
        var error = failure.TryGetProperty("error", out var errEl) ? errEl.GetString() : "<no error text>";
        return $"  - {id}: {error}";
    }

    private bool ShouldSkip() => !_fx.Succeeded;

    private string SkipReason() =>
        $"Extension-boot fixture initialization failed: {_fx.Error}\nLog:\n{string.Join("\n", _fx.Log)}";
}
