using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Core.Loop;

using ProviderUserMessage = BotNexus.Agent.Providers.Core.Models.UserMessage;
using ProviderAssistantMessage = BotNexus.Agent.Providers.Core.Models.AssistantMessage;
using ProviderToolResultMessage = BotNexus.Agent.Providers.Core.Models.ToolResultMessage;
using AgentUserMessage = BotNexus.Agent.Core.Types.UserMessage;

/// <summary>
/// Converts between agent messages and provider messages.
/// </summary>
/// <remarks>
/// Handles UserMessage, AssistantAgentMessage, ToolResultAgentMessage conversions.
/// Parses image data URIs for multimodal content.
/// </remarks>
internal static class MessageConverter
{
    /// <summary>
    /// Convert agent messages to provider messages.
    /// </summary>
    /// <param name="agentMessages">The agent message timeline.</param>
    /// <returns>Provider-compatible Message[] for LLM invocation.</returns>
    public static IReadOnlyList<Message> ToProviderMessages(IReadOnlyList<AgentMessage> agentMessages)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var providerMessages = new List<Message>(agentMessages.Count);

        foreach (var message in agentMessages)
        {
            switch (message)
            {
                case AgentUserMessage user:
                    providerMessages.Add(ToProviderUserMessage(user, timestamp));
                    break;
                case SubAgentCompletionMessage completion:
                    providerMessages.Add(ToProviderSubAgentCompletionMessage(completion));
                    break;
                case AssistantAgentMessage assistant:
                    providerMessages.Add(ToProviderAssistantMessage(assistant, timestamp));
                    break;
                case ToolResultAgentMessage toolResult:
                    providerMessages.Add(ToToolResultMessage(toolResult));
                    break;
            }
        }

        return providerMessages;
    }

    /// <summary>
    /// Convert provider assistant message to agent assistant message.
    /// </summary>
    /// <param name="providerMessage">The provider assistant message.</param>
    /// <returns>An AgentAssistantMessage with accumulated content, tool calls, and usage.</returns>
    /// <remarks>
    /// Text blocks are concatenated with NO separator (#3425). A stream chunk boundary is transport
    /// metadata, not content: the provider may split a response anywhere, including mid-word, so any
    /// separator inserted between blocks is text the model never emitted.
    /// <para>
    /// This previously used <c>string.Join(Environment.NewLine, ...)</c>, which on Windows injected a
    /// literal <c>\r\n</c> between every text block and corrupted 1,033 persisted assistant messages
    /// across 15 agents into one-token-per-line output (<c>/pl</c> + <c>anning</c>). Mitm captures of
    /// the GitHub Copilot CLI against the identical endpoints show the wire carries no CR at all -
    /// 0 raw CR bytes across 3,025 provider deltas - which is why VS Code and the Copilot CLI never
    /// exhibited the symptom. The corruption was manufactured here, on our side of the parser, which
    /// is also why five successive transport-side CRLF strips never fixed it.
    /// </para>
    /// <para>
    /// Genuine model newlines arrive INSIDE a block's text as bare LF and are preserved verbatim.
    /// Never reintroduce a separator here, and never trim or whitespace-normalize a block: the only
    /// correct assembly of streamed text is exact ordered concatenation.
    /// </para>
    /// </remarks>
    public static AssistantAgentMessage ToAgentMessage(ProviderAssistantMessage providerMessage)
    {
        var text = string.Concat(
            providerMessage.Content
                .OfType<TextContent>()
                .Select(content => content.Text));

        var toolCalls = providerMessage.Content
            .OfType<ToolCallContent>()
            .ToList();

        var usage = providerMessage.Usage is null
            ? null
            : new AgentUsage(
                InputTokens: providerMessage.Usage.Input,
                OutputTokens: providerMessage.Usage.Output,
                CacheRead: providerMessage.Usage.CacheRead > 0 ? (int?)providerMessage.Usage.CacheRead : null,
                CacheWrite: providerMessage.Usage.CacheWrite > 0 ? (int?)providerMessage.Usage.CacheWrite : null);

        return new AssistantAgentMessage(
            Content: text,
            ToolCalls: toolCalls.Count > 0 ? toolCalls : null,
            FinishReason: providerMessage.StopReason,
            Usage: usage,
            ErrorMessage: providerMessage.ErrorMessage,
            Timestamp: DateTimeOffset.FromUnixTimeMilliseconds(providerMessage.Timestamp),
            ContentBlocks: providerMessage.Content.ToList());
    }

    /// <summary>
    /// Convert agent tool result to provider tool result message.
    /// </summary>
    /// <param name="agentResult">The agent tool result message.</param>
    /// <returns>A provider ToolResultMessage ready for LLM invocation.</returns>
    public static ProviderToolResultMessage ToToolResultMessage(ToolResultAgentMessage agentResult)
    {
        var blocks = agentResult.Result.Content
            .Select(ConvertToolContent)
            .ToList();

        return new ProviderToolResultMessage(
            ToolCallId: agentResult.ToolCallId,
            ToolName: agentResult.ToolName,
            Content: blocks,
            IsError: agentResult.IsError,
            Timestamp: (agentResult.Timestamp ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds());
    }

    private static ProviderUserMessage ToProviderUserMessage(AgentUserMessage user, long fallbackTimestamp)
    {
        if (user.Images is null || user.Images.Count == 0)
        {
            return new ProviderUserMessage(
                Content: new UserMessageContent(user.Content),
                Timestamp: fallbackTimestamp);
        }

        var blocks = new List<ContentBlock>(user.Images.Count + 1);
        if (!string.IsNullOrWhiteSpace(user.Content))
        {
            blocks.Add(new TextContent(user.Content));
        }

        foreach (var image in user.Images)
        {
            var (data, mimeType) = ParseImageValue(image.Value);
            blocks.Add(new ImageContent(data, mimeType));
        }

        return new ProviderUserMessage(
            Content: new UserMessageContent(blocks),
            Timestamp: fallbackTimestamp);
    }

    private static ProviderUserMessage ToProviderSubAgentCompletionMessage(SubAgentCompletionMessage completion)
    {
        return new ProviderUserMessage(
            Content: new UserMessageContent(completion.Content),
            Timestamp: completion.CompletedAt.ToUnixTimeMilliseconds());
    }

    private static ProviderAssistantMessage ToProviderAssistantMessage(AssistantAgentMessage assistant, long fallbackTimestamp)
    {
        var content = assistant.ContentBlocks is { Count: > 0 }
            ? assistant.ContentBlocks.ToList()
            : BuildAssistantContentBlocks(assistant);

        var usage = assistant.Usage is null
            ? Usage.Empty()
            : new Usage
            {
                Input = assistant.Usage.InputTokens ?? 0,
                Output = assistant.Usage.OutputTokens ?? 0,
                TotalTokens = (assistant.Usage.InputTokens ?? 0) + (assistant.Usage.OutputTokens ?? 0),
                CacheRead = assistant.Usage.CacheRead ?? 0,
                CacheWrite = assistant.Usage.CacheWrite ?? 0
            };

        return new ProviderAssistantMessage(
            Content: content,
            Api: "agent-core",
            Provider: "agent-core",
            ModelId: "agent-core",
            Usage: usage,
            StopReason: assistant.FinishReason,
            ErrorMessage: assistant.ErrorMessage,
            ResponseId: null,
            Timestamp: (assistant.Timestamp ?? DateTimeOffset.FromUnixTimeMilliseconds(fallbackTimestamp)).ToUnixTimeMilliseconds());
    }

    private static ContentBlock ConvertToolContent(AgentToolContent content)
    {
        return content.Type switch
        {
            AgentToolContentType.Image => CreateImageContent(content.Value),
            _ => new TextContent(content.Value)
        };
    }

    private static ImageContent CreateImageContent(string value)
    {
        var (data, mimeType) = ParseImageValue(value);
        return new ImageContent(data, mimeType);
    }

    private static (string Data, string MimeType) ParseImageValue(string value)
    {
        if (!value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return (value, "image/png");
        }

        var commaIndex = value.IndexOf(',');
        if (commaIndex < 0)
        {
            return (value, "image/png");
        }

        var prefix = value[..commaIndex];
        var mimeType = "image/png";
        var mediaTypePart = prefix["data:".Length..];
        var semicolonIndex = mediaTypePart.IndexOf(';');
        if (semicolonIndex >= 0)
        {
            mimeType = mediaTypePart[..semicolonIndex];
        }
        else if (!string.IsNullOrWhiteSpace(mediaTypePart))
        {
            mimeType = mediaTypePart;
        }

        return (value[(commaIndex + 1)..], mimeType);
    }

    private static List<ContentBlock> BuildAssistantContentBlocks(AssistantAgentMessage assistant)
    {
        var content = new List<ContentBlock>();
        if (!string.IsNullOrEmpty(assistant.Content))
        {
            content.Add(new TextContent(assistant.Content));
        }

        if (assistant.ToolCalls is { Count: > 0 })
        {
            content.AddRange(assistant.ToolCalls);
        }

        return content;
    }
}
