using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Extensions.Skills;

/// <summary>
/// Lightweight, explicitly-named agent tool that delegates to a shared <see cref="SkillTool"/>
/// with a fixed action. This gives models distinct, self-describing tool names
/// (<c>skills_list</c>, <c>skill_view</c>) for better ergonomics, while the multi-action
/// <c>skills</c> tool remains the single implementation and the backward-compatible surface.
/// </summary>
/// <remarks>
/// The alias injects its fixed <see cref="_action"/> into the argument dictionary before
/// delegating to the inner tool, so both surfaces share the same discovery logic and
/// per-session loaded-skill state. Callers never pass an <c>action</c> argument to an alias.
/// </remarks>
public sealed class SkillAliasTool : IAgentTool
{
    private readonly SkillTool _inner;
    private readonly string _action;

    private SkillAliasTool(SkillTool inner, string action, string name, string label, JsonElement parameters)
    {
        _inner = inner;
        _action = action;
        Name = name;
        Label = label;
        Definition = new Tool(name, DescriptionFor(action), parameters);
    }

    /// <summary>Creates the explicit <c>skills_list</c> alias delegating to <c>skills</c> action=list.</summary>
    public static SkillAliasTool CreateListAlias(SkillTool inner)
        => new(
            inner,
            action: "list",
            name: "skills_list",
            label: "List Skills",
            parameters: ListParameters());

    /// <summary>Creates the explicit <c>skill_view</c> alias delegating to <c>skills</c> action=view_file.</summary>
    public static SkillAliasTool CreateViewAlias(SkillTool inner)
        => new(
            inner,
            action: "view_file",
            name: "skill_view",
            label: "View Skill File",
            parameters: ViewParameters());

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public string Label { get; }

    /// <inheritdoc />
    public Tool Definition { get; }

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(arguments);
    }

    /// <inheritdoc />
    public Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
    {
        // Force the fixed action; a caller-supplied 'action' on an alias is ignored so the
        // alias name is the sole source of truth for what it does.
        var merged = new Dictionary<string, object?>(arguments, StringComparer.OrdinalIgnoreCase)
        {
            ["action"] = _action
        };

        return _inner.ExecuteAsync(toolCallId, merged, cancellationToken, onUpdate);
    }

    private static string DescriptionFor(string action) => action switch
    {
        "list" => "List available skills and their descriptions. Equivalent to the `skills` tool with action `list`; use this to discover which skills exist before loading one.",
        "view_file" => "View a single linked support file (under references/, templates/, scripts/, or assets/) from a skill without loading the whole skill. Equivalent to the `skills` tool with action `view_file`.",
        _ => "Skill helper tool."
    };

    private static JsonElement ListParameters() => JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {},
          "required": []
        }
        """).RootElement.Clone();

    private static JsonElement ViewParameters() => JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "skillName": {
              "type": "string",
              "description": "Name of the skill to view a linked file from."
            },
            "filePath": {
              "type": "string",
              "description": "Relative path (within the skill directory) of the linked support file to view. Must live under references/, templates/, scripts/, or assets/."
            }
          },
          "required": ["skillName", "filePath"]
        }
        """).RootElement.Clone();
}
