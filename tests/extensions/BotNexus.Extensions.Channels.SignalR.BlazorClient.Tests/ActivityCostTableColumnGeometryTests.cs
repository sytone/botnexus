using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Geometry guard for the conversation cost table's column allocation (#2898).
/// </summary>
/// <remarks>
/// <para>
/// This is a geometry test rather than a DOM-presence test for the same reason
/// <see cref="ActivityTableColumnGeometryTests"/> is: the bUnit tests assert every cell renders,
/// and they passed against a genuinely broken page. The cost table reuses <c>.activity-table</c>
/// for its type treatment, but that class's column rules are <em>positional</em>
/// (<c>nth-child</c>) and tuned for the overview's FIVE columns. Applied unchanged to the cost
/// table's SIX they handed 50% to Agent, collapsed Conversation to a few characters, and left
/// column 6 with no declared width at all - which under <c>table-layout: fixed</c> clipped the
/// Total cost column off the visible table entirely. Only a resolved-width assertion catches that.
/// </para>
/// <para>
/// The geometry is read from the shipped stylesheet, so a regression in the real CSS reddens this
/// test rather than a regression in a copy of it.
/// </para>
/// </remarks>
public sealed class ActivityCostTableColumnGeometryTests
{
    private static readonly string s_cssPath = Path.Combine(
        Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!,
        "wwwroot",
        "css",
        "app.css");

    /// <summary>Width the table is resolved against. Assertions are relative; this only makes the numbers readable.</summary>
    private const double TableWidthPx = 1200d;

    /// <summary>Number of columns the cost table renders: agent, conversation, sessions, messages, compactions, total.</summary>
    private const int CostColumnCount = 6;

    private static string ReadCssWithoutComments()
    {
        var css = File.ReadAllText(s_cssPath);
        var sb = new StringBuilder(css.Length);
        for (var i = 0; i < css.Length; i++)
        {
            if (i + 1 < css.Length && css[i] == '/' && css[i + 1] == '*')
            {
                var end = css.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0)
                    break;
                i = end + 1;
                continue;
            }
            sb.Append(css[i]);
        }
        return sb.ToString();
    }

    private static double ReadCostColumnPercent(string css, int columnIndex)
    {
        var pattern =
            @"\.activity-cost\s+\.activity-table\s+td:nth-child\(" + columnIndex + @"\)\s*\{[^}]*?width:\s*([^;}]+)";
        var match = Regex.Match(css, pattern, RegexOptions.Singleline);
        Assert.True(
            match.Success,
            $"#2898: no width declared for .activity-cost .activity-table td:nth-child({columnIndex}). " +
            "Under table-layout: fixed an undeclared column is clipped off the table.");

        var value = match.Groups[1].Value.Trim();
        var percent = Regex.Match(value, @"^([\d.]+)%$");
        Assert.True(percent.Success, $"Column {columnIndex} declares '{value}', which this resolver models only as a percentage.");
        return double.Parse(percent.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Every one of the six columns declares a width, so none is clipped off the table. This is the
    /// clause the original defect violated: the sixth column inherited nothing at all.
    /// </summary>
    [Fact]
    public void Every_cost_column_declares_a_width()
    {
        var css = ReadCssWithoutComments();

        for (var i = 1; i <= CostColumnCount; i++)
        {
            var pct = ReadCostColumnPercent(css, i);
            Assert.True(pct > 0, $"#2898: column {i} resolved to {pct}% - it would render invisible.");
        }
    }

    /// <summary>
    /// The declared widths sum to 100%, so no column overflows the container and none is left with
    /// an unallocated remainder to fight over.
    /// </summary>
    [Fact]
    public void Cost_column_widths_sum_to_the_full_table()
    {
        var css = ReadCssWithoutComments();

        var total = 0d;
        for (var i = 1; i <= CostColumnCount; i++)
            total += ReadCostColumnPercent(css, i);

        Assert.True(
            Math.Abs(total - 100d) < 0.01,
            $"#2898: cost columns sum to {total}%, not 100% - a column is starved or the table overflows.");
    }

    /// <summary>
    /// The Conversation column is the widest, and comfortably readable. It carries the title plus
    /// an origin badge, so it must not lose its allocation to the fixed-width numeric columns - the
    /// exact collapse the shared five-column rules produced.
    /// </summary>
    [Fact]
    public void Conversation_column_is_the_widest_and_readable()
    {
        var css = ReadCssWithoutComments();

        var widths = new double[CostColumnCount + 1];
        for (var i = 1; i <= CostColumnCount; i++)
            widths[i] = TableWidthPx * ReadCostColumnPercent(css, i) / 100d;

        var conversation = widths[2];

        for (var i = 1; i <= CostColumnCount; i++)
        {
            if (i == 2) continue;
            Assert.True(
                conversation > widths[i],
                $"#2898: Conversation resolved to {conversation}px, not wider than column {i} at {widths[i]}px.");
        }

        Assert.True(
            conversation >= 300d,
            $"#2898: Conversation resolved to {conversation}px - too narrow to read a title plus a badge.");
    }

    /// <summary>
    /// The numeric columns are right-aligned with tabular figures. Comparing magnitudes down a
    /// column is the entire task this table exists for, and ragged left-aligned proportional digits
    /// defeat it.
    /// </summary>
    [Fact]
    public void Numeric_columns_are_right_aligned_and_tabular()
    {
        var css = ReadCssWithoutComments();

        var match = Regex.Match(
            css,
            @"\.activity-cost\s+\.activity-table\s+td:nth-child\(n\+3\)\s*\{([^}]*)\}",
            RegexOptions.Singleline);

        Assert.True(match.Success, "#2898: no alignment rule found for the cost table's numeric columns.");
        Assert.Contains("text-align: right", match.Groups[1].Value, StringComparison.Ordinal);
        Assert.Contains("tabular-nums", match.Groups[1].Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// The cost rules are SCOPED to <c>.activity-cost</c>, so adding them cannot change the
    /// overview table's geometry. Asserted by confirming the overview's own column-1 rule is still
    /// declared unscoped and still allocates the title column its 50%.
    /// </summary>
    [Fact]
    public void Cost_geometry_does_not_disturb_the_overview_table()
    {
        var css = ReadCssWithoutComments();

        var overview = Regex.Match(
            css,
            @"(?<!\.activity-cost\s)\.activity-table\s+td:nth-child\(1\)\s*\{[^}]*?width:\s*([^;}]+)",
            RegexOptions.Singleline);

        Assert.True(overview.Success, "The overview table's column-1 width rule has gone missing.");
        Assert.Equal("50%", overview.Groups[1].Value.Trim());
    }
}
