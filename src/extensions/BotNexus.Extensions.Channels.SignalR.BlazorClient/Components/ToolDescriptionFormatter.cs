using System.Text.Json;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;

/// <summary>
/// Generates short, human-readable descriptions for tool calls displayed in the chat panel.
/// Each description includes an emoji prefix and a context-aware summary extracted from tool arguments.
/// </summary>
public static class ToolDescriptionFormatter
{
    private const int MaxPreviewLength = 50;

    /// <summary>
    /// Build a short description like "📄 Read Agent.cs" from the tool name and its JSON arguments.
    /// Falls back to "🔧 toolName" for unknown tools or when arguments can't be parsed.
    /// </summary>
    public static string FormatDescription(string toolName, string? toolArgs)
    {
        var emoji = GetEmoji(toolName);
        JsonElement? args = null;

        if (!string.IsNullOrEmpty(toolArgs))
        {
            try
            {
                args = JsonDocument.Parse(toolArgs).RootElement;
            }
            catch (JsonException)
            {
                // Malformed JSON — fall through to default
            }
        }

        var detail = toolName switch
        {
            "read" => FormatFileOp("Read", args),
            "write" => FormatFileOp("Write", args),
            "edit" => FormatFileOp("Edit", args),
            "shell" => FormatShell(args),
            "exec" => FormatExec(args),
            "web_search" => FormatStringArg("", args, "query"),
            "web_fetch" => FormatUrl(args),
            "grep" => FormatStringArg("Grep", args, "pattern"),
            "glob" => FormatStringArg("Glob", args, "pattern"),
            "ls" => FormatStringArg("List", args, "path"),
            "memory_save" => "Save memory",
            "memory_search" => FormatStringArg("Search memory:", args, "query"),
            "memory_get" => "Get memory",
            "cron" => FormatStringArg("Cron", args, "action"),
            "canvas" => FormatStringArg("Canvas", args, "action"),
            "ask_user" => "Ask user",
            "spawn_subagent" => FormatSpawnSubagent(args),
            "list_subagents" => "List sub-agents",
            "manage_subagent" => "Manage sub-agent",
            "conversation" => FormatStringArg("Conversation", args, "action"),
            "sessions" => FormatStringArg("Sessions", args, "action"),
            "skill_manage" => FormatSkillManage(args),
            "skills" => FormatSkills(args),
            "delay" => FormatDelay(args),
            "get_datetime" => "Get datetime",
            "agent_converse" => FormatStringArg("Talk to", args, "agentId"),
            "watch_file" => FormatStringArg("Watch", args, "path"),
            "create_agent" => FormatStringArg("Create agent", args, "id"),
            "update_agent" => FormatStringArg("Update agent", args, "id"),
            "list_agents" => "List agents",
            _ => toolName,
        };

        return $"{emoji} {detail}";
    }

    private static string GetEmoji(string toolName) => toolName switch
    {
        "read" => "📄",
        "write" or "edit" => "✏️",
        "shell" or "exec" => "💻",
        "web_search" => "🔍",
        "web_fetch" => "🌐",
        "grep" or "glob" => "🔎",
        "ls" => "📂",
        "memory_save" => "💾",
        "memory_search" => "🧠",
        "memory_get" => "🧠",
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
        _ => "🔧",
    };

    private static string FormatFileOp(string verb, JsonElement? args)
    {
        var path = GetString(args, "path");
        if (path is null) return verb.ToLowerInvariant();
        var fileName = Path.GetFileName(path);
        return string.IsNullOrEmpty(fileName) ? $"{verb} {Truncate(path)}" : $"{verb} {fileName}";
    }

    private static string FormatShell(JsonElement? args)
    {
        var cmd = GetString(args, "command");
        return cmd is null ? "shell" : Truncate(cmd);
    }

    private static string FormatExec(JsonElement? args)
    {
        if (args is null) return "exec";

        if (args.Value.TryGetProperty("command", out var cmdProp))
        {
            if (cmdProp.ValueKind == JsonValueKind.Array)
            {
                var parts = new List<string>();
                foreach (var element in cmdProp.EnumerateArray())
                {
                    if (element.ValueKind == JsonValueKind.String)
                        parts.Add(element.GetString()!);
                }
                return parts.Count > 0 ? Truncate(string.Join(' ', parts)) : "exec";
            }

            if (cmdProp.ValueKind == JsonValueKind.String)
            {
                return Truncate(cmdProp.GetString()!);
            }
        }

        return "exec";
    }

    private static string FormatUrl(JsonElement? args)
    {
        var url = GetString(args, "url");
        if (url is null) return "web_fetch";

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var display = uri.Host + uri.PathAndQuery.TrimEnd('/');
            return Truncate(display);
        }

        return Truncate(url);
    }

    private static string FormatSpawnSubagent(JsonElement? args)
    {
        var name = GetString(args, "name");
        return name is not null ? $"Spawn {name}" : "Spawn sub-agent";
    }

    private static string FormatSkillManage(JsonElement? args)
    {
        var action = GetString(args, "action");
        var name = GetString(args, "name");
        if (action is null) return "skill_manage";
        return name is not null ? $"Skill {action} {name}" : $"Skill {action}";
    }

    private static string FormatSkills(JsonElement? args)
    {
        var action = GetString(args, "action");
        var skillName = GetString(args, "skillName");
        if (action == "load" && skillName is not null)
            return $"Load skill {skillName}";
        if (action is not null)
            return $"Skills {action}";
        return "skills";
    }

    private static string FormatDelay(JsonElement? args)
    {
        if (args is null) return "Delay";
        if (args.Value.TryGetProperty("seconds", out var sec) && sec.ValueKind == JsonValueKind.Number)
            return $"Delay {sec.GetInt32()}s";
        return "Delay";
    }

    private static string FormatStringArg(string prefix, JsonElement? args, string propertyName)
    {
        var value = GetString(args, propertyName);
        if (value is null) return string.IsNullOrEmpty(prefix) ? "tool" : prefix.ToLowerInvariant();
        return string.IsNullOrEmpty(prefix) ? Truncate(value) : $"{prefix} {Truncate(value)}";
    }

    private static string? GetString(JsonElement? args, string propertyName)
    {
        if (args is null) return null;
        if (args.Value.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
            return prop.GetString();
        return null;
    }

    private static string Truncate(string value)
    {
        if (value.Length <= MaxPreviewLength) return value;
        return value[..MaxPreviewLength] + "…";
    }
}
