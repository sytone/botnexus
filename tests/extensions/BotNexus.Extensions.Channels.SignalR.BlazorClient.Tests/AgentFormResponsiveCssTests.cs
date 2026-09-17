using System.Reflection;
using System.Text.RegularExpressions;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class AgentFormResponsiveCssTests
{
    private const string DesktopColumns = "minmax(9rem, 12rem) minmax(0, 1fr)";
    private const string NarrowColumns = "minmax(0, 1fr)";

    private static readonly string s_cssPath = Path.Combine(
        Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!,
        "wwwroot",
        "css",
        "app.css");

    [Fact]
    public void Agent_editor_grid_preserves_desktop_columns_and_stacks_below_720px()
    {
        var css = ReadCssWithoutComments();
        var desktopRule = FindRule(css, ".agents-form-grid", 0);
        var narrowMedia = FindBlock(css, "@media (max-width: 720px)");
        var narrowRule = FindRule(narrowMedia, ".agents-form-grid", 0);
        var narrowLabelRule = FindRule(narrowMedia, ".agents-form-grid label", 0);

        Assert.Contains($"grid-template-columns: {DesktopColumns};", desktopRule, StringComparison.Ordinal);
        Assert.Contains($"grid-template-columns: {NarrowColumns};", narrowRule, StringComparison.Ordinal);
        Assert.Contains("gap: 0.2rem;", narrowRule, StringComparison.Ordinal);
        Assert.Contains("padding-top: 0;", narrowLabelRule, StringComparison.Ordinal);
    }

    [Fact]
    public void Narrow_agent_editor_controls_can_use_the_full_single_column_without_overflow()
    {
        var css = ReadCssWithoutComments();
        var narrowMedia = FindBlock(css, "@media (max-width: 720px)");
        var narrowRule = FindRule(narrowMedia, ".agents-form-grid", 0);
        var narrowInputRule = FindRule(narrowMedia, ".agents-form-grid .cfg-input", 0);
        var narrowTextareaRule = FindRule(narrowMedia, ".agents-form-grid .cfg-textarea", 0);
        var narrowControlCellRule = FindRule(narrowMedia, ".agents-form-grid > div", 0);

        Assert.Contains("min-width: 0;", narrowRule, StringComparison.Ordinal);
        Assert.Contains("max-width: 100%;", narrowInputRule, StringComparison.Ordinal);
        Assert.Contains("max-width: 100%;", narrowTextareaRule, StringComparison.Ordinal);
        Assert.Contains("min-width: 0;", narrowControlCellRule, StringComparison.Ordinal);
    }

    private static string ReadCssWithoutComments() =>
        Regex.Replace(File.ReadAllText(s_cssPath), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);

    private static string FindRule(string css, string selector, int startIndex)
    {
        var rulePattern = new Regex(@"(?ms)(?<selectors>[^{}]+)\{");
        foreach (Match match in rulePattern.Matches(css[startIndex..]))
        {
            var selectors = match.Groups["selectors"].Value
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (!selectors.Contains(selector, StringComparer.Ordinal))
            {
                continue;
            }

            var blockStart = startIndex + match.Index;
            var openBrace = startIndex + match.Index + match.Value.LastIndexOf('{');
            return FindBlock(css, blockStart, openBrace);
        }

        throw new Xunit.Sdk.XunitException($"CSS selector '{selector}' was not found.");
    }

    private static string FindBlock(string css, string header)
    {
        var headerIndex = css.IndexOf(header, StringComparison.Ordinal);
        Assert.True(headerIndex >= 0, $"CSS block '{header}' was not found.");
        var openBrace = css.IndexOf('{', headerIndex);
        return FindBlock(css, headerIndex, openBrace);
    }

    private static string FindBlock(string css, int blockStart, int openBrace)
    {
        Assert.True(openBrace >= 0, "CSS block has no opening brace.");
        var depth = 0;
        for (var i = openBrace; i < css.Length; i++)
        {
            if (css[i] == '{')
            {
                depth++;
            }
            else if (css[i] == '}' && --depth == 0)
            {
                return css[blockStart..(i + 1)];
            }
        }

        throw new Xunit.Sdk.XunitException("CSS block has no matching closing brace.");
    }
}
