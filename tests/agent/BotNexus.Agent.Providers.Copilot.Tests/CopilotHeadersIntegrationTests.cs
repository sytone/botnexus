using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Utilities;

namespace BotNexus.Agent.Providers.Copilot.Tests;

public class CopilotHeadersIntegrationTests
{
    [Fact]
    public void XInitiator_SetToUser_ForUserInitiatedRequests()
    {
        var messages = new List<Message>
        {
            new UserMessage("Hello", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
        };

        var initiator = CopilotHeaders.InferInitiator(messages);

        initiator.ShouldBe("user");
    }

    [Fact]
    public void XInitiator_SetToAgent_ForAssistantInitiatedRequests()
    {
        var messages = new List<Message>
        {
            new UserMessage("Hi", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            new AssistantMessage(
                Content: [new TextContent("Hello!")],
                Api: "github-copilot",
                Provider: "github-copilot",
                ModelId: "gpt-4o",
                Usage: Usage.Empty(),
                StopReason: StopReason.Stop,
                ErrorMessage: null,
                ResponseId: null,
                Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            )
        };

        var initiator = CopilotHeaders.InferInitiator(messages);

        initiator.ShouldBe("agent");
    }

    [Fact]
    public void CopilotVisionRequest_SetWhenImagesPresent()
    {
        var messages = new List<Message>
        {
            new UserMessage(
                new UserMessageContent(new List<ContentBlock>
                {
                    new TextContent("What's in this image?"),
                    new ImageContent("base64data", "image/png")
                }),
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            )
        };

        var hasImages = CopilotHeaders.HasVisionInput(messages);
        var headers = CopilotHeaders.BuildDynamicHeaders(messages, hasImages);

        hasImages.ShouldBeTrue();
        headers.ShouldContainKey("Copilot-Vision-Request");
        headers["Copilot-Vision-Request"].ShouldBe("true");
    }

    [Fact]
    public void Headers_MergedCorrectly_WithModelHeaders()
    {
        var messages = new List<Message>
        {
            new UserMessage("Test", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
        };

        var hasImages = CopilotHeaders.HasVisionInput(messages);
        var dynamicHeaders = CopilotHeaders.BuildDynamicHeaders(messages, hasImages);

        var modelHeaders = new Dictionary<string, string>
        {
            ["X-Custom-Header"] = "custom-value",
            ["Openai-Organization"] = "org-123"
        };

        // Simulate merging as the provider does
        var merged = new Dictionary<string, string>(dynamicHeaders);
        foreach (var (key, value) in modelHeaders)
            merged[key] = value;

        merged.ShouldContainKey("X-Initiator");
        merged.ShouldContainKey("X-Custom-Header");
        merged.ShouldContainKey("Openai-Organization");
        merged["X-Initiator"].ShouldBe("user");
        merged["X-Custom-Header"].ShouldBe("custom-value");
    }
}
