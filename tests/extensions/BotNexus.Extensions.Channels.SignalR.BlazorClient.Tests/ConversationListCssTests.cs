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
}
