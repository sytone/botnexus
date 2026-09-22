using System.Text.RegularExpressions;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile.Tests;

public sealed partial class MobileIconStyleTests
{
    private static readonly string s_repoRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));

    private static readonly string s_mobileWwwroot = Path.Combine(
        s_repoRoot, "src", "extensions", "BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile", "wwwroot");

    private static readonly string s_desktopWwwroot = Path.Combine(
        s_repoRoot, "src", "extensions", "BotNexus.Extensions.Channels.SignalR.BlazorClient", "wwwroot");

    private static readonly string s_coreWwwroot = Path.Combine(
        s_repoRoot, "src", "extensions", "BotNexus.Extensions.Channels.SignalR.BlazorClient.Core", "wwwroot");

    [Theory]
    [InlineData("mobile")]
    [InlineData("desktop")]
    public void LoadedStylesDefineSharedIconBehavior(string client)
    {
        var css = ReadLoadedStyles(client == "mobile" ? s_mobileWwwroot : s_desktopWwwroot);

        css.ShouldContain(".bn-icon-spin");
        css.ShouldContain("animation: bn-icon-spin");
        css.ShouldContain("@keyframes bn-icon-spin");
        css.ShouldMatch(@"@media\s*\(prefers-reduced-motion:\s*reduce\)[\s\S]*?\.bn-icon-spin\s*\{[\s\S]*?animation:\s*none");
        css.ShouldContain(".bn-icon-flat { stroke: currentColor; }");
        css.ShouldContain(".bn-icon-inherit { color: inherit; stroke: currentColor; }");
        css.ShouldContain(".bn-icon-check { color:");
        css.ShouldContain(".bn-icon-error { color:");
    }

    private static string ReadLoadedStyles(string wwwroot)
    {
        var html = File.ReadAllText(Path.Combine(wwwroot, "index.html"));
        var hrefs = StylesheetRegex().Matches(html).Select(match => match.Groups[1].Value);

        return string.Join("\n", hrefs.Select(href => File.ReadAllText(ResolveStylesheet(wwwroot, href))));
    }

    private static string ResolveStylesheet(string clientWwwroot, string href)
    {
        const string corePrefix = "_content/BotNexus.Extensions.Channels.SignalR.BlazorClient.Core/";
        var path = href.Split('?', 2)[0].Replace('/', Path.DirectorySeparatorChar);

        return path.StartsWith(corePrefix, StringComparison.Ordinal)
            ? Path.Combine(s_coreWwwroot, path[corePrefix.Length..])
            : Path.Combine(clientWwwroot, path);
    }

    [GeneratedRegex("<link[^>]+rel=\"stylesheet\"[^>]+href=\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex StylesheetRegex();
}
