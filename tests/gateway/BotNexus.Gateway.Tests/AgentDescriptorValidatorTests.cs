using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Agents;

namespace BotNexus.Gateway.Tests;

public sealed class AgentDescriptorValidatorTests
{
    [Fact]
    public void Validate_WithValidDescriptor_ReturnsNoErrors()
    {
        var descriptor = CreateValidDescriptor();

        var errors = AgentDescriptorValidator.Validate(descriptor);

        errors.ShouldBeEmpty();
    }

    [Fact]
    public void Validate_WithoutAgentId_ReturnsAgentIdError()
    {
        var descriptor = CreateValidDescriptor() with { AgentId = default };

        var errors = AgentDescriptorValidator.Validate(descriptor);

        errors.ShouldContain("AgentId is required.");
    }

    [Fact]
    public void Validate_WithoutDisplayName_ReturnsDisplayNameError()
    {
        var descriptor = CreateValidDescriptor() with { DisplayName = string.Empty };

        var errors = AgentDescriptorValidator.Validate(descriptor);

        errors.ShouldContain("DisplayName is required.");
    }

    [Fact]
    public void Validate_WithoutModelId_ReturnsModelIdError()
    {
        var descriptor = CreateValidDescriptor() with { ModelId = string.Empty };

        var errors = AgentDescriptorValidator.Validate(descriptor);

        errors.ShouldContain("ModelId is required.");
    }

    [Fact]
    public void Validate_WithoutApiProvider_ReturnsApiProviderError()
    {
        var descriptor = CreateValidDescriptor() with { ApiProvider = string.Empty };

        var errors = AgentDescriptorValidator.Validate(descriptor);

        errors.ShouldContain("ApiProvider is required.");
    }

    [Fact]
    public void Validate_WithSystemPromptAndSystemPromptFile_ReturnsNoPromptErrors()
    {
        var descriptor = CreateValidDescriptor() with
        {
            SystemPrompt = "Prompt",
            SystemPromptFile = "prompt.txt"
        };

        var errors = AgentDescriptorValidator.Validate(descriptor);

        errors.ShouldNotContain(error => error.Contains("SystemPrompt", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_WithoutSystemPromptAndSystemPromptFile_ReturnsNoPromptErrors()
    {
        var descriptor = CreateValidDescriptor() with
        {
            SystemPrompt = null,
            SystemPromptFile = null
        };

        var errors = AgentDescriptorValidator.Validate(descriptor);

        errors.ShouldNotContain(error => error.Contains("SystemPrompt", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_WithNegativeMaxConcurrentSessions_ReturnsError()
    {
        var descriptor = CreateValidDescriptor() with { MaxConcurrentSessions = -1 };

        var errors = AgentDescriptorValidator.Validate(descriptor);

        errors.ShouldContain("MaxConcurrentSessions must be >= 0.");
    }

    [Fact]
    public void Validate_WithZeroMaxConcurrentSessions_ReturnsNoErrors()
    {
        var descriptor = CreateValidDescriptor() with { MaxConcurrentSessions = 0 };

        var errors = AgentDescriptorValidator.Validate(descriptor);

        errors.ShouldBeEmpty();
    }

    private static AgentDescriptor CreateValidDescriptor()
        => new()
        {
            AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a"),
            DisplayName = "Agent A",
            ModelId = "model",
            ApiProvider = "provider",
            SystemPrompt = "Prompt",
            MaxConcurrentSessions = 1
        };
}
