using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed partial class ConfirmationButtonContrastTests
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
    public void ConfirmationButtonEnabledStatesKeepSolidTextContrast(string themeSelector, string themeName)
    {
        var css = File.ReadAllText(s_cssPath);
        var theme = Rule(css, themeSelector);
        var button = Rule(css, ".confirm-btn");
        var hover = Rule(css, ".confirm-btn:hover");

        var foreground = ResolveColor(css, theme, Declaration(button, "color"));
        var normalBackground = ResolveColor(css, theme, Declaration(button, "background"));
        var hoverBackground = ResolveColor(css, theme, Declaration(hover, "background"));

        AssertContrast(themeName, "normal", foreground, normalBackground);
        AssertContrast(themeName, "keyboard-focus", foreground, normalBackground);
        AssertContrast(themeName, "hover", foreground, hoverBackground);
        Assert.NotEqual(normalBackground, hoverBackground);
    }

    [Fact]
    public void ConfirmationButtonKeyboardFocusKeepsTheGlobalVisibleIndicator()
    {
        var css = File.ReadAllText(s_cssPath);
        var focus = Rule(css, ":focus-visible");

        Assert.Equal("var(--focus-ring-width) solid var(--focus-ring-color)", Declaration(focus, "outline"));
        Assert.Equal("var(--focus-ring-offset)", Declaration(focus, "outline-offset"));
    }

    private static void AssertContrast(string theme, string state, string foreground, string background)
    {
        var contrast = ContrastRatio(foreground, background);
        Assert.True(contrast >= NormalTextMinimumContrast,
            $"The {theme} confirmation button {state} state resolves to {foreground} on {background}: " +
            $"{contrast:0.###}:1 is below the {NormalTextMinimumContrast:0.0}:1 normal-text threshold.");
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
        var resolved = TryDeclaration(theme, token) ??
                       TryDeclaration(Rule(css, ":root"), token) ??
                       throw new InvalidOperationException($"CSS token '{token}' was not declared.");
        return ResolveColor(css, theme, resolved);
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
