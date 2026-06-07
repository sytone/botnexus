using System.IO;
using System.Reflection;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Content-level tests verifying interrupt-steer button CSS rules.
/// Closes #951.
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
    public void InterruptSteerBtn_HasConsistentFontSize()
    {
        var content = File.ReadAllText(s_cssPath);

        var ruleStart = content.IndexOf(".interrupt-steer-btn {", StringComparison.Ordinal);
        var ruleEnd = content.IndexOf('}', ruleStart);
        var ruleBlock = content.Substring(ruleStart, ruleEnd - ruleStart + 1);

        // Must match steer-btn and abort-btn font-size of 0.85rem
        Assert.Contains("font-size: 0.85rem", ruleBlock, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InterruptSteerBtn_HasWhiteSpaceNoWrap()
    {
        var content = File.ReadAllText(s_cssPath);

        var ruleStart = content.IndexOf(".interrupt-steer-btn {", StringComparison.Ordinal);
        var ruleEnd = content.IndexOf('}', ruleStart);
        var ruleBlock = content.Substring(ruleStart, ruleEnd - ruleStart + 1);

        Assert.Contains("white-space: nowrap", ruleBlock, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InterruptSteerBtn_HasDisabledState()
    {
        var content = File.ReadAllText(s_cssPath);

        Assert.Contains(".interrupt-steer-btn:disabled", content, StringComparison.Ordinal);
    }
}
