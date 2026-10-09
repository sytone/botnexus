using System.Reflection;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class ComposerActiveStateCssTests
{
    private static readonly string s_cssPath = Path.Combine(
        Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!,
        "wwwroot",
        "css",
        "app.css");

    [Fact]
    public void Desktop_active_composer_uses_non_intercepting_bottom_edge_pulse()
    {
        var content = File.ReadAllText(s_cssPath);
        var activeRule = RuleBlock(content, ".chat-input-area.composer-active::before {");

        activeRule.ShouldContain("pointer-events: none");
        activeRule.ShouldContain("bottom: 0.3rem");
        activeRule.ShouldContain("height: 2px");
        activeRule.ShouldContain("background: var(--accent)");
        activeRule.ShouldContain("animation: composer-bottom-edge-pulse");
        content.ShouldContain("@keyframes composer-bottom-edge-pulse");
        content.ShouldNotContain("offset-path:");
        content.ShouldNotContain("offset-distance:");
        content.ShouldNotContain("composer-perimeter-travel");
    }

    [Fact]
    public void Desktop_active_composer_retains_static_reduced_motion_and_forced_color_boundaries()
    {
        var content = File.ReadAllText(s_cssPath);

        content.ShouldContain("@media (prefers-reduced-motion: reduce)");
        content.ShouldContain(".chat-input-area.composer-active::before");
        content.ShouldContain("animation: none");
        content.ShouldContain("@media (forced-colors: active)");
        content.ShouldContain("background: Highlight");
        content.ShouldContain(".composer-active.composer-motion-disabled::before");
    }

    private static string RuleBlock(string content, string selector)
    {
        var start = content.IndexOf(selector, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0);
        var end = content.IndexOf('}', start);
        return content.Substring(start, end - start + 1);
    }
}
