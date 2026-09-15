using System.Text.Json;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Utilities;

namespace BotNexus.Agent.Providers.Anthropic;

/// <summary>
/// Converts BotNexus messages to Anthropic API message format and handles
/// cache control, tool-name mapping, and tool-call ID normalization.
/// </summary>
internal static class AnthropicMessageConverter
{
    /// <summary>
    /// Known non-Anthropic thinking signature field names used by OpenAI/Copilot providers.
    /// These are stored as ThinkingSignature when sessions originate on those providers.
    /// They must NOT be sent to Anthropic as thinking block signatures.
    /// </summary>
    private static readonly HashSet<string> NonAnthropicSignatures = new(StringComparer.OrdinalIgnoreCase)
    {
        "reasoning_content",
        "reasoning",
        "reasoning_text"
    };

    private static readonly IReadOnlyDictionary<string, string> ClaudeCodeToolLookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Read"] = "Read",
        ["Write"] = "Write",
        ["Edit"] = "Edit",
        ["Bash"] = "Bash",
        ["Grep"] = "Grep",
        ["Glob"] = "Glob",
        ["AskUserQuestion"] = "AskUserQuestion",
        ["EnterPlanMode"] = "EnterPlanMode",
        ["ExitPlanMode"] = "ExitPlanMode",
        ["KillShell"] = "KillShell",
        ["NotebookEdit"] = "NotebookEdit",
        ["Skill"] = "Skill",
        ["Task"] = "Task",
        ["TaskOutput"] = "TaskOutput",
        ["TodoWrite"] = "TodoWrite",
        ["WebFetch"] = "WebFetch",
        ["WebSearch"] = "WebSearch"
    };

    internal static object NormalizeToolSchema(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>(),
                ["required"] = Array.Empty<string>()
            };
        }

        if (schema.TryGetProperty("type", out var typeElement) &&
            string.Equals(typeElement.GetString(), "object", StringComparison.OrdinalIgnoreCase))
        {
            return schema;
        }

        var properties = schema.TryGetProperty("properties", out var propertiesElement)
            ? JsonSerializer.Deserialize<object>(propertiesElement.GetRawText())
            : new Dictionary<string, object?>();
        var required = schema.TryGetProperty("required", out var requiredElement) &&
                       requiredElement.ValueKind == JsonValueKind.Array
            ? JsonSerializer.Deserialize<object>(requiredElement.GetRawText())
            : Array.Empty<string>();

        return new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required
        };
    }

    internal static List<Dictionary<string, object?>> ConvertMessages(
        IReadOnlyList<Message> messages, LlmModel model, bool isOAuthToken)
    {
        var transformed = MessageTransformer.TransformMessages(messages, model, NormalizeToolCallId);
        var result = new List<Dictionary<string, object?>>();
        var isLastToolResult = false;

        foreach (var msg in transformed)
        {
            switch (msg)
            {
                case UserMessage user:
                    isLastToolResult = false;
                    var userMessage = ConvertUserMessage(user, model);
                    if (userMessage is not null)
                        result.Add(userMessage);
                    break;

                case AssistantMessage assistant:
                    isLastToolResult = false;
                    var assistantMessage = ConvertAssistantMessage(assistant, isOAuthToken);
                    if (assistantMessage is not null)
                        result.Add(assistantMessage);
                    break;

                case ToolResultMessage toolResult:
                    var block = MakeToolResultBlock(toolResult);
                    if (isLastToolResult && result.Count > 0 &&
                        result[^1]["content"] is List<object> existingBlocks)
                    {
                        existingBlocks.Add(block);
                    }
                    else
                    {
                        result.Add(new Dictionary<string, object?>
                        {
                            ["role"] = "user",
                            ["content"] = new List<object> { block }
                        });
                        isLastToolResult = true;
                    }
                    break;
            }
        }

        return result;
    }

    private static Dictionary<string, object?>? ConvertUserMessage(UserMessage msg, LlmModel model)
    {
        object content;

        if (msg.Content.IsText)
        {
            if (string.IsNullOrWhiteSpace(msg.Content.Text))
                return null;

            content = msg.Content.Text.SanitizeSurrogates();
        }
        else
        {
            var blocks = new List<object>();
            var droppedImageCount = msg.Content.Blocks!.Count(b => b is ImageContent);
            var supportsImages = ImageModalityGuard.AllowImages(
                model,
                droppedImageCount,
                "anthropic.user");
            foreach (var block in msg.Content.Blocks!)
            {
                switch (block)
                {
                    case TextContent text:
                        if (string.IsNullOrWhiteSpace(text.Text))
                            break;

                        blocks.Add(new Dictionary<string, object?>
                        {
                            ["type"] = "text",
                            ["text"] = text.Text.SanitizeSurrogates()
                        });
                        break;
                    case ImageContent image:
                        if (!supportsImages)
                            break;
                        blocks.Add(new Dictionary<string, object?>
                        {
                            ["type"] = "image",
                            ["source"] = new Dictionary<string, object?>
                            {
                                ["type"] = "base64",
                                ["media_type"] = image.MimeType,
                                ["data"] = image.Data
                            }
                        });
                        break;
                }
            }

            // #2485 AC4: substitute an in-band notice for the removed images so the user learns
            // the attachment could not be delivered, rather than only an operator reading the log.
            if (!supportsImages)
            {
                var notice = ImageModalityGuard.BuildDropNotice(model, droppedImageCount);
                if (notice is not null)
                    blocks.Add(new Dictionary<string, object?> { ["type"] = "text", ["text"] = notice });
            }

            if (blocks.Count == 0)
                return null;

            content = blocks;
        }

        return new Dictionary<string, object?>
        {
            ["role"] = "user",
            ["content"] = content
        };
    }

    private static Dictionary<string, object?>? ConvertAssistantMessage(AssistantMessage msg, bool isOAuthToken)
    {
        var blocks = new List<object>();

        foreach (var block in msg.Content)
        {
            switch (block)
            {
                case TextContent text:
                    if (string.IsNullOrWhiteSpace(text.Text))
                        break;

                    var textBlock = new Dictionary<string, object?>
                    {
                        ["type"] = "text",
                        ["text"] = text.Text.SanitizeSurrogates()
                    };
                    if (text.TextSignature is not null)
                        textBlock["signature"] = text.TextSignature;
                    blocks.Add(textBlock);
                    break;

                case ThinkingContent thinking:
                    if (thinking.Redacted == true)
                    {
                        if (string.IsNullOrWhiteSpace(thinking.ThinkingSignature))
                            break;

                        // Drop redacted thinking blocks with non-Anthropic signatures.
                        // These originate from cross-provider session replay and would
                        // cause Anthropic to reject the request.
                        if (IsNonAnthropicSignature(thinking.ThinkingSignature))
                            break;

                        blocks.Add(new Dictionary<string, object?>
                        {
                            ["type"] = "redacted_thinking",
                            ["data"] = thinking.ThinkingSignature
                        });
                    }
                    else
                    {
                        if (string.IsNullOrWhiteSpace(thinking.Thinking))
                            break;

                        // Non-Anthropic signatures (e.g. "reasoning_content") or missing
                        // signatures cannot be sent as thinking blocks — convert to text.
                        if (string.IsNullOrWhiteSpace(thinking.ThinkingSignature) ||
                            IsNonAnthropicSignature(thinking.ThinkingSignature))
                        {
                            blocks.Add(new Dictionary<string, object?>
                            {
                                ["type"] = "text",
                                ["text"] = thinking.Thinking.SanitizeSurrogates()
                            });
                            break;
                        }

                        blocks.Add(new Dictionary<string, object?>
                        {
                            ["type"] = "thinking",
                            ["thinking"] = thinking.Thinking.SanitizeSurrogates(),
                            ["signature"] = thinking.ThinkingSignature
                        });
                    }
                    break;

                case ToolCallContent toolCall:
                    var toolUseBlock = new Dictionary<string, object?>
                    {
                        ["type"] = "tool_use",
                        ["id"] = toolCall.Id,
                        ["name"] = isOAuthToken ? ToClaudeCodeName(toolCall.Name) : toolCall.Name,
                        ["input"] = toolCall.Arguments
                    };
                    if (toolCall.ThoughtSignature is not null)
                        toolUseBlock["signature"] = toolCall.ThoughtSignature;
                    blocks.Add(toolUseBlock);
                    break;
            }
        }

        if (blocks.Count == 0)
            return null;

        return new Dictionary<string, object?>
        {
            ["role"] = "assistant",
            ["content"] = blocks
        };
    }

    private static Dictionary<string, object?> MakeToolResultBlock(ToolResultMessage toolResult)
    {
        object content;
        var textBlocks = toolResult.Content.OfType<TextContent>()
            .Select(t => t.Text.SanitizeSurrogates())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();
        var hasImages = toolResult.Content.Any(c => c is ImageContent);

        if (!hasImages && textBlocks.Count == 1)
        {
            content = textBlocks[0];
        }
        else
        {
            var blocks = new List<object>();
            if (textBlocks.Count > 0)
            {
                blocks.Add(new Dictionary<string, object?>
                {
                    ["type"] = "text",
                    ["text"] = string.Join("\n", textBlocks)
                });
            }
            else if (hasImages)
            {
                blocks.Add(new Dictionary<string, object?>
                {
                    ["type"] = "text",
                    ["text"] = "(see attached image)"
                });
            }

            foreach (var image in toolResult.Content.OfType<ImageContent>())
            {
                blocks.Add(new Dictionary<string, object?>
                {
                    ["type"] = "image",
                    ["source"] = new Dictionary<string, object?>
                    {
                        ["type"] = "base64",
                        ["media_type"] = image.MimeType,
                        ["data"] = image.Data
                    }
                });
            }

            content = blocks;
        }

        var result = new Dictionary<string, object?>
        {
            ["type"] = "tool_result",
            ["tool_use_id"] = toolResult.ToolCallId,
            ["content"] = content
        };

        if (toolResult.IsError)
        {
            result["is_error"] = true;
        }

        return result;
    }

    internal static Dictionary<string, object?>? BuildCacheControl(
        CacheRetention retention, string baseUrl)
    {
        if (retention == CacheRetention.None)
            return null;

        var cacheControl = new Dictionary<string, object?> { ["type"] = "ephemeral" };

        if (retention == CacheRetention.Long &&
            baseUrl.Contains("api.anthropic.com", StringComparison.OrdinalIgnoreCase))
        {
            cacheControl["ttl"] = "1h";
        }

        return cacheControl;
    }

    /// <summary>
    /// Applies a cache-control breakpoint to the last non-system message only.
    /// Retained for use by callers that want the legacy single-breakpoint behaviour.
    /// New callers should prefer <see cref="ApplyMultiBreakpointCacheControl"/>.
    /// </summary>
    internal static void ApplyLastUserMessageCacheControl(
        List<Dictionary<string, object?>> messages, CacheRetention retention, string baseUrl)
    {
        ApplyMultiBreakpointCacheControl(messages, retention, baseUrl, maxBreakpoints: 1);
    }

    /// <summary>
    /// Number of content blocks Anthropic will walk backwards from a breakpoint looking for a
    /// cache entry an earlier request wrote. A breakpoint further than this from the nearest
    /// existing entry finds nothing and reads no cache at all.
    /// </summary>
    internal const int LookbackBlocks = 20;

    /// <summary>
    /// Spacing between the stable anchor breakpoints, in content blocks. Deliberately under
    /// <see cref="LookbackBlocks"/> so that when conversation growth creates a new anchor, that
    /// anchor's own lookback still reaches the previous one and the chain holds.
    /// </summary>
    internal const int AnchorStrideBlocks = 16;

    /// <summary>
    /// Places up to <paramref name="maxBreakpoints"/> cache-control breakpoints across the
    /// converted message list.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The tail always gets one: it writes the entry the next request will read, and its own
    /// lookback normally reaches the previous request's tail entry, which is what keeps an
    /// ordinary turn-by-turn conversation fully cached.
    /// </para>
    /// <para>
    /// The remaining breakpoints used to go on the messages immediately before the tail, which
    /// looks like redundancy and is not. A cache read needs a breakpoint within
    /// <see cref="LookbackBlocks"/> blocks of a position some earlier request actually wrote, and
    /// three breakpoints bunched inside a single turn's new content are all equally far from the
    /// last one. One wide turn -- a parallel tool fan-out appending more than twenty blocks --
    /// therefore stranded every one of them at once and re-billed the whole conversation.
    /// </para>
    /// <para>
    /// So the spare breakpoints go on <em>anchors</em>: positions measured in blocks from the
    /// start of the conversation, which do not move as the conversation grows. An anchor chosen
    /// on one request is chosen again on the next and matches its own entry exactly, so even when
    /// the tail is stranded the prefix up to the anchor is still served from cache and the damage
    /// is bounded by the stride rather than by the length of the conversation.
    /// </para>
    /// <para>
    /// Below one stride there are no anchor positions to use and nothing to strand, so short
    /// conversations keep the original behaviour of stamping the last few messages.
    /// </para>
    /// </remarks>
    /// <param name="messages">The converted message list to annotate in-place.</param>
    /// <param name="retention">Cache retention mode. <see cref="CacheRetention.None"/> is a no-op.</param>
    /// <param name="baseUrl">Provider base URL, used for Long-TTL eligibility check.</param>
    /// <param name="maxBreakpoints">Maximum number of breakpoints to place (default 3).</param>
    internal static void ApplyMultiBreakpointCacheControl(
        List<Dictionary<string, object?>> messages,
        CacheRetention retention,
        string baseUrl,
        int maxBreakpoints = 3)
    {
        if (retention == CacheRetention.None || maxBreakpoints <= 0 || messages.Count == 0) return;

        var cacheControl = BuildCacheControl(retention, baseUrl);
        if (cacheControl is null) return;

        foreach (var index in SelectBreakpointIndices(messages, maxBreakpoints))
        {
            StampMessage(messages[index], cacheControl);
        }
    }

    /// <summary>
    /// Chooses which message indices carry a breakpoint, newest first.
    /// </summary>
    internal static IReadOnlyList<int> SelectBreakpointIndices(
        List<Dictionary<string, object?>> messages, int maxBreakpoints)
    {
        var lastIndex = messages.Count - 1;
        var blockEnds = CumulativeBlockEnds(messages);
        var totalBlocks = blockEnds[lastIndex];

        // Nothing to anchor to yet, and nothing far enough apart to strand: keep the original
        // behaviour so short conversations are untouched by this.
        if (totalBlocks <= AnchorStrideBlocks)
        {
            var tail = new List<int>();
            for (var i = lastIndex; i >= 0 && tail.Count < maxBreakpoints; i--)
                tail.Add(i);
            return tail;
        }

        var selected = new List<int> { lastIndex };

        // Anchors sit at fixed multiples of the stride measured from the start of the
        // conversation, so the same anchor is chosen again on the next request. Walk down from
        // the tail, taking the closest anchors first: they leave the least uncached.
        for (var anchorBlock = totalBlocks / AnchorStrideBlocks * AnchorStrideBlocks;
             anchorBlock > 0 && selected.Count < maxBreakpoints;
             anchorBlock -= AnchorStrideBlocks)
        {
            var index = FindMessageEndingAtOrAfter(blockEnds, anchorBlock);
            if (index >= 0 && index < lastIndex && !selected.Contains(index))
                selected.Add(index);
        }

        return selected;
    }

    /// <summary>
    /// Running total of content blocks at the end of each message. Entries for earlier messages
    /// never change as the conversation grows, which is what makes an anchor position stable.
    /// </summary>
    private static int[] CumulativeBlockEnds(List<Dictionary<string, object?>> messages)
    {
        var ends = new int[messages.Count];
        var running = 0;

        for (var i = 0; i < messages.Count; i++)
        {
            running += messages[i]["content"] switch
            {
                List<object> blocks => Math.Max(blocks.Count, 1),
                _ => 1
            };
            ends[i] = running;
        }

        return ends;
    }

    /// <summary>
    /// First message whose content ends at or after <paramref name="blockTarget"/>. A breakpoint
    /// can only sit on a message boundary, so an anchor lands on the message that spans it.
    /// </summary>
    private static int FindMessageEndingAtOrAfter(int[] blockEnds, int blockTarget)
    {
        for (var i = 0; i < blockEnds.Length; i++)
        {
            if (blockEnds[i] >= blockTarget)
                return i;
        }

        return -1;
    }

    /// <summary>
    /// Attaches a breakpoint to a message's last content block, wrapping plain string content into
    /// a typed block first. A message already carrying one is left alone rather than double-stamped.
    /// </summary>
    private static void StampMessage(
        Dictionary<string, object?> message, Dictionary<string, object?> cacheControl)
    {
        switch (message["content"])
        {
            case string textContent:
                message["content"] = new List<object>
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "text",
                        ["text"] = textContent,
                        ["cache_control"] = cacheControl
                    }
                };
                break;

            case List<object> { Count: > 0 } blocks
                when blocks[^1] is Dictionary<string, object?> lastBlock
                    && !lastBlock.ContainsKey("cache_control"):
                lastBlock["cache_control"] = cacheControl;
                break;
        }
    }

    internal static string ToClaudeCodeName(string name) =>
        ClaudeCodeToolLookup.TryGetValue(name, out var canonical) ? canonical : name;

    internal static string FromClaudeCodeName(string name, IReadOnlyList<Tool>? tools)
    {
        if (tools is { Count: > 0 })
        {
            var matched = tools.FirstOrDefault(tool => string.Equals(tool.Name, name, StringComparison.OrdinalIgnoreCase));
            if (matched is not null)
                return matched.Name;
        }

        return name;
    }

    /// <summary>
    /// Determines whether a thinking signature is from a non-Anthropic provider.
    /// Non-Anthropic providers store the field name (e.g. "reasoning_content") as the signature,
    /// whereas Anthropic uses long base64-encoded cryptographic signatures.
    /// </summary>
    internal static bool IsNonAnthropicSignature(string? signature) =>
        signature is not null && NonAnthropicSignatures.Contains(signature);

    internal static string NormalizeToolCallId(string id, LlmModel sourceModel, string targetProviderId)
    {
        return id.NormalizeToolCallId(64);
    }
}
