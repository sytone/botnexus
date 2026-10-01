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
    public void Desktop_active_composer_uses_non_intercepting_perimeter_animation()
    {
        var content = File.ReadAllText(s_cssPath);
        var activeRule = RuleBlock(content, ".chat-input-area.composer-active::before {");

        activeRule.ShouldContain("pointer-events: none");
        activeRule.ShouldContain("offset-path: inset");
        activeRule.ShouldContain("animation: composer-perimeter-travel");
        content.ShouldContain("@keyframes composer-perimeter-travel");
        content.ShouldContain("offset-distance: 100%");
    }

    [Fact]
    public void Desktop_active_composer_retains_static_reduced_motion_and_forced_color_boundaries()
    {
        var content = File.ReadAllText(s_cssPath);

        content.ShouldContain("@media (prefers-reduced-motion: reduce)");
        content.ShouldContain(".chat-input-area.composer-active::before");
        content.ShouldContain("animation: none");
        content.ShouldContain("@media (forced-colors: active)");
        content.ShouldContain("outline: 2px solid Highlight");
    }

    private static string RuleBlock(string content, string selector)
    {
        var start = content.IndexOf(selector, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0);
        var end = content.IndexOf('}', start);
        return content.Substring(start, end - start + 1);
    }
}
