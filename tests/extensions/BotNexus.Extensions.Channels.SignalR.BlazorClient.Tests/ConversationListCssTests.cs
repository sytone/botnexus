using System.IO;
using System.Reflection;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Content-level tests verifying conversation list item CSS rules are correct.
/// Closes #935.
/// </summary>
public sealed class ConversationListCssTests
{
    private static readonly string s_cssPath = Path.Combine(
        Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!,
        "wwwroot",
        "css",
        "app.css");

    [Fact]
    public void ConversationListItemBtn_HasNoUnderline()
    {
        // #935: <a> elements with class conversation-list-item-btn must not have
        // browser-default underline.  text-decoration:none is required.
        var content = File.ReadAllText(s_cssPath);

        // Find the rule block for .conversation-list-item-btn
        var ruleStart = content.IndexOf(".conversation-list-item-btn {", StringComparison.Ordinal);
        Assert.True(ruleStart >= 0, ".conversation-list-item-btn rule not found in app.css");

        // Extract to the closing brace
        var ruleEnd = content.IndexOf('}', ruleStart);
        Assert.True(ruleEnd > ruleStart, "Could not find closing brace of .conversation-list-item-btn");

        var ruleBlock = content.Substring(ruleStart, ruleEnd - ruleStart + 1);

        Assert.Contains("text-decoration: none", ruleBlock,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConversationMetadata_IsLeftAlignedAndActionsReserveTheTitleLine()
    {
        var content = File.ReadAllText(s_cssPath);

        var metadataRule = FindRuleContaining(content, ".conversation-list-item-meta {");
        metadataRule.ShouldContain("align-self: flex-start");
        metadataRule.ShouldNotContain("margin-left: auto");

        var titleReservationRule = FindRuleContaining(
            content,
            ".conversation-list-item:has(.conversation-row-actions) .conversation-list-item-main");
        titleReservationRule.ShouldContain("min-height: 28px");
        titleReservationRule.ShouldContain("padding-right: var(--conversation-row-actions-width)");

        var actionsRule = FindRuleContaining(content, ".conversation-row-actions {");
        actionsRule.ShouldContain("top: 0.6rem");
        actionsRule.ShouldNotContain("bottom: 0");
    }

    [Fact]
    public void ConversationRowActions_FocusWithinRevealsStripAndEveryButton()
    {
        var content = File.ReadAllText(s_cssPath);

        var backdropRule = FindRuleContaining(
            content,
            ".conversation-list-item:hover .conversation-row-actions");
        backdropRule.ShouldContain(
            ".conversation-row-actions:focus-within",
            customMessage: "Keyboard focus must reveal the action-strip backdrop.");

        var buttonRule = FindRuleContaining(
            content,
            ".conversation-list-item:hover .conversation-row-actions > button");
        buttonRule.ShouldContain(
            ".conversation-row-actions:focus-within > button",
            customMessage: "Keyboard focus must restore a visible non-zero box for every row action.");
        buttonRule.ShouldContain("width: var(--hit-pointer)");
        buttonRule.ShouldContain("min-width: var(--hit-pointer)");
        buttonRule.ShouldContain("opacity: 1");
    }

    private static string FindRuleContaining(string content, string selector)
    {
        var selectorStart = content.IndexOf(selector, StringComparison.Ordinal);
        selectorStart.ShouldBeGreaterThanOrEqualTo(0, $"Selector '{selector}' was not found in app.css.");

        var ruleStart = content.LastIndexOf('}', selectorStart) + 1;
        var ruleEnd = content.IndexOf('}', selectorStart);
        ruleEnd.ShouldBeGreaterThan(selectorStart, $"Selector '{selector}' has no closing brace.");

        return content[ruleStart..(ruleEnd + 1)];
    }
}
