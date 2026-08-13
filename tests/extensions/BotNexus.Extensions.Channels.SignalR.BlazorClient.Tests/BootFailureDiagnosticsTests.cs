using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Xunit;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Issue #2880: when a <c>_framework/*.wasm</c> asset fails to download, Blazor's start
/// promise rejects and the static <c>.loading-screen</c> div is never torn down, so the user
/// sees "Loading BotNexus..." forever and the only diagnosis is devtools.
///
/// The critical distinction these tests pin is that an authenticating reverse proxy answering
/// a sub-resource request with an HTML login page is INDISTINGUISHABLE from a genuine platform
/// fault in the browser -- both surface as an opaque <c>TypeError: Failed to fetch</c> because
/// SRI erases the real cause. Only a follow-up probe of the response content type can tell them
/// apart, and the panel must say which one it was.
///
/// There is no jsdom/Jint harness in this repository, so -- following the precedent set by
/// <see cref="MarkdownRendererFailClosedTests"/> -- these tests execute the real
/// <c>wwwroot/js/bootDiagnostics.js</c> under Node. Node is a hard requirement (present on all
/// GitHub-hosted runners and in the remote validation container): the tests FAIL rather than
/// skip if it cannot be launched, so they can never pass vacuously.
/// </summary>
public sealed class BootFailureDiagnosticsTests
{
    private static string RepoRelative(params string[] parts)
    {
        var head = new[]
        {
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..",
        };
        var all = new string[head.Length + parts.Length];
        head.CopyTo(all, 0);
        parts.CopyTo(all, head.Length);
        return Path.GetFullPath(Path.Combine(all));
    }

    private static readonly string BootDiagnosticsJsPath = RepoRelative(
        "src", "extensions", "BotNexus.Extensions.Channels.SignalR.BlazorClient",
        "wwwroot", "js", "bootDiagnostics.js");

    private static readonly string DesktopIndexHtmlPath = RepoRelative(
        "src", "extensions", "BotNexus.Extensions.Channels.SignalR.BlazorClient",
        "wwwroot", "index.html");

    private static readonly string MobileIndexHtmlPath = RepoRelative(
        "src", "extensions", "BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile",
        "wwwroot", "index.html");

    private const string FailedToFetch =
        "Failed to start platform. Reason: Error: download 'https://n.netbird.sytone.net/_framework/"
        + "System.cvgusyd4es.wasm' for System.cvgusyd4es.wasm failed 0 TypeError: Failed to fetch";

    // ---------------------------------------------------------------- asset presence

    [Fact]
    public void BootDiagnostics_js_exists()
    {
        Assert.True(
            File.Exists(BootDiagnosticsJsPath),
            $"bootDiagnostics.js not found at {BootDiagnosticsJsPath}");
    }

    // ---------------------------------------------------------------- AC1: autostart=false + handler

    [Fact]
    public void Desktop_index_disables_blazor_autostart()
    {
        var content = File.ReadAllText(DesktopIndexHtmlPath);

        Assert.Matches(
            "<script[^>]*blazor\\.webassembly[^>]*autostart=\"false\"",
            content);
    }

    [Fact]
    public void Desktop_index_loads_boot_diagnostics_and_starts_through_it()
    {
        var content = File.ReadAllText(DesktopIndexHtmlPath);

        Assert.Contains("js/bootDiagnostics.js", content, StringComparison.Ordinal);
        Assert.Contains("BotNexusBoot.startBlazor", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Mobile_index_disables_blazor_autostart_and_starts_through_boot_diagnostics()
    {
        // AC6: the mobile client is NOT exempt -- it has the same static loading div and the
        // same default-autostart script tag, so it gets the same handler.
        var content = File.ReadAllText(MobileIndexHtmlPath);

        Assert.Matches("<script[^>]*blazor\\.webassembly[^>]*autostart=\"false\"", content);
        Assert.Contains("js/bootDiagnostics.js", content, StringComparison.Ordinal);
        Assert.Contains("BotNexusBoot.startBlazor", content, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- AC2 + AC3: panel replaces spinner

    [Fact]
    public void Failed_boot_removes_the_loading_screen_from_app()
    {
        var html = RunBootScenario(probeContentType: null, probeThrows: true);

        Assert.DoesNotContain("loading-screen", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Loading BotNexus", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Failed_boot_renders_a_panel_containing_the_underlying_error_message()
    {
        var html = RunBootScenario(probeContentType: null, probeThrows: true);

        Assert.Contains("data-testid=\"boot-failure-panel\"", html, StringComparison.Ordinal);
        Assert.Contains("Failed to start platform", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Failed_boot_panel_offers_a_reload_control()
    {
        var html = RunBootScenario(probeContentType: null, probeThrows: true);

        Assert.Contains("data-testid=\"boot-failure-reload\"", html, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- AC4: auth interstitial vs platform fault

    [Fact]
    public void Html_content_type_on_a_framework_asset_is_reported_as_an_auth_interstitial()
    {
        // The exact #2880 incident: NetBird's OAuth2 proxy answers the sub-resource with 200 OK
        // and a login page body. The browser only ever sees "TypeError: Failed to fetch".
        var html = RunBootScenario(probeContentType: "text/html; charset=utf-8", probeThrows: false);

        Assert.Contains("data-testid=\"boot-failure-panel\"", html, StringComparison.Ordinal);
        Assert.Contains("boot-failure-kind-auth-interstitial", html, StringComparison.Ordinal);
        Assert.Contains("sign in", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Auth_interstitial_panel_does_not_blame_the_botnexus_platform()
    {
        var html = RunBootScenario(probeContentType: "text/html", probeThrows: false);

        // Sad path for the classifier: presenting an expired SSO session as a platform fault is
        // exactly the misdiagnosis #2880 exists to stop. The word "platform" may still appear in
        // the echoed browser error text, so this asserts the CLASSIFICATION, not the transcript.
        Assert.DoesNotContain("boot-failure-kind-platform", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Auth_interstitial_panel_links_to_the_origin_root_for_reauthentication()
    {
        var html = RunBootScenario(probeContentType: "text/html", probeThrows: false);

        Assert.Contains("data-testid=\"boot-failure-reauth\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Binary_content_type_on_a_reachable_asset_is_reported_as_a_platform_fault()
    {
        // Happy-path probe: the asset really is served correctly, so the boot failure is ours.
        var html = RunBootScenario(probeContentType: "application/wasm", probeThrows: false);

        Assert.Contains("boot-failure-kind-platform", html, StringComparison.Ordinal);
        Assert.DoesNotContain("boot-failure-kind-auth-interstitial", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Unreachable_probe_is_reported_as_a_platform_fault_not_an_auth_interstitial()
    {
        // Sad path: the probe itself fails. Guessing "auth" here would be a fabricated diagnosis.
        var html = RunBootScenario(probeContentType: null, probeThrows: true);

        Assert.Contains("boot-failure-kind-platform", html, StringComparison.Ordinal);
        Assert.DoesNotContain("boot-failure-kind-auth-interstitial", html, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- AC1: no unhandled rejection

    [Fact]
    public void Failed_boot_does_not_leave_an_unhandled_rejection()
    {
        var probe = RunDriver(BuildStartDriver(probeContentType: null, probeThrows: true, emit: "unhandled"));

        Assert.Equal("none", probe.Trim());
    }

    [Fact]
    public void Successful_boot_leaves_the_loading_screen_for_blazor_to_replace()
    {
        // Non-vacuity anchor: the panel must appear ONLY on failure. If startBlazor rendered the
        // panel unconditionally every failure assertion above would pass for the wrong reason.
        var html = RunDriver(BuildStartDriver(
            probeContentType: null, probeThrows: false, emit: "html", blazorSucceeds: true));

        Assert.Contains("loading-screen", html, StringComparison.Ordinal);
        Assert.DoesNotContain("boot-failure-panel", html, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- reporting seam

    [Fact]
    public void Classified_failure_is_reported_with_its_cause_rather_than_the_erased_TypeError()
    {
        var reported = RunDriver(BuildStartDriver(
            probeContentType: "text/html", probeThrows: false, emit: "report"));

        Assert.Contains("auth-interstitial", reported, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- harness

    private static string RunBootScenario(string? probeContentType, bool probeThrows)
        => RunDriver(BuildStartDriver(probeContentType, probeThrows, emit: "html"));

    /// <summary>
    /// Builds a Node driver that loads the real bootDiagnostics.js against a minimal DOM shim and
    /// an injected fetch, drives <c>startBlazor</c> to rejection, and prints the resulting
    /// <c>#app</c> markup (or the reporting/unhandled-rejection observation).
    /// </summary>
    private static string BuildStartDriver(
        string? probeContentType,
        bool probeThrows,
        string emit,
        bool blazorSucceeds = false)
    {
        var probeBody = probeThrows
            ? "return Promise.reject(new TypeError('Failed to fetch'));"
            : $"return Promise.resolve({{ ok: true, status: 200, headers: {{ get: function (n) {{ return String(n).toLowerCase() === 'content-type' ? {JsonSerializer.Serialize(probeContentType)} : null; }} }} }});";

        var blazorBody = blazorSucceeds
            ? "return Promise.resolve();"
            : $"return Promise.reject(new Error({JsonSerializer.Serialize(FailedToFetch)}));";

        return $$"""
            globalThis.window = globalThis;
            globalThis.navigator = globalThis.navigator || { userAgent: 'node' };
            globalThis.location = { origin: 'https://n.netbird.sytone.net', href: 'https://n.netbird.sytone.net/', reload: function () { } };
            globalThis.window.location = globalThis.location;

            // Minimal DOM shim: #app really contains the static loading screen, so any assertion
            // that the spinner is gone can only pass if the code actually replaced the markup.
            var __app = {
              id: 'app',
              innerHTML: '<div class="loading-screen"><div class="loading-spinner"></div><p>Loading BotNexus...</p></div>'
            };
            globalThis.document = {
              readyState: 'complete',
              getElementById: function (id) { return id === 'app' ? __app : null; },
              addEventListener: function () { }
            };

            var __reported = [];
            var __unhandled = 'none';
            globalThis.process.on('unhandledRejection', function (r) {
              __unhandled = String((r && r.message) || r);
            });

            require({{JsonSerializer.Serialize(BootDiagnosticsJsPath)}});

            var __p = window.BotNexusBoot.startBlazor({
              blazorStart: function () { {{blazorBody}} },
              fetchFn: function () { {{probeBody}} },
              reportFn: function (payload) { __reported.push(JSON.stringify(payload)); }
            });

            __p.then(function () {
              setTimeout(function () {
                if ({{JsonSerializer.Serialize(emit)}} === 'html') { process.stdout.write(__app.innerHTML); }
                else if ({{JsonSerializer.Serialize(emit)}} === 'report') { process.stdout.write(__reported.join('\n')); }
                else { process.stdout.write(__unhandled); }
              }, 10);
            });
            """;
    }

    private static string RunDriver(string driverSource)
    {
        Assert.True(
            File.Exists(BootDiagnosticsJsPath),
            $"bootDiagnostics.js not found at {BootDiagnosticsJsPath}");

        var tempDir = Path.Combine(Path.GetTempPath(), "botnexus-boot-2880-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var driverPath = Path.Combine(tempDir, "driver.js");
            File.WriteAllText(driverPath, driverSource);

            var psi = new ProcessStartInfo("node", "\"" + driverPath + "\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            using var process = Process.Start(psi);
            Assert.NotNull(process);

            var stdout = process!.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            Assert.True(
                process.ExitCode == 0,
                $"node driver failed (exit {process.ExitCode}). stderr: {stderr}");

            return stdout;
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch (IOException) { }
        }
    }
}
