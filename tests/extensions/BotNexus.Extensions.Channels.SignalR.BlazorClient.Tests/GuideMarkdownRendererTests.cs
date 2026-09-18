using System.Diagnostics;
using System.Text.Json;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class GuideMarkdownRendererTests
{
    private static readonly string MarkdownJsPath = Path.GetFullPath(
        Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "..",
            "src", "extensions",
            "BotNexus.Extensions.Channels.SignalR.BlazorClient",
            "wwwroot", "js", "markdown.js"));

    private static readonly object[] Pages =
    [
        new { id = "getting-started", file = "getting-started.md" },
        new { id = "agents", file = "agents.md" },
        new { id = "channels-signalr", file = "channels/signalr.md" },
        new { id = "channels-telegram", file = "channels/telegram.md" },
    ];

    [Theory]
    [InlineData("getting-started.md", "agents.md", "/guide/agents")]
    [InlineData("channels/signalr.md", "telegram.md#setup", "/guide/channels-telegram#setup")]
    [InlineData("channels/signalr.md", "../agents.md", "/guide/agents")]
    [InlineData("agents.md", "#world-level-instructions", "#world-level-instructions")]
    public void Known_guide_links_resolve_to_in_app_navigation(string currentFile, string href, string expectedHref)
    {
        var html = Render(currentFile, href);

        html.ShouldContain($"href=\"{expectedHref}\"");
        html.ShouldNotContain("target=\"_blank\"");
        html.ShouldNotContain("rel=\"noopener noreferrer\"");
    }

    [Fact]
    public void Unknown_relative_target_keeps_explicit_external_style_behavior()
    {
        var html = Render("getting-started.md", "../tutorials/first-agent.md#setup");

        html.ShouldContain("href=\"../tutorials/first-agent.md#setup\"");
        html.ShouldContain("target=\"_blank\"");
        html.ShouldContain("rel=\"noopener noreferrer\"");
    }

    [Fact]
    public void External_target_keeps_safe_external_attributes()
    {
        var html = Render("getting-started.md", "https://example.test/docs");

        html.ShouldContain("href=\"https://example.test/docs\"");
        html.ShouldContain("target=\"_blank\"");
        html.ShouldContain("rel=\"noopener noreferrer\"");
    }

    [Fact]
    public void Rendered_guide_html_still_passes_through_DOMPurify()
    {
        var html = Render("getting-started.md", "agents.md", includeUnsafeAttribute: true);

        html.ShouldNotContain("onclick", Case.Insensitive);
    }

    private static string Render(string currentFile, string href, bool includeUnsafeAttribute = false)
    {
        File.Exists(MarkdownJsPath).ShouldBeTrue($"markdown.js not found at {MarkdownJsPath}");
        var tempDir = Path.Combine(Path.GetTempPath(), "botnexus-guide-md-3934-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var driverPath = Path.Combine(tempDir, "driver.js");
            File.WriteAllText(driverPath, BuildDriver(currentFile, href, includeUnsafeAttribute));
            var startInfo = new ProcessStartInfo("node", driverPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            using var process = Process.Start(startInfo);
            process.ShouldNotBeNull();
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            process.ExitCode.ShouldBe(0, $"node driver failed: {stderr}");
            return stdout;
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch (IOException) { }
        }
    }

    private static string BuildDriver(string currentFile, string href, bool includeUnsafeAttribute) => $$"""
        globalThis.window = globalThis;
        globalThis.navigator = {};
        globalThis.console = { warn: function () {} };
        globalThis.marked = {
          Renderer: function () {
            this.link = function (token) {
              var unsafe = {{(includeUnsafeAttribute ? "' onclick=\"alert(1)\"'" : "''")}};
              return '<a href="' + token.href + '"' + unsafe + '>' + token.text + '</a>';
            };
          },
          parse: function (_, options) {
            return options.renderer.link({ href: {{JsonSerializer.Serialize(href)}}, text: 'link' });
          }
        };
        globalThis.DOMPurify = {
          sanitize: function (html) { return html.replace(/ onclick="[^"]*"/gi, ''); }
        };
        require({{JsonSerializer.Serialize(MarkdownJsPath)}});
        var result = window.BotNexus.renderGuideMarkdown(
          '[link](target)',
          {{JsonSerializer.Serialize(currentFile)}},
          {{JsonSerializer.Serialize(Pages)}});
        process.stdout.write(result);
        """;
}
