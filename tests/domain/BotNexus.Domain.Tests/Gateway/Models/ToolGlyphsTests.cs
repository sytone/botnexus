using BotNexus.Domain.Gateway.Models;

namespace BotNexus.Domain.Tests.Gateway.Models;

/// <summary>
/// Tests for <see cref="ToolGlyphs"/>, the canonical cross-channel tool-icon map. These lock the
/// glyph contract that Telegram, the TUI, and (via the mirror in the Blazor formatter) the web
/// client all render, so that the same tool shows the same icon everywhere.
/// </summary>
public sealed class ToolGlyphsTests
{
    [Theory]
    [InlineData("read", "📄")]
    [InlineData("write", "✏️")]
    [InlineData("edit", "✏️")]
    [InlineData("shell", "💻")]
    [InlineData("exec", "💻")]
    [InlineData("web_search", "🔍")]
    [InlineData("web_fetch", "🌐")]
    [InlineData("grep", "🔎")]
    [InlineData("glob", "🔎")]
    [InlineData("ls", "📂")]
    [InlineData("memory_save", "💾")]
    [InlineData("memory_search", "🧠")]
    [InlineData("memory_get", "🧠")]
    [InlineData("cron", "⏰")]
    [InlineData("canvas", "🎨")]
    [InlineData("ask_user", "❓")]
    [InlineData("spawn_subagent", "🤖")]
    [InlineData("conversation", "💬")]
    [InlineData("sessions", "📋")]
    [InlineData("skill_manage", "🛠️")]
    [InlineData("skills", "📚")]
    [InlineData("delay", "⏳")]
    [InlineData("get_datetime", "🕐")]
    [InlineData("agent_converse", "🗣️")]
    [InlineData("watch_file", "👁️")]
    public void ForTool_ReturnsCanonicalGlyph_ForKnownTool(string toolName, string expected)
    {
        ToolGlyphs.ForTool(toolName).ShouldBe(expected);
    }

    [Theory]
    [InlineData("some_unknown_tool")]
    [InlineData("mcp__server__do_thing")]
    [InlineData("")]
    [InlineData(null)]
    public void ForTool_FallsBackToDefault_ForUnknownOrEmpty(string? toolName)
    {
        ToolGlyphs.ForTool(toolName).ShouldBe(ToolGlyphs.Default);
    }

    [Fact]
    public void Default_IsTheWrench()
    {
        ToolGlyphs.Default.ShouldBe("🔧");
    }

    [Fact]
    public void ForTool_NeverReturnsNullOrEmpty()
    {
        // Whatever comes in, a glyph always comes out (callers string-interpolate it directly).
        ToolGlyphs.ForTool("read").ShouldNotBeNullOrEmpty();
        ToolGlyphs.ForTool("definitely-not-a-tool").ShouldNotBeNullOrEmpty();
        ToolGlyphs.ForTool(null).ShouldNotBeNullOrEmpty();
    }
}
