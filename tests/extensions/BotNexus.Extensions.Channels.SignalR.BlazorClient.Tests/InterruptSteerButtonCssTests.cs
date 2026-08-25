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

        // The point is that the three related buttons agree, not what they agree on: the
        // design system replaced the former 0.85rem literal with a shared type-role token, and
        // pinning the literal here would redden on every future rename of a value that is
        // deliberately defined in one place.
        var fontSize = FontSizeOf(content, ".interrupt-steer-btn {");
        Assert.False(string.IsNullOrWhiteSpace(fontSize), ".interrupt-steer-btn declares no font-size");
        Assert.Equal(FontSizeOf(content, ".steer-btn {"), fontSize);
        Assert.Equal(FontSizeOf(content, ".abort-btn {"), fontSize);
    }

    /// <summary>Returns the font-size declared by the first rule with this selector, or null.</summary>
    private static string? FontSizeOf(string css, string selector)
    {
        var start = css.IndexOf(selector, StringComparison.Ordinal);
        if (start < 0)
            return null;

        var end = css.IndexOf('}', start);
        foreach (var line in css[start..end].Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("font-size:", StringComparison.OrdinalIgnoreCase))
                return trimmed.TrimEnd(';').Trim();
        }

        return null;
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
