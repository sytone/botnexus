using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Extensions.Skills;
using BotNexus.Gateway.Abstractions.Models;
using Shouldly;

namespace BotNexus.Extensions.Skills.Tests;

public sealed class SkillsConfigResolverTests
{
    [Fact]
    public void AgentDefaultFalse_DisablesSharedManagementForInheritingAgent()
    {
        var resolved = SkillsConfigResolver.Resolve(Descriptor(defaultJson: """{"allowSharedSkillManagement":false}"""));

        resolved.ShouldNotBeNull();
        resolved.AllowSharedSkillManagement.ShouldBeFalse();
    }

    [Fact]
    public void NamedFalse_OverridesAgentDefaultTrue()
    {
        var resolved = SkillsConfigResolver.Resolve(Descriptor(
            namedJson: """{"allowSharedSkillManagement":false}""",
            defaultJson: """{"allowSharedSkillManagement":true}"""));

        resolved.ShouldNotBeNull();
        resolved.AllowSharedSkillManagement.ShouldBeFalse();
    }

    [Fact]
    public void NamedPartialConfig_InheritsAgentDefaultFalse()
    {
        var resolved = SkillsConfigResolver.Resolve(Descriptor(
            namedJson: """{"maxLoadedSkills":7}""",
            defaultJson: """{"allowSharedSkillManagement":false}"""));

        resolved.ShouldNotBeNull();
        resolved.MaxLoadedSkills.ShouldBe(7);
        resolved.AllowSharedSkillManagement.ShouldBeFalse();
    }

    [Fact]
    public void NamedTrue_OverridesAgentDefaultFalse()
    {
        var resolved = SkillsConfigResolver.Resolve(Descriptor(
            namedJson: """{"allowSharedSkillManagement":true}""",
            defaultJson: """{"allowSharedSkillManagement":false}"""));

        resolved.ShouldNotBeNull();
        resolved.AllowSharedSkillManagement.ShouldBeTrue();
    }

    private static AgentDescriptor Descriptor(string? namedJson = null, string? defaultJson = null)
        => new()
        {
            AgentId = AgentId.From("test-agent"),
            DisplayName = "Test Agent",
            ModelId = "model",
            ApiProvider = "provider",
            ExtensionConfig = Bag(namedJson),
            DefaultExtensionConfig = Bag(defaultJson)
        };

    private static IReadOnlyDictionary<string, JsonElement> Bag(string? json)
    {
        if (json is null)
            return new Dictionary<string, JsonElement>();

        using var document = JsonDocument.Parse(json);
        return new Dictionary<string, JsonElement>
        {
            [SkillsExtensionJson.ExtensionId] = document.RootElement.Clone()
        };
    }
}
