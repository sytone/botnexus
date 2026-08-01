using System;
using System.Diagnostics;
using System.IO;
using Xunit;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Issue #2617: <c>window.BotNexus.renderMarkdown</c> must fail CLOSED when either
/// <c>marked</c> or <c>DOMPurify</c> is missing, returning HTML-escaped plain text
/// instead of unsanitized (or entirely raw) markup.
///
/// There is no pre-existing JS test harness in this repository, so these tests execute
/// the real <c>wwwroot/js/markdown.js</c> under Node with the third-party globals
/// deliberately absent. Node is a hard requirement here (it is present on all
/// GitHub-hosted runners) - the tests fail rather than skip if it cannot be launched,
/// so they can never pass vacuously.
/// </summary>
public sealed class MarkdownRendererFailClosedTests
{
    private static readonly string MarkdownJsPath = Path.GetFullPath(
        Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "..",
            "src", "extensions",
            "BotNexus.Extensions.Channels.SignalR.BlazorClient",
            "wwwroot", "js", "markdown.js"));

    private const string XssPayload = "<img src=x onerror=alert(1)>";

    [Fact]
    public void Markdown_js_exists()
    {
        Assert.True(File.Exists(MarkdownJsPath), $"markdown.js not found at {MarkdownJsPath}");
    }

    [Fact]
    public void Escapes_payload_when_DOMPurify_is_missing()
    {
        var output = RunRenderMarkdown(MarkdownJsPath, withMarked: true, withPurify: false, input: XssPayload);

        Assert.Contains("&lt;img src=x onerror=alert(1)&gt;", output);
        Assert.DoesNotContain("<img", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Escapes_payload_when_marked_is_missing()
    {
        var output = RunRenderMarkdown(MarkdownJsPath, withMarked: false, withPurify: true, input: XssPayload);

        Assert.Contains("&lt;img src=x onerror=alert(1)&gt;", output);
        Assert.DoesNotContain("<img", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Escapes_quotes_and_ampersands_in_degraded_path()
    {
        var output = RunRenderMarkdown(
            MarkdownJsPath, withMarked: false, withPurify: false, input: "a & b \" c ' d < e > f");

        Assert.Equal("a &amp; b &quot; c &#39; d &lt; e &gt; f", output.Trim());
    }

    [Fact]
    public void Warns_naming_the_missing_dependency()
    {
        var warnings = RunRenderMarkdown(
            MarkdownJsPath, withMarked: true, withPurify: false, input: "hello", captureWarnings: true);

        Assert.Contains("DOMPurify", warnings);
    }

    [Fact]
    public void Normal_path_still_renders_html_when_both_libraries_present()
    {
        var output = RunRenderMarkdown(MarkdownJsPath, withMarked: true, withPurify: true, input: "**bold**");

        Assert.Contains("<strong>bold</strong>", output);
        Assert.DoesNotContain("**bold**", output);
    }

    /// <summary>
    /// Executes the real markdown.js under Node with stubbed globals and returns either
    /// the rendered output or the collected console warnings.
    /// </summary>
    internal static string RunRenderMarkdown(
        string markdownJsPath,
        bool withMarked,
        bool withPurify,
        string input,
        bool captureWarnings = false)
    {
        Assert.True(File.Exists(markdownJsPath), $"markdown.js not found at {markdownJsPath}");

        var tempDir = Path.Combine(Path.GetTempPath(), "botnexus-md-2617-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var driverPath = Path.Combine(tempDir, "driver.js");
            File.WriteAllText(driverPath, BuildDriver(markdownJsPath, withMarked, withPurify, input, captureWarnings));

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

    private static string BuildDriver(
        string markdownJsPath, bool withMarked, bool withPurify, string input, bool captureWarnings)
    {
        // A minimal stub of marked that turns **x** into <strong>x</strong>, and of DOMPurify
        // that strips <img> tags. Neither is used on the degraded paths under test.
        var stubs = string.Empty;

        if (withMarked)
        {
            stubs += """
                globalThis.marked = {
                  Renderer: function () { this.link = function (t) { return '<a href="#">x</a>'; }; },
                  parse: function (md) { return '<p>' + md.replace(/\*\*(.+?)\*\*/g, '<strong>$1</strong>') + '</p>'; }
                };
                """;
        }

        if (withPurify)
        {
            stubs += """

                globalThis.DOMPurify = { sanitize: function (html) { return html.replace(/<img[^>]*>/gi, ''); } };
                """;
        }

        var warningsSetup = captureWarnings
            ? "var __warnings = []; console.warn = function () { __warnings.push(Array.prototype.join.call(arguments, ' ')); };"
            : "console.warn = function () { };";

        var tail = captureWarnings
            ? "process.stdout.write(__warnings.join('\\n'));"
            : "process.stdout.write(String(__result));";

        return $$"""
            globalThis.window = globalThis;
            globalThis.navigator = globalThis.navigator || {};
            globalThis.document = { createElement: function () { return { style: {}, setAttribute: function () { }, addEventListener: function () { } }; } };
            {{warningsSetup}}
            {{stubs}}
            require({{System.Text.Json.JsonSerializer.Serialize(markdownJsPath)}});
            var __result = window.BotNexus.renderMarkdown({{System.Text.Json.JsonSerializer.Serialize(input)}});
            {{tail}}
            """;
    }
}
