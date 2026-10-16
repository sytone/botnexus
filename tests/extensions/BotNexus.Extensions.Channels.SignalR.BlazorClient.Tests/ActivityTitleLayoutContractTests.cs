using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>Shipped CSS contracts for #4882, supplementing DOM tests and browser geometry.</summary>
public sealed class ActivityTitleLayoutContractTests
{
    private static string ReadCss()
    {
        var assemblyDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
            ?? throw new InvalidOperationException("Test assembly directory is unavailable.");
        return Regex.Replace(File.ReadAllText(Path.Combine(assemblyDirectory, "wwwroot", "css", "app.css")),
            @"/\*.*?\*/", "", RegexOptions.Singleline);
    }

    private static string Rule(string css, string selector)
    {
        var blocks = Regex.Matches(css, @"([^{}]+)\{([^{}]*)\}");
        var matching = blocks.Where(block => block.Groups[1].Value.Split(',')
            .Any(value => value.Trim() == selector)).ToList();
        matching.Count.ShouldBe(1, $"Expected one shipped rule for {selector}.");
        return matching[0].Groups[2].Value;
    }

    [Fact]
    public void Title_cell_remains_table_cell_and_only_inner_content_is_flex()
    {
        var css = ReadCss();
        var cell = Rule(css, ".activity-cell-title");
        cell.ShouldMatch(@"display:\s*table-cell\s*;");
        cell.ShouldMatch(@"white-space:\s*normal\s*;");
        cell.ShouldMatch(@"overflow:\s*visible\s*;");
        cell.ShouldNotMatch(@"display:\s*flex\s*;");
        cell.ShouldNotMatch(@"max-width:");
        var inner = Rule(css, ".activity-title-content");
        inner.ShouldMatch(@"display:\s*flex\s*;");
        inner.ShouldMatch(@"flex-wrap:\s*wrap\s*;");
        inner.ShouldMatch(@"min-width:\s*0\s*;");
        inner.ShouldMatch(@"width:\s*100%\s*;");
    }

    [Fact]
    public void Full_title_wraps_on_own_row_without_clipping_and_badges_remain_nonshrinking()
    {
        var css = ReadCss();
        var title = Rule(css, ".activity-conversation-title");
        title.ShouldMatch(@"flex:\s*1\s+1\s+100%\s*;");
        title.ShouldMatch(@"white-space:\s*normal\s*;");
        title.ShouldMatch(@"overflow-wrap:\s*anywhere\s*;");
        title.ShouldMatch(@"overflow:\s*visible\s*;");
        title.ShouldNotMatch(@"text-overflow:\s*ellipsis");
        Rule(css, ".activity-badge").ShouldMatch(@"flex:\s*0\s+0\s+auto\s*;");
    }

    [Fact]
    public void Cron_allocates_all_nine_columns_independently_with_readable_name_and_numeric_alignment()
    {
        var css = ReadCss();
        var widths = Enumerable.Range(1, 9).Select(index =>
        {
            var selector = $".activity-cron-cost .activity-table td:nth-child({index})";
            var match = Regex.Match(Rule(css, selector), @"width:\s*([\d.]+)%\s*;");
            match.Success.ShouldBeTrue($"Missing percentage width for {selector}.");
            Rule(css, $".activity-cron-cost .activity-table th:nth-child({index})").ShouldBe(Rule(css, selector));
            return double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        }).ToArray();
        widths.ShouldAllBe(width => width > 0);
        widths.Sum().ShouldBe(100d, 0.01d);
        widths[0].ShouldBeGreaterThanOrEqualTo(30d);
        widths.Skip(1).ShouldAllBe(width => width < widths[0]);
        var numeric = Rule(css, ".activity-cron-cost .activity-table td:nth-child(n+2)");
        numeric.ShouldMatch(@"text-align:\s*right\s*;");
        numeric.ShouldContain("tabular-nums");
        Rule(css, ".activity-cron-cost .activity-table th:nth-child(n+2)").ShouldBe(numeric);
        var minimum = Regex.Match(Rule(css, ".activity-cron-cost .activity-table"), @"min-width:\s*([\d.]+)px\s*;");
        minimum.Success.ShouldBeTrue();
        double.Parse(minimum.Groups[1].Value, CultureInfo.InvariantCulture).ShouldBeGreaterThanOrEqualTo(1200d);
        Rule(css, ".activity-table-scroll").ShouldMatch(@"overflow-x:\s*auto\s*;");
        Rule(css, ".activity-table thead th").ShouldMatch(@"overflow-wrap:\s*anywhere\s*;");
    }
}
