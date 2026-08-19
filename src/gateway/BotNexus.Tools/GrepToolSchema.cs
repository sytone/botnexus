using BotNexus.Agent.Core.Tools.Generated;

namespace BotNexus.Tools;

/// <summary>
/// The single declaration of <c>grep</c>'s argument surface (#3320).
/// </summary>
/// <remarks>
/// <para>
/// Everything the tool knows about its parameters lives here exactly once. The JSON schema sent to
/// the model and the prepare-stage copy rules are both GENERATED from these attributes, so the
/// #2641 failure - a parameter that reaches the schema but never the copy list, silently dropping
/// the caller's value in favour of the default - has no way to occur: there is no second list.
/// </para>
/// <para>
/// Declaration order is significant. It fixes schema property order, and an alias may only target a
/// key already declared above it (<c>BNTS003</c>).
/// </para>
/// </remarks>
[ToolSchema]
[ToolParameter("pattern", "string", Description = "Search pattern (supports regex)", Required = true)]
[ToolParameter("path", "string", Description = "Directory or file to search (default: working directory)")]
[ToolParameter("glob", "string", Description = "Glob pattern to include files (e.g., *.cs, *.ts)")]
[ToolParameter("ignore_case", "boolean", Description = "Perform case-insensitive matching (default: false)")]
[ToolParameter("ignoreCase", "boolean", Description = "Case-insensitive matching alias.", AliasOf = "ignore_case")]
[ToolParameter("literal", "boolean", Description = "Treat pattern as literal string (default: false)")]
[ToolParameter("context", "integer", Description = "Number of lines to show before and after each match (default: 0)")]
[ToolParameter("limit", "integer", Description = "Maximum results to return (default: 100)")]

// Undocumented-but-tolerated caller spellings. They are accepted by the prepare stage and NOT
// advertised to the model: removing them would break callers that already send them, and
// advertising them would change the model-visible schema, which #3320 puts out of scope.
[ToolParameter("include", "string", AliasOf = "glob", HiddenFromSchema = true)]
[ToolParameter("max_results", "integer", AliasOf = "limit", HiddenFromSchema = true)]
internal static partial class GrepToolSchema
{
}
