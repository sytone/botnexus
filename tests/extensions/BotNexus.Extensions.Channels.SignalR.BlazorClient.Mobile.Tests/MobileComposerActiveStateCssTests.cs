namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class MobileComposerActiveStateCssTests
{
    private static readonly string s_cssPath = FindRepositoryFile(
        "src",
        "extensions",
        "BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile",
        "wwwroot",
        "css",
        "mobile.css");

    [Fact]
    public void Mobile_active_composer_has_non_intercepting_perimeter_and_accessibility_fallbacks()
    {
        var content = File.ReadAllText(s_cssPath);

        content.ShouldContain(".bottom-bar.composer-active::before");
        content.ShouldContain("pointer-events: none");
        content.ShouldContain("offset-path: inset");
        content.ShouldContain("offset-distance: 100%");
        content.ShouldContain("animation: mobile-composer-perimeter-travel");
        content.ShouldContain("@media (prefers-reduced-motion: reduce)");
        content.ShouldContain("animation: none");
        content.ShouldContain("@media (forced-colors: active)");
        content.ShouldContain("outline: 2px solid Highlight");
    }

    private static string FindRepositoryFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Repository file not found: {Path.Combine(segments)}");
    }
}
