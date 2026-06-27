namespace BotNexus.Domain.Gateway.Models;

/// <summary>
/// Canonical, cross-channel mapping from a tool name to the emoji glyph used to represent it
/// when surfacing tool execution to users.
/// </summary>
/// <remarks>
/// <para>
/// This is the single source of truth for tool icons across every server-side channel adapter
/// (Telegram, TUI, and any future adapter that references <c>BotNexus.Domain</c>). Before this
/// type existed each channel invented its own convention: the Blazor client had a rich per-tool
/// emoji map, the TUI hard-coded a single wrench, and Telegram showed no icon at all. Centralising
/// the mapping here keeps the user-facing representation of a given tool identical no matter which
/// channel a conversation runs on.
/// </para>
/// <para>
/// The Blazor WebAssembly client (<c>BotNexus.Extensions.Channels.SignalR.BlazorClient</c>) is
/// deliberately decoupled from <c>BotNexus.Domain</c> -- a browser client must not drag the domain
/// assembly into the WASM payload -- so it keeps its own copy of this map. A unit test asserts the
/// two maps stay in lockstep so the convention cannot silently drift. If you add or change a glyph
/// here, update the Blazor <c>ToolDescriptionFormatter</c> to match (the test will fail otherwise).
/// </para>
/// </remarks>
public static class ToolGlyphs
{
    /// <summary>
    /// The fallback glyph used for any tool not explicitly mapped (including dynamically-registered
    /// tools and MCP tools whose names are not known at compile time). A wrench reads as "a tool ran"
    /// without implying a specific capability.
    /// </summary>
    public const string Default = "🔧";

    /// <summary>
    /// Returns the canonical emoji glyph for <paramref name="toolName"/>, or <see cref="Default"/>
    /// when the tool is unknown or the name is null/empty. Matching is case-sensitive against the
    /// registered tool ids (which are themselves lower-case by convention).
    /// </summary>
    public static string ForTool(string? toolName) => toolName switch
    {
        "read" => "📄",
        "write" or "edit" => "✏️",
        "shell" or "exec" => "💻",
        "web_search" => "🔍",
        "web_fetch" => "🌐",
        "grep" or "glob" => "🔎",
        "ls" => "📂",
        "memory_save" => "💾",
        "memory_search" or "memory_get" => "🧠",
        "cron" => "⏰",
        "canvas" => "🎨",
        "ask_user" => "❓",
        "spawn_subagent" or "list_subagents" or "manage_subagent" => "🤖",
        "conversation" => "💬",
        "sessions" => "📋",
        "skill_manage" => "🛠️",
        "skills" => "📚",
        "delay" => "⏳",
        "get_datetime" => "🕐",
        "agent_converse" => "🗣️",
        "watch_file" => "👁️",
        "create_agent" or "update_agent" or "list_agents" => "🤖",
        _ => Default,
    };
}
