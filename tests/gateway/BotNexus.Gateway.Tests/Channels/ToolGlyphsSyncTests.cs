using BotNexus.Domain.Gateway.Models;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;

namespace BotNexus.Gateway.Tests.Channels;

/// <summary>
/// Locks the Blazor web client's per-tool emoji map (<see cref="ToolDescriptionFormatter"/>) in sync
/// with the canonical cross-channel map (<see cref="ToolGlyphs"/>).
/// </summary>
/// <remarks>
/// The Blazor WebAssembly client is intentionally decoupled from <c>BotNexus.Domain</c> (the domain
/// assembly must not ship in the WASM payload), so it keeps its own copy of the glyph map rather than
/// referencing <see cref="ToolGlyphs"/> directly. This test -- which lives in a server-side assembly
/// that can see BOTH -- asserts the two never drift. If it fails, a glyph was changed in one place but
/// not the other: update <c>ToolDescriptionFormatter.GetEmoji</c> or <c>ToolGlyphs.ForTool</c> to match.
/// </remarks>
public sealed class ToolGlyphsSyncTests
{
    // Every tool name that ToolGlyphs maps explicitly (plus a couple of unknowns for the fallback).
    private static readonly string[] AllToolNames =
    [
        "read", "write", "edit", "shell", "exec", "web_search", "web_fetch", "grep", "glob", "ls",
        "memory_save", "memory_search", "memory_get", "cron", "canvas", "ask_user", "spawn_subagent",
        "list_subagents", "manage_subagent", "conversation", "sessions", "skill_manage", "skills",
        "delay", "get_datetime", "agent_converse", "watch_file", "create_agent", "update_agent",
        "list_agents",
        // unknown / dynamically-registered -> both must fall back to the wrench
        "some_unknown_tool", "mcp__server__tool",
    ];

    [Theory]
    [MemberData(nameof(ToolNames))]
    public void BlazorEmoji_MatchesCanonicalToolGlyph(string toolName)
    {
        var canonical = ToolGlyphs.ForTool(toolName);

        // The Blazor formatter returns "{emoji} {detail}"; the emoji is the leading token before the
        // first space. Extract it and compare to the canonical glyph.
        var formatted = ToolDescriptionFormatter.FormatDescription(toolName, null);
        var blazorEmoji = formatted.Split(' ', 2)[0];

        blazorEmoji.ShouldBe(canonical, $"tool '{toolName}': Blazor emoji '{blazorEmoji}' != ToolGlyphs '{canonical}'");
    }

    public static IEnumerable<object[]> ToolNames()
        => AllToolNames.Select(n => new object[] { n });
}
