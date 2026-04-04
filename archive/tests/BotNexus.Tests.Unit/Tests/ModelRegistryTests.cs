using BotNexus.Providers.Base;
using FluentAssertions;

namespace BotNexus.Tests.Unit.Tests;

public class ModelRegistryTests
{
    [Theory]
    [InlineData("claude-opus-4.6", "anthropic-messages")]
    [InlineData("claude-sonnet-4.6", "anthropic-messages")]
    [InlineData("claude-sonnet-4.5", "anthropic-messages")]
    [InlineData("claude-sonnet-4", "anthropic-messages")]
    [InlineData("claude-opus-4.5", "anthropic-messages")]
    [InlineData("claude-haiku-4.5", "anthropic-messages")]
    public void ClaudeModels_ResolveToAnthropicMessages(string modelId, string expectedApi)
    {
        var model = CopilotModels.Resolve(modelId);
        
        model.Should().NotBeNull();
        model.Id.Should().Be(modelId);
        model.Api.Should().Be(expectedApi);
        model.Provider.Should().Be("github-copilot");
    }
    
    [Theory]
    [InlineData("gpt-4o", "openai-completions")]
    [InlineData("gpt-4o-mini", "openai-completions")]
    [InlineData("gpt-4.1", "openai-completions")]
    [InlineData("o1", "openai-completions")]
    [InlineData("o1-mini", "openai-completions")]
    [InlineData("o3", "openai-completions")]
    [InlineData("o3-mini", "openai-completions")]
    [InlineData("o4-mini", "openai-completions")]
    public void GPT4Models_ResolveToOpenAiCompletions(string modelId, string expectedApi)
    {
        var model = CopilotModels.Resolve(modelId);
        
        model.Should().NotBeNull();
        model.Id.Should().Be(modelId);
        model.Api.Should().Be(expectedApi);
        model.Provider.Should().Be("github-copilot");
    }
    
    [Theory]
    [InlineData("gpt-5", "openai-responses")]
    [InlineData("gpt-5-mini", "openai-responses")]
    [InlineData("gpt-5.1", "openai-responses")]
    [InlineData("gpt-5.2", "openai-responses")]
    [InlineData("gpt-5.2-codex", "openai-responses")]
    [InlineData("gpt-5.4", "openai-responses")]
    [InlineData("gpt-5.4-mini", "openai-responses")]
    public void GPT5Models_ResolveToOpenAiResponses(string modelId, string expectedApi)
    {
        var model = CopilotModels.Resolve(modelId);
        
        model.Should().NotBeNull();
        model.Id.Should().Be(modelId);
        model.Api.Should().Be(expectedApi);
        model.Provider.Should().Be("github-copilot");
    }
    
    [Theory]
    [InlineData("gemini-2.5-pro", "openai-completions")]
    [InlineData("gemini-3-flash-preview", "openai-completions")]
    [InlineData("gemini-3-pro-preview", "openai-completions")]
    [InlineData("gemini-3.1-pro-preview", "openai-completions")]
    public void GeminiModels_ResolveToOpenAiCompletions(string modelId, string expectedApi)
    {
        var model = CopilotModels.Resolve(modelId);
        
        model.Should().NotBeNull();
        model.Id.Should().Be(modelId);
        model.Api.Should().Be(expectedApi);
        model.Provider.Should().Be("github-copilot");
    }
    
    [Fact]
    public void UnknownModel_ThrowsArgumentException()
    {
        var act = () => CopilotModels.Resolve("unknown-model-xyz");
        
        act.Should().Throw<ArgumentException>()
            .WithMessage("Unknown model ID: unknown-model-xyz*");
    }
    
    [Fact]
    public void TryResolve_UnknownModel_ReturnsFalse()
    {
        var result = CopilotModels.TryResolve("unknown-model-xyz", out var model);
        
        result.Should().BeFalse();
        model.Should().BeNull();
    }
    
    [Fact]
    public void TryResolve_KnownModel_ReturnsTrue()
    {
        var result = CopilotModels.TryResolve("claude-opus-4.6", out var model);
        
        result.Should().BeTrue();
        model.Should().NotBeNull();
        model!.Id.Should().Be("claude-opus-4.6");
    }
    
    [Theory]
    [InlineData("claude-opus-4.6", 1000000)]
    [InlineData("claude-sonnet-4.5", 200000)]
    [InlineData("gpt-4o", 128000)]
    [InlineData("o1", 200000)]
    [InlineData("gpt-5.2", 200000)]
    [InlineData("gemini-2.5-pro", 1000000)]
    [InlineData("gemini-3-pro-preview", 2000000)]
    public void Models_HaveCorrectContextWindow(string modelId, int expectedContextWindow)
    {
        var model = CopilotModels.Resolve(modelId);
        
        model.ContextWindow.Should().Be(expectedContextWindow);
    }
    
    [Theory]
    [InlineData("claude-opus-4.6", true)]
    [InlineData("claude-sonnet-4.6", true)]
    [InlineData("claude-sonnet-4.5", false)]
    [InlineData("o1", true)]
    [InlineData("o3-mini", true)]
    [InlineData("gpt-4o", false)]
    [InlineData("gpt-5.2", false)]
    public void Models_HaveCorrectReasoningCapability(string modelId, bool expectedReasoning)
    {
        var model = CopilotModels.Resolve(modelId);
        
        model.Reasoning.Should().Be(expectedReasoning);
    }
    
    [Theory]
    [InlineData("claude-opus-4.6", new[] { "text", "image" })]
    [InlineData("gpt-4o", new[] { "text", "image" })]
    [InlineData("o1-mini", new[] { "text" })]
    [InlineData("gpt-5.2", new[] { "text" })]
    [InlineData("gemini-2.5-pro", new[] { "text", "image" })]
    public void Models_HaveCorrectInputTypes(string modelId, string[] expectedInput)
    {
        var model = CopilotModels.Resolve(modelId);
        
        model.Input.Should().BeEquivalentTo(expectedInput);
    }
    
    [Fact]
    public void AllModels_HaveCopilotHeaders()
    {
        foreach (var model in CopilotModels.All)
        {
            model.Headers.Should().NotBeNull();
            model.Headers.Should().ContainKey("User-Agent");
            model.Headers.Should().ContainKey("Editor-Version");
            model.Headers.Should().ContainKey("Copilot-Integration-Id");
        }
    }
    
    [Fact]
    public void AllModels_HaveCopilotBaseUrl()
    {
        foreach (var model in CopilotModels.All)
        {
            model.BaseUrl.Should().Be("https://api.individual.githubcopilot.com");
        }
    }
    
    [Fact]
    public void ModelResolution_IsCaseInsensitive()
    {
        var lower = CopilotModels.Resolve("claude-opus-4.6");
        var upper = CopilotModels.Resolve("CLAUDE-OPUS-4.6");
        var mixed = CopilotModels.Resolve("Claude-Opus-4.6");
        
        lower.Should().Be(upper);
        lower.Should().Be(mixed);
    }
}
