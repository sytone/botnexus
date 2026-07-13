using System.IO;
using System.Reflection;
using System.Text;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Structural-integrity guard for the portal stylesheet.
///
/// Regression coverage for the "settings modal does nothing / cog has a grey box"
/// class of bug: commit #438 introduced <c>.agent-card-last-activity { margin-left: auto;</c>
/// WITHOUT a closing brace. Because CSS supports <c>&amp;</c> nesting, every rule after that
/// point (banner-settings-btn, portal-settings-overlay, portal-settings-panel, ~700 lines)
/// was silently reparented as a nested descendant that never matched any element —
/// so the settings modal rendered unstyled below the fold and the cog fell back to the
/// user-agent default grey button background.
///
/// A single unbalanced brace anywhere in app.css can invisibly disable an arbitrary
/// tail of the stylesheet, so we assert global brace balance rather than a single rule.
/// </summary>
public sealed class CssStructuralIntegrityTests
{
    private static readonly string s_cssPath = Path.Combine(
        Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!,
        "wwwroot",
        "css",
        "app.css");

    /// <summary>
    /// Strip <c>/* ... */</c> comments so braces inside comments don't skew the count.
    /// </summary>
    private static string StripComments(string css)
    {
        var sb = new StringBuilder(css.Length);
        for (int i = 0; i < css.Length; i++)
        {
            if (i + 1 < css.Length && css[i] == '/' && css[i + 1] == '*')
            {
                var end = css.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) break;
                i = end + 1;
                continue;
            }
            sb.Append(css[i]);
        }
        return sb.ToString();
    }

    [Fact]
    public void AppCss_HasBalancedBraces_NoUnclosedRules()
    {
        var css = StripComments(File.ReadAllText(s_cssPath));

        int depth = 0, minDepth = 0, firstNegativeLine = -1, line = 1;
        for (int i = 0; i < css.Length; i++)
        {
            char c = css[i];
            if (c == '\n') line++;
            else if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth < minDepth) { minDepth = depth; if (firstNegativeLine < 0) firstNegativeLine = line; }
            }
        }

        Assert.True(minDepth >= 0,
            $"app.css has an unmatched closing brace (depth went negative near line {firstNegativeLine}).");
        Assert.True(depth == 0,
            $"app.css has {depth} unclosed '{{' rule(s) — a missing closing brace silently nests every " +
            "subsequent rule and disables the tail of the stylesheet (see settings-modal regression).");
    }

    /// <summary>
    /// The settings modal and cog rules MUST live at the top level of the stylesheet.
    /// If an earlier rule loses its closing brace, these selectors get swallowed as
    /// nested descendants — this asserts they are reachable as standalone rules by
    /// checking they are NOT indented under a still-open block at their position.
    /// </summary>
    [Theory]
    [InlineData(".banner-settings-btn {")]
    [InlineData(".portal-settings-overlay")]
    [InlineData(".portal-settings-panel")]
    public void CriticalSelector_IsTopLevelRule(string selector)
    {
        var css = StripComments(File.ReadAllText(s_cssPath));
        var idx = css.IndexOf(selector, StringComparison.Ordinal);
        Assert.True(idx >= 0, $"Selector '{selector}' not found in app.css");

        // Compute brace depth at the point the selector appears. A top-level rule
        // must sit at depth 0 (not inside any open block).
        int depth = 0;
        for (int i = 0; i < idx; i++)
        {
            if (css[i] == '{') depth++;
            else if (css[i] == '}') depth--;
        }

        Assert.Equal(0, depth);
    }
}
