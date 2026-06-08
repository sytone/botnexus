namespace BotNexus.Extensions.DebugTool;

/// <summary>
/// Configuration for the platform debug tool.
/// </summary>
public sealed class DebugToolConfig
{
    /// <summary>Whether the debug tool is enabled. Defaults to true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Whether the raw_sql action is permitted. Defaults to false for safety.</summary>
    public bool AllowRawSql { get; set; }
}
