using BotNexus.Agent.Core.Types;

namespace BotNexus.Agent.Core.Loop;

/// <summary>
/// Applies a host-owned text sanitizer to a finalized tool result before budgeting or retention.
/// </summary>
/// <remarks>
/// Text blocks are joined for one sanitizer pass so a credential split across adjacent blocks is
/// still recognized. Images and provider/tool metadata are opaque and retain their existing shape.
/// This boundary cannot reconstruct bytes a tool already omitted, paginated, or truncated itself.
/// </remarks>
public static class ToolResultSanitizer
{
    /// <summary>Sanitizes all text content while preserving non-text blocks and result metadata.</summary>
    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(result))]
    public static AgentToolResult? Apply(AgentToolResult? result, Func<string, string>? sanitizeText)
    {
        if (result is null || sanitizeText is null || result.Content.Count == 0)
        {
            return result;
        }

        var content = new List<AgentToolContent>(result.Content.Count);
        var changed = false;

        for (var i = 0; i < result.Content.Count;)
        {
            var block = result.Content[i];
            if (block.Type != AgentToolContentType.Text)
            {
                content.Add(block);
                i++;
                continue;
            }

            var adjacentText = new System.Text.StringBuilder();
            while (i < result.Content.Count && result.Content[i].Type == AgentToolContentType.Text)
            {
                adjacentText.Append(result.Content[i].Value);
                i++;
            }

            var original = adjacentText.ToString();
            var sanitized = sanitizeText(original);
            changed |= !string.Equals(original, sanitized, StringComparison.Ordinal);
            content.Add(new AgentToolContent(AgentToolContentType.Text, sanitized));
        }

        return changed ? new AgentToolResult(content, result.Details) : result;
    }
}
