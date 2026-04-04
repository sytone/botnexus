using BotNexus.AgentCore.Types;
using BotNexus.Providers.Core.Models;

namespace BotNexus.AgentCore.Loop;

using ProviderUserMessage = BotNexus.Providers.Core.Models.UserMessage;
using ProviderAssistantMessage = BotNexus.Providers.Core.Models.AssistantMessage;
using ProviderToolResultMessage = BotNexus.Providers.Core.Models.ToolResultMessage;
using AgentUserMessage = BotNexus.AgentCore.Types.UserMessage;

internal static class MessageConverter
{
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

    public static AssistantAgentMessage ToAgentMessage(ProviderAssistantMessage providerMessage)
    {
        var text = string.Join(
            Environment.NewLine,
            providerMessage.Content
                .OfType<TextContent>()
                .Select(content => content.Text));

        var toolCalls = providerMessage.Content
            .OfType<ToolCallContent>()
            .ToList();

        var usage = providerMessage.Usage is null
            ? null
            : new AgentUsage(providerMessage.Usage.Input, providerMessage.Usage.Output);

        return new AssistantAgentMessage(
            Content: text,
            ToolCalls: toolCalls.Count > 0 ? toolCalls : null,
            FinishReason: providerMessage.StopReason,
            Usage: usage,
            ErrorMessage: providerMessage.ErrorMessage,
            Timestamp: DateTimeOffset.FromUnixTimeMilliseconds(providerMessage.Timestamp));
    }

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

    private static ProviderAssistantMessage ToProviderAssistantMessage(AssistantAgentMessage assistant, long fallbackTimestamp)
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

        var usage = Usage.Empty();
        if (assistant.Usage is not null)
        {
            usage.Input = assistant.Usage.InputTokens ?? 0;
            usage.Output = assistant.Usage.OutputTokens ?? 0;
            usage.TotalTokens = usage.Input + usage.Output;
        }

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
}
