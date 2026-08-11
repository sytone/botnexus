using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Geometry guard for the Activity table's column allocation (#2788).
///
/// <para><b>Why this test exists and why it is not a DOM-presence test.</b> The bUnit
/// component tests assert the conversation title string is rendered, and it always was:
/// the defect was purely one of resolved column width. Every row displayed a single
/// clipped character because <c>.activity-cell-title { max-width: 0 }</c> combined with
/// <c>width: auto</c> on column 1 under <c>table-layout: fixed</c> resolved the first
/// column to approximately zero. A test that reads the DOM passes against that broken
/// page, so it cannot be the guard.</para>
///
/// <para><b>What is asserted.</b> This test resolves the declared column geometry using
/// the CSS fixed-table-layout rule - under <c>table-layout: fixed</c> the first row's
/// declared widths determine the columns outright, and a <c>max-width</c> on the cell
/// constrains the column, not merely the flex line inside it. The resolution is computed
/// from the shipped stylesheet, so a regression in the stylesheet reddens the test rather
/// than a regression in a copy of it. A real-browser measurement of the same geometry
/// lives in <c>BotNexus.E2E.PortalDesktop.Tests.ActivityTableLayoutTests</c>; that suite
/// is quarantined out of the core validation gate, which is why the invariant is also
/// enforced here where it always runs.</para>
/// </summary>
public sealed class ActivityTableColumnGeometryTests
{
    private static readonly string s_cssPath = Path.Combine(
        Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!,
        "wwwroot",
        "css",
        "app.css");

    /// <summary>The width the table is resolved against. Any positive value works; the
    /// assertions are all relative, so the constant only makes the numbers readable.</summary>
    private const double TableWidthPx = 1200d;

    /// <summary>
    /// Read the stylesheet with <c>/* ... */</c> comments removed.
    /// </summary>
    /// <remarks>
    /// The rule comments in this area of app.css deliberately quote the selectors and
    /// declarations they are explaining ("do not reintroduce <c>max-width</c> here"), so a
    /// naive selector regex matches the prose before it reaches the rule. Parsing the
    /// comment-stripped text is the only way this guard reads the CSS the browser sees.
    /// </remarks>
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
                {
                    break;
                }

                i = end + 1;
                continue;
            }

            sb.Append(css[i]);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Resolve the rendered width of an <c>.activity-table</c> column under
    /// <c>table-layout: fixed</c>, in pixels, from the shipped stylesheet.
    /// </summary>
    /// <remarks>
    /// Fixed layout resolves a column from the declared width of its first-row cell, then
    /// clamps it by any <c>max-width</c> declared on that cell - the clamp is what collapsed
    /// column 1 to zero. <c>auto</c> columns share whatever the declared columns leave over.
    /// </remarks>
    private static double ResolveColumnWidthPx(string css, int columnIndex)
    {
        var declared = new Dictionary<int, double?>();
        for (var i = 1; i <= 5; i++)
        {
            declared[i] = ReadColumnDeclaredWidthPercent(css, i);
        }

        var declaredTotal = 0d;
        var autoColumns = 0;
        foreach (var kv in declared)
        {
            if (kv.Value is { } pct)
            {
                declaredTotal += pct;
            }
            else
            {
                autoColumns++;
            }
        }

        var remaining = Math.Max(0d, 100d - declaredTotal);
        var percent = declared[columnIndex] ?? (autoColumns > 0 ? remaining / autoColumns : 0d);
        var width = TableWidthPx * percent / 100d;

        // The cell's own max-width clamps the resolved column under fixed layout.
        if (columnIndex == 1 && ReadCellMaxWidthPx(css, ".activity-cell-title") is { } cap)
        {
            width = Math.Min(width, cap);
        }

        return width;
    }

    private static double? ReadColumnDeclaredWidthPercent(string css, int columnIndex)
    {
        var pattern =
            @"\.activity-table\s+td:nth-child\(" + columnIndex + @"\)\s*\{[^}]*?width:\s*([^;}]+)";
        var match = Regex.Match(css, pattern, RegexOptions.Singleline);
        Assert.True(
            match.Success,
            $"No width declaration found for .activity-table td:nth-child({columnIndex}) in app.css.");

        var value = match.Groups[1].Value.Trim();
        if (string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var percent = Regex.Match(value, @"^([\d.]+)%$");
        Assert.True(
            percent.Success,
            $"Column {columnIndex} declares '{value}', which this resolver only models as a percentage or 'auto'.");
        return double.Parse(percent.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    private static double? ReadCellMaxWidthPx(string css, string selector)
    {
        var pattern = Regex.Escape(selector) + @"\s*\{([^}]*)\}";
        var match = Regex.Match(css, pattern, RegexOptions.Singleline);
        Assert.True(match.Success, $"Rule block for '{selector}' not found in app.css.");

        var maxWidth = Regex.Match(match.Groups[1].Value, @"max-width:\s*([^;}]+)");
        if (!maxWidth.Success)
        {
            return null;
        }

        var value = maxWidth.Groups[1].Value.Trim();
        if (string.Equals(value, "none", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (value == "0" || Regex.IsMatch(value, @"^0(px|%|rem|em)$"))
        {
            return 0d;
        }

        var px = Regex.Match(value, @"^([\d.]+)px$");
        if (px.Success)
        {
            return double.Parse(px.Groups[1].Value, CultureInfo.InvariantCulture);
        }

        var pct = Regex.Match(value, @"^([\d.]+)%$");
        if (pct.Success)
        {
            return TableWidthPx * double.Parse(pct.Groups[1].Value, CultureInfo.InvariantCulture) / 100d;
        }

        return null;
    }

    /// <summary>
    /// Clause 1 and clause 6 of #2788: the Conversation column resolves to a readable,
    /// non-zero width and is wider than the fixed-width Status signal column.
    /// </summary>
    /// <remarks>
    /// Non-vacuity (clause 7): restoring <c>max-width: 0</c> on <c>.activity-cell-title</c>
    /// clamps the resolved width to 0 and reddens this test by name.
    /// </remarks>
    [Fact]
    public void ConversationColumn_ResolvesWiderThanStatusColumn()
    {
        var css = ReadCssWithoutComments();

        var conversation = ResolveColumnWidthPx(css, 1);
        var status = ResolveColumnWidthPx(css, 3);

        Assert.True(
            conversation > 0d,
            $"#2788: the Conversation column resolved to {conversation}px - it collapses and clips the title.");
        Assert.True(
            conversation > status,
            $"#2788: the Conversation column resolved to {conversation}px, which is not wider than the " +
            $"Status column at {status}px. The title is the primary identifying content of the row.");
    }

    /// <summary>
    /// Clause 4 of #2788: the declared columns must not over-subscribe the table, or the
    /// table bursts its container on a desktop viewport.
    /// </summary>
    [Fact]
    public void DeclaredColumns_DoNotExceedTableWidth()
    {
        var css = ReadCssWithoutComments();

        var total = 0d;
        for (var i = 1; i <= 5; i++)
        {
            total += ResolveColumnWidthPx(css, i);
        }

        Assert.True(
            total <= TableWidthPx + 0.5d,
            $"#2788: declared columns resolve to {total}px against a {TableWidthPx}px table.");
    }

    /// <summary>
    /// Sad path / root-cause guard for #2788. The shrink constraint belongs on the flex
    /// CHILD (<c>.activity-conversation-title { min-width: 0 }</c>), never as a
    /// <c>max-width</c> on the cell: under <c>table-layout: fixed</c> a cell max-width
    /// constrains the whole column.
    /// </summary>
    [Fact]
    public void TitleCell_DeclaresNoMaxWidth_SoTheColumnIsNotClamped()
    {
        var css = ReadCssWithoutComments();

        Assert.Null(ReadCellMaxWidthPx(css, ".activity-cell-title"));
    }

    /// <summary>
    /// Clause 2 and clause 3 of #2788: the #2528 one-line ellipsis guarantee and the #2496
    /// origin badge must both survive the fix. The title remains the only shrinkable part
    /// of the flex line, and it shrinks via <c>min-width: 0</c> on the child.
    /// </summary>
    [Fact]
    public void TitleShrinksViaChildMinWidth_AndStillEllipsisesOnOneLine()
    {
        var css = ReadCssWithoutComments();

        var rule = Regex.Match(
            css,
            @"\.activity-conversation-title\s*\{([^}]*)\}",
            RegexOptions.Singleline);
        Assert.True(rule.Success, ".activity-conversation-title rule not found in app.css.");

        var block = rule.Groups[1].Value;
        Assert.Matches(@"min-width:\s*0", block);
        Assert.Matches(@"overflow:\s*hidden", block);
        Assert.Matches(@"text-overflow:\s*ellipsis", block);
        Assert.Matches(@"white-space:\s*nowrap", block);
    }
}
