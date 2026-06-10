using BotNexus.Agent.Providers.Copilot;
using BotNexus.Agent.Providers.Copilot.Discovery;

namespace BotNexus.Gateway.Tests.Models;

public sealed class CopilotModelDiscoveryProviderTests
{
    [Theory]
    [InlineData("claude-sonnet-4.6", "claude", "Anthropic", "github-copilot-messages")]
    [InlineData("claude-opus-4.8", "claude", "Anthropic", "github-copilot-messages")]
    [InlineData("claude-haiku-4.5", "claude", "Anthropic", "github-copilot-messages")]
    [InlineData("gpt-5", "gpt", "OpenAI", "github-copilot-responses")]
    [InlineData("gpt-5.2", "gpt", "OpenAI", "github-copilot-responses")]
    [InlineData("gpt-5.4-mini", "gpt", "OpenAI", "github-copilot-responses")]
    [InlineData("gpt-4.1", "gpt", "OpenAI", "github-copilot-completions")]
    [InlineData("gpt-4o", "gpt", "OpenAI", "github-copilot-completions")]
    [InlineData("gemini-2.5-pro", "gemini", "Google", "github-copilot-completions")]
    [InlineData("gemini-3-flash-preview", "gemini", "Google", "github-copilot-completions")]
    [InlineData("o3", "o3", "OpenAI", "github-copilot-responses")]
    [InlineData("o4-mini", "o4", "OpenAI", "github-copilot-responses")]
    [InlineData("grok-code-fast-1", "grok", "xAI", "github-copilot-completions")]
    public void ResolveApiFormat_MapsCorrectly(string modelId, string family, string vendor, string expectedApi)
    {
        var result = CopilotModelDiscoveryProvider.ResolveApiFormat(modelId, family, vendor);
        result.ShouldBe(expectedApi);
    }

    [Theory]
    [InlineData("claude-opus-4.6", "claude", true)]
    [InlineData("claude-sonnet-4.6", "claude", true)]
    [InlineData("claude-haiku-4.5", "claude", false)]
    [InlineData("gpt-5", "gpt", true)]
    [InlineData("gpt-5.2", "gpt", true)]
    [InlineData("gpt-4.1", "gpt", false)]
    [InlineData("gpt-4o", "gpt", false)]
    [InlineData("gemini-3-flash-preview", "gemini", true)]
    [InlineData("gemini-2.5-pro", "gemini", false)]
    [InlineData("o3", "o3", true)]
    [InlineData("o4-mini", "o4", true)]
    [InlineData("grok-code-fast-1", "grok", true)]
    public void IsReasoningModel_CorrectlyIdentifies(string modelId, string family, bool expected)
    {
        var result = CopilotModelDiscoveryProvider.IsReasoningModel(modelId, family);
        result.ShouldBe(expected);
    }

    [Theory]
    [InlineData("claude-opus-4.6", "claude", true)]
    [InlineData("claude-opus-4.8", "claude", true)]
    [InlineData("claude-opus-4.5", "claude", false)]
    [InlineData("claude-sonnet-4.6", "claude", false)]
    [InlineData("gpt-5.2", "gpt", true)]
    [InlineData("gpt-5.4", "gpt", true)]
    [InlineData("gpt-5.1", "gpt", false)]
    [InlineData("gpt-5", "gpt", false)]
    [InlineData("gpt-4.1", "gpt", false)]
    public void SupportsExtraHighThinking_CorrectlyIdentifies(string modelId, string family, bool expected)
    {
        var result = CopilotModelDiscoveryProvider.SupportsExtraHighThinking(modelId, family);
        result.ShouldBe(expected);
    }

    [Fact]
    public void MapToLlmModel_MapsAllFields()
    {
        // Arrange
        var info = new CopilotModelInfo
        {
            Id = "claude-sonnet-4.6",
            Name = "Claude Sonnet 4.6",
            Vendor = "Anthropic",
            Capabilities = new CopilotModelCapabilities
            {
                Family = "claude",
                Supports = new CopilotModelSupports { Vision = true, ToolCalls = true, Streaming = true },
                Limits = new Dictionary<string, System.Text.Json.JsonElement>
                {
                    ["max_prompt_tokens"] = System.Text.Json.JsonDocument.Parse("1000000").RootElement,
                    ["max_output_tokens"] = System.Text.Json.JsonDocument.Parse("32000").RootElement
                }
            }
        };

        // Act
        var model = CopilotModelDiscoveryProvider.MapToLlmModel(info);

        // Assert
        model.ShouldNotBeNull();
        model.Id.ShouldBe("claude-sonnet-4.6");
        model.Name.ShouldBe("Claude Sonnet 4.6");
        model.Api.ShouldBe("github-copilot-messages");
        model.Provider.ShouldBe("github-copilot");
        model.Reasoning.ShouldBeTrue();
        model.Input.ShouldContain("text");
        model.Input.ShouldContain("image");
        model.ContextWindow.ShouldBe(1000000);
        model.MaxTokens.ShouldBe(32000);
        model.SupportsExtraHighThinking.ShouldBe(false);
        model.Headers.ShouldNotBeNull();
    }

    [Fact]
    public void MapToLlmModel_NoVision_TextOnly()
    {
        // Arrange
        var info = new CopilotModelInfo
        {
            Id = "gpt-4.1",
            Name = "GPT-4.1",
            Vendor = "OpenAI",
            Capabilities = new CopilotModelCapabilities
            {
                Family = "gpt",
                Supports = new CopilotModelSupports { Vision = false }
            }
        };

        // Act
        var model = CopilotModelDiscoveryProvider.MapToLlmModel(info);

        // Assert
        model.ShouldNotBeNull();
        model.Input.ShouldBe(new[] { "text" });
    }

    [Fact]
    public void MapToLlmModel_NullCapabilities_UsesDefaults()
    {
        // Arrange
        var info = new CopilotModelInfo
        {
            Id = "unknown-model",
            Name = "Unknown"
        };

        // Act
        var model = CopilotModelDiscoveryProvider.MapToLlmModel(info);

        // Assert
        model.ShouldNotBeNull();
        model.ContextWindow.ShouldBe(128000); // default
        model.MaxTokens.ShouldBe(32000); // default
        model.Input.ShouldBe(new[] { "text" });
    }

    [Fact]
    public void MapToLlmModel_NullId_ReturnsNull()
    {
        var info = new CopilotModelInfo { Id = null };
        CopilotModelDiscoveryProvider.MapToLlmModel(info).ShouldBeNull();
    }

    [Fact]
    public void MapToLlmModel_EmptyId_ReturnsNull()
    {
        var info = new CopilotModelInfo { Id = "   " };
        CopilotModelDiscoveryProvider.MapToLlmModel(info).ShouldBeNull();
    }

    [Fact]
    public void MapToLlmModel_CompletionsApi_SetsCompat()
    {
        // Arrange
        var info = new CopilotModelInfo
        {
            Id = "gpt-4o",
            Name = "GPT-4o",
            Vendor = "OpenAI",
            Capabilities = new CopilotModelCapabilities { Family = "gpt" }
        };

        // Act
        var model = CopilotModelDiscoveryProvider.MapToLlmModel(info);

        // Assert
        model.ShouldNotBeNull();
        model.Api.ShouldBe("github-copilot-completions");
        model.Compat.ShouldNotBeNull();
    }

    [Fact]
    public void MapToLlmModel_MessagesApi_NoCompat()
    {
        // Arrange
        var info = new CopilotModelInfo
        {
            Id = "claude-sonnet-4.6",
            Vendor = "Anthropic",
            Capabilities = new CopilotModelCapabilities { Family = "claude" }
        };

        // Act
        var model = CopilotModelDiscoveryProvider.MapToLlmModel(info);

        // Assert
        model.ShouldNotBeNull();
        model.Api.ShouldBe("github-copilot-messages");
        model.Compat.ShouldBeNull();
    }
}
