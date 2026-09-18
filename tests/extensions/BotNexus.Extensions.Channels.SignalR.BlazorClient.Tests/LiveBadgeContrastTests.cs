using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed partial class LiveBadgeContrastTests
{
    private const double NormalTextMinimumContrast = 4.5;

    private static readonly string s_cssPath = Path.Combine(
        Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!,
        "wwwroot",
        "css",
        "app.css");

    [Theory]
    [InlineData(":root", "dark")]
    [InlineData("[data-theme=\"light\"]", "light")]
    public void LiveBadge_TextBearingColorsMeetNormalTextContrast(string themeSelector, string themeName)
    {
        var css = File.ReadAllText(s_cssPath);
        var theme = Rule(css, themeSelector);
        var badge = Rule(css, ".activity-badge-live");
        var sharedBadge = Rule(css, ".activity-badge");

        var backgroundToken = Declaration(badge, "background");
        var foregroundToken = Declaration(badge, "color");
        var background = ResolveColor(css, theme, backgroundToken);
        var foreground = ResolveColor(css, theme, foregroundToken);
        var fontSize = ResolveLength(css, Declaration(sharedBadge, "font-size"));
        var contrast = ContrastRatio(foreground, background);

        Assert.True(fontSize < 24,
            $"The {themeName} Live badge is {fontSize:0.##}px and must satisfy the normal-text threshold.");
        Assert.True(contrast >= NormalTextMinimumContrast,
            $"The {themeName} Live badge resolves to {foregroundToken} ({foreground}) on " +
            $"{backgroundToken} ({background}) at {fontSize:0.##}px: {contrast:0.###}:1 is below " +
            $"the {NormalTextMinimumContrast:0.0}:1 normal-text threshold.");
    }

    private static string Rule(string css, string selector)
    {
        var match = Regex.Match(css, $@"(?m)^\s*{Regex.Escape(selector)}\s*\{{(?<body>.*?)^\s*\}}", RegexOptions.Singleline);
        Assert.True(match.Success, $"CSS rule '{selector}' was not found.");
        return match.Groups["body"].Value;
    }

    private static string Declaration(string rule, string property)
    {
        var match = Regex.Match(rule, $@"(?m)^\s*{Regex.Escape(property)}\s*:\s*(?<value>[^;]+);");
        Assert.True(match.Success, $"CSS declaration '{property}' was not found.");
        return match.Groups["value"].Value.Trim();
    }

    private static string ResolveColor(string css, string theme, string value)
    {
        var tokenMatch = CssVariableRegex().Match(value);
        if (!tokenMatch.Success)
            return value;

        var token = tokenMatch.Groups["token"].Value;
        var themeValue = TryDeclaration(theme, token);
        var rootValue = TryDeclaration(Rule(css, ":root"), token);
        var resolved = themeValue ?? rootValue ?? throw new InvalidOperationException($"CSS token '{token}' was not declared.");
        return ResolveColor(css, theme, resolved);
    }

    private static double ResolveLength(string css, string value)
    {
        var tokenMatch = CssVariableRegex().Match(value);
        if (tokenMatch.Success)
        {
            var resolved = Declaration(Rule(css, ":root"), tokenMatch.Groups["token"].Value);
            return ResolveLength(css, resolved);
        }

        var match = Regex.Match(value, @"^(?<number>\d+(?:\.\d+)?)(?<unit>px|rem)$");
        Assert.True(match.Success, $"Unsupported CSS length '{value}'.");
        var number = double.Parse(match.Groups["number"].Value, CultureInfo.InvariantCulture);
        return match.Groups["unit"].Value == "rem" ? number * 16 : number;
    }

    private static string? TryDeclaration(string rule, string property)
    {
        var match = Regex.Match(rule, $@"(?m)^\s*{Regex.Escape(property)}\s*:\s*(?<value>[^;]+);");
        return match.Success ? match.Groups["value"].Value.Trim() : null;
    }

    private static double ContrastRatio(string foreground, string background)
    {
        var foregroundLuminance = RelativeLuminance(foreground);
        var backgroundLuminance = RelativeLuminance(background);
        return (Math.Max(foregroundLuminance, backgroundLuminance) + 0.05) /
               (Math.Min(foregroundLuminance, backgroundLuminance) + 0.05);
    }

    private static double RelativeLuminance(string hex)
    {
        Assert.Matches("^#[0-9a-fA-F]{6}$", hex);
        var channels = new[] { hex[1..3], hex[3..5], hex[5..7] }
            .Select(channel => int.Parse(channel, NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255d)
            .Select(channel => channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4))
            .ToArray();
        return 0.2126 * channels[0] + 0.7152 * channels[1] + 0.0722 * channels[2];
    }

    [GeneratedRegex(@"^var\((?<token>--[a-z0-9-]+)\)$", RegexOptions.IgnoreCase)]
    private static partial Regex CssVariableRegex();
}
