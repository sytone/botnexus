using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Core.Configuration;

using AgentUserMessage = BotNexus.Agent.Core.Types.UserMessage;
using ProviderUserMessage = BotNexus.Agent.Providers.Core.Models.UserMessage;

/// <summary>
/// Default conversion from agent timeline messages to provider messages.
/// </summary>
public static class DefaultMessageConverter
{
    /// <summary>
    /// Executes create.
    /// </summary>
    /// <returns>The create result.</returns>
    public static ConvertToLlmDelegate Create() => ConvertToLlm;

    /// <summary>
    /// Executes convert to llm.
    /// </summary>
    /// <param name="messages">The messages.</param>
    /// <returns>The convert to llm result.</returns>
    public static Task<IReadOnlyList<Message>> ConvertToLlm(
        IReadOnlyList<AgentMessage> messages,
        CancellationToken cancellationToken)
    {
        if (messages is null || messages.Count == 0)
            return Task.FromResult<IReadOnlyList<Message>>([]);

        var filtered = messages
            .Where(message => message?.Role is "user" or "assistant" or "tool")
            .ToList();

        var converted = filtered
            .Select(ToProviderMessage)
            .OfType<Message>()
            .ToList();

        return Task.FromResult<IReadOnlyList<Message>>(converted);
    }

    private static Message? ToProviderMessage(AgentMessage message)
    {
        return message switch
        {
            AgentUserMessage user => ToProviderUserMessage(user),
            SubAgentCompletionMessage completion => ToProviderSubAgentCompletionMessage(completion),
            AssistantAgentMessage assistant => ToProviderAssistantMessage(assistant),
            ToolResultAgentMessage toolResult => ToProviderToolResultMessage(toolResult),
            _ => null
        };
    }

    private static Message ToProviderUserMessage(AgentUserMessage user)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (user.Images is null || user.Images.Count == 0)
        {
            return new ProviderUserMessage(
                Content: new UserMessageContent(user.Content),
                Timestamp: timestamp);
        }

        var blocks = new List<ContentBlock>(user.Images.Count + 1);
        if (!string.IsNullOrWhiteSpace(user.Content))
            blocks.Add(new TextContent(user.Content));

        foreach (var image in user.Images)
        {
            var (data, mimeType) = ParseImageValue(image.Value);
            blocks.Add(new ImageContent(data, mimeType));
        }

        return new ProviderUserMessage(
            Content: new UserMessageContent(blocks),
            Timestamp: timestamp);
    }

    private static Message ToProviderSubAgentCompletionMessage(SubAgentCompletionMessage completion)
    {
        return new ProviderUserMessage(
            Content: new UserMessageContent(completion.Content),
            Timestamp: completion.CompletedAt.ToUnixTimeMilliseconds());
    }

    private static Message ToProviderAssistantMessage(AssistantAgentMessage assistant)
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
                TotalTokens = (assistant.Usage.InputTokens ?? 0) + (assistant.Usage.OutputTokens ?? 0)
            };

        return new AssistantMessage(
            Content: content,
            Api: "agent-core",
            Provider: "agent-core",
            ModelId: "agent-core",
            Usage: usage,
            StopReason: assistant.FinishReason,
            ErrorMessage: assistant.ErrorMessage,
            ResponseId: null,
            Timestamp: (assistant.Timestamp ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds());
    }

    private static Message ToProviderToolResultMessage(ToolResultAgentMessage toolResult)
    {
        var blocks = toolResult.Result.Content
            .Select(content => content.Type == AgentToolContentType.Image
                ? (ContentBlock)CreateImageContent(content.Value)
                : new TextContent(content.Value))
            .ToList();

        return new ToolResultMessage(
            ToolCallId: toolResult.ToolCallId,
            ToolName: toolResult.ToolName,
            Content: blocks,
            IsError: toolResult.IsError,
            Timestamp: (toolResult.Timestamp ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds());
    }

    private static ImageContent CreateImageContent(string value)
    {
        var (data, mimeType) = ParseImageValue(value);
        return new ImageContent(data, mimeType);
    }

    private static (string Data, string MimeType) ParseImageValue(string value)
    {
        if (!value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return (value, "image/png");

        var commaIndex = value.IndexOf(',');
        if (commaIndex < 0)
            return (value, "image/png");

        var prefix = value[..commaIndex];
        var mimeType = "image/png";
        var mediaTypePart = prefix["data:".Length..];
        var semicolonIndex = mediaTypePart.IndexOf(';');
        if (semicolonIndex >= 0)
            mimeType = mediaTypePart[..semicolonIndex];
        else if (!string.IsNullOrWhiteSpace(mediaTypePart))
            mimeType = mediaTypePart;

        return (value[(commaIndex + 1)..], mimeType);
    }

    private static List<ContentBlock> BuildAssistantContentBlocks(AssistantAgentMessage assistant)
    {
        var content = new List<ContentBlock>();
        if (!string.IsNullOrEmpty(assistant.Content))
            content.Add(new TextContent(assistant.Content));

        if (assistant.ToolCalls is { Count: > 0 })
            content.AddRange(assistant.ToolCalls);

        return content;
    }
}
