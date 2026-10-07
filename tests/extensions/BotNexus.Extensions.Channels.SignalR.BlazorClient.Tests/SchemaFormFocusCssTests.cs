using System.Reflection;
using System.Text.RegularExpressions;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class SchemaFormFocusCssTests
{
    private static readonly string s_cssPath = Path.Combine(
        Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!,
        "wwwroot",
        "css",
        "app.css");

    [Fact]
    public void Keyboard_focus_retains_the_global_outline_for_schema_controls()
    {
        var css = File.ReadAllText(s_cssPath).Replace("\r\n", "\n", StringComparison.Ordinal);
        var focusRule = FindRule(css,
            ".schema-field-control > input:focus,\n.schema-field-control > select:focus");

        Assert.Contains("border-color: var(--accent)", focusRule, StringComparison.Ordinal);
        Assert.DoesNotContain("outline: none", focusRule, StringComparison.OrdinalIgnoreCase);

        var globalFocusVisibleRule = FindRule(css, ":focus-visible");
        Assert.Contains("outline: var(--focus-ring-width) solid var(--focus-ring-color)",
            globalFocusVisibleRule,
            StringComparison.Ordinal);
    }

    private static string FindRule(string css, string selector)
    {
        var pattern = $@"{Regex.Escape(selector)}\s*\{{(?<body>[^}}]*)\}}";
        var match = Regex.Match(css, pattern, RegexOptions.CultureInvariant);
        Assert.True(match.Success, $"CSS rule not found: {selector}");
        return match.Groups["body"].Value;
    }
}
