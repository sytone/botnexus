using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using Xunit;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile.Tests;

/// <summary>
/// Issue #2617 (mobile portal copy): <c>window.BotNexus.renderMarkdown</c> must fail CLOSED
/// when <c>marked</c> or <c>DOMPurify</c> is missing. The mobile portal ships its own copy of
/// <c>wwwroot/js/markdown.js</c>; these tests execute that copy directly under Node and also
/// pin it byte-identical to the desktop copy so the two cannot drift.
/// </summary>
public sealed class MobileMarkdownRendererFailClosedTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));

    private static readonly string MobileMarkdownJs = Path.Combine(
        RepoRoot, "src", "extensions",
        "BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile", "wwwroot", "js", "markdown.js");

    private static readonly string DesktopMarkdownJs = Path.Combine(
        RepoRoot, "src", "extensions",
        "BotNexus.Extensions.Channels.SignalR.BlazorClient", "wwwroot", "js", "markdown.js");

    private const string XssPayload = "<img src=x onerror=alert(1)>";

    [Fact]
    public void Mobile_and_desktop_markdown_js_are_byte_identical()
    {
        Assert.True(File.Exists(MobileMarkdownJs), $"missing {MobileMarkdownJs}");
        Assert.True(File.Exists(DesktopMarkdownJs), $"missing {DesktopMarkdownJs}");

        Assert.Equal(Hash(DesktopMarkdownJs), Hash(MobileMarkdownJs));
    }

    [Fact]
    public void Escapes_payload_when_DOMPurify_is_missing()
    {
        var output = Run(withMarked: true, withPurify: false, input: XssPayload);

        Assert.Contains("&lt;img src=x onerror=alert(1)&gt;", output);
        Assert.DoesNotContain("<img", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Escapes_payload_when_marked_is_missing()
    {
        var output = Run(withMarked: false, withPurify: true, input: XssPayload);

        Assert.Contains("&lt;img src=x onerror=alert(1)&gt;", output);
        Assert.DoesNotContain("<img", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Normal_path_still_renders_html_when_both_libraries_present()
    {
        var output = Run(withMarked: true, withPurify: true, input: "**bold**");

        Assert.Contains("<strong>bold</strong>", output);
        Assert.DoesNotContain("**bold**", output);
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string Run(bool withMarked, bool withPurify, string input)
    {
        Assert.True(File.Exists(MobileMarkdownJs), $"missing {MobileMarkdownJs}");

        var tempDir = Path.Combine(Path.GetTempPath(), "botnexus-md-2617-m-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var stubs = string.Empty;
            if (withMarked)
            {
                stubs += """
                    globalThis.marked = {
                      Renderer: function () { this.link = function () { return '<a href="#">x</a>'; }; },
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

            var driver = $$"""
                globalThis.window = globalThis;
                globalThis.navigator = globalThis.navigator || {};
                globalThis.document = { createElement: function () { return { style: {}, setAttribute: function () { }, addEventListener: function () { } }; } };
                console.warn = function () { };
                {{stubs}}
                require({{System.Text.Json.JsonSerializer.Serialize(MobileMarkdownJs)}});
                process.stdout.write(String(window.BotNexus.renderMarkdown({{System.Text.Json.JsonSerializer.Serialize(input)}})));
                """;

            var driverPath = Path.Combine(tempDir, "driver.js");
            File.WriteAllText(driverPath, driver);

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

            Assert.True(process.ExitCode == 0, $"node driver failed (exit {process.ExitCode}). stderr: {stderr}");
            return stdout;
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch (IOException) { }
        }
    }
}
