using System.IO;
using System.Reflection;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Content-level tests verifying the redirect control keeps its warning intent while sharing the
/// compact composer action sizing and state rules.
/// </summary>
public sealed class InterruptSteerButtonCssTests
{
    private static readonly string s_cssPath = Path.Combine(
        Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!,
        "wwwroot",
        "css",
        "app.css");

    [Fact]
    public void InterruptSteerBtn_HasCssRule()
    {
        var content = File.ReadAllText(s_cssPath);

        var ruleStart = content.IndexOf(".interrupt-steer-btn {", StringComparison.Ordinal);
        Assert.True(ruleStart >= 0, ".interrupt-steer-btn CSS rule not found in app.css");
    }

    [Fact]
    public void ComposerActionBtn_HasCompactSquareSize()
    {
        var ruleBlock = RuleBlock(File.ReadAllText(s_cssPath), ".composer-action-btn {");

        Assert.Contains("width: 2.25rem", ruleBlock, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("height: 2.25rem", ruleBlock, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("padding: 0", ruleBlock, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InterruptSteerBtn_UsesWarningIntentColour()
    {
        var ruleBlock = RuleBlock(File.ReadAllText(s_cssPath), ".composer-action-btn.interrupt-steer-btn {");

        Assert.Contains("var(--warning)", ruleBlock, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("var(--color-warning-wash)", ruleBlock, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ComposerActionBtn_HasSharedDisabledState()
    {
        var content = File.ReadAllText(s_cssPath);

        Assert.Contains(".composer-action-btn:disabled", content, StringComparison.Ordinal);
    }

    private static string RuleBlock(string content, string selector)
    {
        var ruleStart = content.IndexOf(selector, StringComparison.Ordinal);
        Assert.True(ruleStart >= 0, $"{selector} CSS rule not found in app.css");
        var ruleEnd = content.IndexOf('}', ruleStart);
        return content.Substring(ruleStart, ruleEnd - ruleStart + 1);
    }
}
