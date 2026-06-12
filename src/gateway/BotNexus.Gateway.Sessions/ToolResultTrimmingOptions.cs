// ToolResultTrimmingOptions.cs
using System.Text.Json.Serialization;

namespace BotNexus.Gateway.Sessions;

/// <summary>
/// Configuration for the tool result trimming pipeline. Controls when and how
/// large tool results are replaced with compact tombstones to reclaim token budget.
/// </summary>
public sealed class ToolResultTrimmingOptions
{
    /// <summary>Section name in configuration.</summary>
    public const string SectionName = "toolResultTrimming";

    /// <summary>
    /// Whether tool result trimming is enabled. Default: true.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Minimum content length (chars) for a tool result to be a trimming candidate.
    /// Results shorter than this are never trimmed. Default: 500.
    /// </summary>
    public int MinContentLengthChars { get; set; } = 500;

    /// <summary>
    /// Number of turns after which a large tool result becomes eligible for trimming.
    /// A "turn" is one user→assistant exchange. Default: 3.
    /// </summary>
    public int AgeTurnsThreshold { get; set; } = 3;

    /// <summary>
    /// Maximum number of leading characters preserved in the tombstone preview.
    /// Default: 200.
    /// </summary>
    public int TombstonePreviewChars { get; set; } = 200;

    /// <summary>
    /// Per-tool overrides for <see cref="AgeTurnsThreshold"/>.
    /// Key is the tool name (case-insensitive), value is the turn threshold.
    /// </summary>
    public Dictionary<string, int> ToolThresholds { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["shell"] = 2,
        ["exec"] = 2,
        ["web_fetch"] = 2,
        ["read"] = 3,
        ["grep"] = 3,
        ["glob"] = 3,
        ["memory_search"] = 5
    };
}
